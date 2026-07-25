using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Contracts;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;
using ProxySiu.Api.Storage;

namespace ProxySiu.Api.Services;

public sealed class ProxyPoolService
{
    private const int RandomProxyCandidateCount = 30;
    private readonly JsonProxyStore _store;
    private readonly ProxyListParser _parser;
    private readonly ProxyChecker _checker;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProxyPoolProfileManager _profileManager;
    private readonly ILogger<ProxyPoolService> _logger;
    private readonly GeoIpService? _geoIp;
    private int _isScanning;
    private int _isChecking;
    private int _isPruning;
    private int _checkTotal;
    private int _checkCompleted;
    private int _checkInFlight;
    private int _checkAlive;
    private int _checkFailed;
    private long _checkStartedAtUnixMilliseconds;
    private long _checkFinishedAtUnixMilliseconds;
    private long _nextCheckAtUnixMilliseconds;
    private DateTimeOffset? _lastScanAt;
    private DateTimeOffset? _lastCheckAt;
    private DateTimeOffset? _lastPruneAt;
    private string? _lastMessage;
    private ProxyPoolOptions Options => _profileManager.Current;

    public ProxyPoolService(
        JsonProxyStore store,
        ProxyListParser parser,
        ProxyChecker checker,
        IHttpClientFactory httpClientFactory,
        ProxyPoolProfileManager profileManager,
        ILogger<ProxyPoolService> logger,
        GeoIpService? geoIp = null)
    {
        _store = store;
        _parser = parser;
        _checker = checker;
        _httpClientFactory = httpClientFactory;
        _profileManager = profileManager;
        _logger = logger;
        _geoIp = geoIp;
    }

    public void SetNextCheckAt(DateTimeOffset value) =>
        Interlocked.Exchange(ref _nextCheckAtUnixMilliseconds, value.ToUnixTimeMilliseconds());

    public Task<DashboardDto> GetDashboardAsync(CancellationToken cancellationToken) =>
        _store.ReadAsync(state =>
        {
            var alive = state.Proxies.Count(proxy => proxy.Status == ProxyStatus.Alive);
            var dead = state.Proxies.Count(proxy => proxy.Status == ProxyStatus.Dead);
            var pending = state.Proxies.Count(proxy => proxy.Status == ProxyStatus.Pending);
            var protocols = Enum.GetValues<ProxyProtocol>().Select(protocol =>
            {
                var items = state.Proxies.Where(proxy => proxy.Protocol == protocol).ToList();
                return new ProtocolSummaryDto(protocol, items.Count,
                    items.Count(proxy => proxy.Status == ProxyStatus.Alive),
                    items.Count(proxy => proxy.Status == ProxyStatus.Dead),
                    items.Count(proxy => proxy.Status == ProxyStatus.Pending));
            }).ToList();
            var latencies = state.Proxies.Where(proxy => proxy.Status == ProxyStatus.Alive && proxy.LatencyMs.HasValue)
                .Select(proxy => proxy.LatencyMs!.Value).ToList();
            return new DashboardDto(
                state.Proxies.Count,
                alive,
                dead,
                pending,
                state.Proxies.Count == 0 ? 0 : Math.Round(alive * 100d / state.Proxies.Count, 2),
                latencies.Count == 0 ? null : (long)Math.Round(latencies.Average()),
                state.Sources.Count,
                state.Sources.Count(source => source.Enabled),
                protocols,
                GetOperationState(),
                state.UpdatedAt);
        }, cancellationToken);

    public Task<PagedResult<ProxyDto>> GetProxiesAsync(ProxyQuery query, CancellationToken cancellationToken) =>
        _store.ReadAsync(state =>
        {
            IEnumerable<ProxyRecord> records = state.Proxies;
            if (!string.IsNullOrWhiteSpace(query.Q))
            {
                records = records.Where(proxy => proxy.Host.Contains(query.Q.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            }

            if (Enum.TryParse<ProxyStatus>(query.Status, true, out var status))
            {
                records = records.Where(proxy => proxy.Status == status);
            }

            if (TryParseProtocol(query.Protocol, out var protocol))
            {
                records = records.Where(proxy => proxy.Protocol == protocol);
            }

            if (!string.IsNullOrWhiteSpace(query.Country))
            {
                records = records.Where(proxy => string.Equals(proxy.GeoLocation?.CountryCode, query.Country,
                    StringComparison.OrdinalIgnoreCase));
            }

            records = (query.Sort?.ToLowerInvariant(), query.Desc ?? true) switch
            {
                ("address", false) => records.OrderBy(proxy => proxy.Host, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(proxy => proxy.Port),
                ("address", true) => records.OrderByDescending(proxy => proxy.Host, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(proxy => proxy.Port),
                ("protocol", false) => records.OrderBy(proxy => proxy.Protocol)
                    .ThenBy(proxy => proxy.Host, StringComparer.OrdinalIgnoreCase),
                ("protocol", true) => records.OrderByDescending(proxy => proxy.Protocol)
                    .ThenBy(proxy => proxy.Host, StringComparer.OrdinalIgnoreCase),
                ("status", false) => records.OrderBy(proxy => StatusRank(proxy.Status))
                    .ThenBy(proxy => proxy.LatencyMs ?? long.MaxValue),
                ("status", true) => records.OrderByDescending(proxy => StatusRank(proxy.Status))
                    .ThenBy(proxy => proxy.LatencyMs ?? long.MaxValue),
                ("latency", false) => records.OrderBy(proxy => !proxy.LatencyMs.HasValue)
                    .ThenBy(proxy => proxy.LatencyMs),
                ("latency", true) => records.OrderBy(proxy => !proxy.LatencyMs.HasValue)
                    .ThenByDescending(proxy => proxy.LatencyMs),
                ("successrate", false) => records.OrderBy(proxy => proxy.SuccessCount + proxy.FailureCount == 0)
                    .ThenBy(SuccessRate),
                ("successrate", true) => records.OrderBy(proxy => proxy.SuccessCount + proxy.FailureCount == 0)
                    .ThenByDescending(SuccessRate),
                ("lastchecked", false) => records.OrderBy(proxy => !proxy.LastCheckedAt.HasValue)
                    .ThenBy(proxy => proxy.LastCheckedAt),
                ("lastchecked", true) => records.OrderBy(proxy => !proxy.LastCheckedAt.HasValue)
                    .ThenByDescending(proxy => proxy.LastCheckedAt),
                ("firstseen", false) => records.OrderBy(proxy => proxy.FirstSeenAt),
                ("firstseen", true) => records.OrderByDescending(proxy => proxy.FirstSeenAt),
                _ => records.OrderBy(proxy => proxy.Status).ThenBy(proxy => proxy.LatencyMs ?? long.MaxValue)
            };

            var list = records.ToList();
            var pageSize = Math.Clamp(query.PageSize ?? 30, 10, 200);
            var page = Math.Max(query.Page ?? 1, 1);
            var sourceNames = state.Sources.ToDictionary(source => source.Id, source => source.Name);
            var items = list.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(proxy => ToDto(proxy, sourceNames)).ToList();
            return new PagedResult<ProxyDto>(items, list.Count, page, pageSize);
        }, cancellationToken);

    public Task<IReadOnlyList<SourceDto>> GetSourcesAsync(CancellationToken cancellationToken) =>
        _store.ReadAsync<IReadOnlyList<SourceDto>>(state => state.Sources
            .OrderByDescending(source => source.Enabled)
            .ThenBy(source => source.Name)
            .Select(ToDto)
            .ToList(), cancellationToken);

    public async Task<ProxyDto> AddProxyAsync(ProxyCreateRequest request, CancellationToken cancellationToken)
    {
        var host = request.Host.Trim().Trim('[', ']');
        if (host.Length is 0 or > 255 || host.Any(char.IsWhiteSpace) || request.Port is < 1 or > 65535)
        {
            throw new ArgumentException("请输入有效的代理地址和端口。", nameof(request));
        }

        if (!Options.AllowPrivateNetworks && !await EndpointSafety.IsPublicHostAsync(host, cancellationToken))
        {
            throw new ArgumentException("默认只允许可公开路由的代理地址；如确需内网代理，请开启 AllowPrivateNetworks。",
                nameof(request));
        }

        return await _store.WriteAsync(state =>
        {
            var key = $"{request.Protocol}:{host.ToLowerInvariant()}:{request.Port}";
            var proxy = state.Proxies.FirstOrDefault(item => item.Key == key);
            if (proxy is null)
            {
                proxy = new ProxyRecord
                {
                    Host = host,
                    Port = request.Port,
                    Protocol = request.Protocol,
                    IsPinned = request.IsPinned
                };
                state.Proxies.Add(proxy);
            }
            else
            {
                proxy.IsPinned = request.IsPinned || proxy.IsPinned;
                proxy.LastSeenAt = DateTimeOffset.UtcNow;
            }

            return ToDto(proxy, state.Sources.ToDictionary(source => source.Id, source => source.Name));
        }, cancellationToken);
    }

    public Task<bool> DeleteProxyAsync(Guid id, CancellationToken cancellationToken) =>
        _store.WriteAsync(state => state.Proxies.RemoveAll(proxy => proxy.Id == id) > 0, cancellationToken);

    public async Task<ProxyDto?> CheckProxyAsync(Guid id, CancellationToken cancellationToken)
    {
        var proxy = await _store.ReadAsync(state => state.Proxies.Where(item => item.Id == id)
            .Select(CloneProxy).FirstOrDefault(), cancellationToken);
        if (proxy is null)
        {
            return null;
        }

        var result = await _checker.CheckAsync(proxy, cancellationToken);
        return await _store.WriteAsync(state =>
        {
            var current = state.Proxies.FirstOrDefault(item => item.Id == id);
            if (current is null)
            {
                return null;
            }

            ApplyCheckResult(current, result);
            return ToDto(current, state.Sources.ToDictionary(source => source.Id, source => source.Name));
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CountrySummaryDto>> GetAliveCountriesAsync(string? protocolValue,
        CancellationToken cancellationToken) =>
        _store.ReadAsync<IReadOnlyList<CountrySummaryDto>>(state =>
        {
            IEnumerable<ProxyRecord> records = state.Proxies.Where(proxy => proxy.Status == ProxyStatus.Alive &&
                !string.IsNullOrWhiteSpace(proxy.GeoLocation?.CountryCode));
            if (TryParseProtocol(protocolValue, out var protocol))
            {
                records = records.Where(proxy => proxy.Protocol == protocol);
            }

            return records.GroupBy(proxy => proxy.GeoLocation!.CountryCode!.ToUpperInvariant())
                .Select(group => new CountrySummaryDto(group.Key,
                    group.Select(proxy => proxy.GeoLocation!.CountryName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                    group.Count()))
                .OrderBy(country => country.Name)
                .ToList();
        }, cancellationToken);

    public async Task<ProxyDto?> GetRandomAliveProxyAsync(string? protocolValue, string? countryCode,
        CancellationToken cancellationToken) =>
        (await GetRandomAliveProxiesAsync(protocolValue, countryCode, 1, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<ProxyDto>> GetRandomAliveProxiesAsync(string? protocolValue, string? countryCode,
        int count, CancellationToken cancellationToken) =>
        _store.ReadAsync(state =>
        {
            IEnumerable<ProxyRecord> records = state.Proxies.Where(proxy => proxy.Status == ProxyStatus.Alive);
            if (TryParseProtocol(protocolValue, out var protocol))
            {
                records = records.Where(proxy => proxy.Protocol == protocol);
            }
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                records = records.Where(proxy => string.Equals(proxy.GeoLocation?.CountryCode, countryCode,
                    StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                records = records.Where(proxy => !string.Equals(proxy.GeoLocation?.CountryCode, "US",
                    StringComparison.OrdinalIgnoreCase));
            }

            var candidates = records.OrderBy(proxy => proxy.LatencyMs ?? long.MaxValue)
                .Take(Math.Max(RandomProxyCandidateCount, count)).ToList();
            if (candidates.Count < 2)
            {
                return (IReadOnlyList<ProxyDto>)candidates
                    .Select(proxy => ToDto(proxy, state.Sources.ToDictionary(source => source.Id, source => source.Name)))
                    .ToList();
            }

            for (var index = candidates.Count - 1; index > 0; index--)
            {
                var selectedIndex = Random.Shared.Next(index + 1);
                (candidates[index], candidates[selectedIndex]) = (candidates[selectedIndex], candidates[index]);
            }

            var sourceNames = state.Sources.ToDictionary(source => source.Id, source => source.Name);
            return (IReadOnlyList<ProxyDto>)candidates.Take(count).Select(proxy => ToDto(proxy, sourceNames)).ToList();
        }, cancellationToken);

    public Task<string> ExportAliveAsync(string? protocolValue, string? countryCode,
        CancellationToken cancellationToken) =>
        _store.ReadAsync(state =>
        {
            IEnumerable<ProxyRecord> records = state.Proxies.Where(proxy => proxy.Status == ProxyStatus.Alive);
            if (TryParseProtocol(protocolValue, out var protocol))
            {
                records = records.Where(proxy => proxy.Protocol == protocol);
            }
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                records = records.Where(proxy => string.Equals(proxy.GeoLocation?.CountryCode, countryCode,
                    StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                records = records.Where(proxy => !string.Equals(proxy.GeoLocation?.CountryCode, "US",
                    StringComparison.OrdinalIgnoreCase));
            }

            return string.Join(Environment.NewLine, records.OrderBy(proxy => proxy.LatencyMs ?? long.MaxValue)
                .Select(proxy => $"{proxy.Host}:{proxy.Port}"));
        }, cancellationToken);

    public Task<SourceDto> AddSourceAsync(SourceWriteRequest request, CancellationToken cancellationToken)
    {
        ValidateSource(request);
        return _store.WriteAsync(state =>
        {
            if (state.Sources.Any(source => source.Url.Equals(request.Url.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("相同 URL 的采集源已经存在。", nameof(request));
            }

            var source = new ProxySource
            {
                Name = request.Name.Trim(),
                Url = request.Url.Trim(),
                Protocol = request.Protocol,
                Enabled = request.Enabled
            };
            state.Sources.Add(source);
            return ToDto(source);
        }, cancellationToken);
    }

    public Task<SourceDto?> UpdateSourceAsync(Guid id, SourceWriteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSource(request);
        return _store.WriteAsync(state =>
        {
            var source = state.Sources.FirstOrDefault(item => item.Id == id);
            if (source is null)
            {
                return null;
            }

            if (state.Sources.Any(item => item.Id != id &&
                    item.Url.Equals(request.Url.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("相同 URL 的采集源已经存在。", nameof(request));
            }

            source.Name = request.Name.Trim();
            source.Url = request.Url.Trim();
            source.Protocol = request.Protocol;
            source.Enabled = request.Enabled;
            return ToDto(source);
        }, cancellationToken);
    }

    public Task<bool> DeleteSourceAsync(Guid id, CancellationToken cancellationToken) =>
        _store.WriteAsync(state =>
        {
            var removed = state.Sources.RemoveAll(source => source.Id == id) > 0;
            if (removed)
            {
                foreach (var proxy in state.Proxies)
                {
                    proxy.SourceIds.Remove(id);
                }
            }

            return removed;
        }, cancellationToken);

    public async Task<PoolOperationResult> ScanAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
        {
            return new PoolOperationResult(true, "采集任务正在运行。" );
        }

        try
        {
            var sources = await _store.ReadAsync(state => state.Sources.Where(source => source.Enabled)
                .Select(CloneSource).ToList(), cancellationToken);
            if (sources.Count == 0)
            {
                return SetMessage(new PoolOperationResult(false, "没有已启用的采集源。"));
            }

            var results = new ConcurrentBag<SourceFetchResult>();
            await Parallel.ForEachAsync(sources, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Options.SourceConcurrency, 1, 16),
                CancellationToken = cancellationToken
            }, async (source, token) => results.Add(await FetchSourceAsync(source, token)));

            var now = DateTimeOffset.UtcNow;
            var summary = await _store.WriteAsync(state =>
            {
                state.Quarantines.RemoveAll(quarantine => quarantine.ExpiresAt <= now);
                var existing = state.Proxies.ToDictionary(proxy => proxy.Key, StringComparer.OrdinalIgnoreCase);
                var quarantined = state.Quarantines.ToDictionary(quarantine => quarantine.Key,
                    StringComparer.OrdinalIgnoreCase);
                var added = 0;
                var updated = 0;
                var deferred = 0;
                var capacityRemoved = 0;
                var pendingCount = state.Proxies.Count(proxy => proxy.Status == ProxyStatus.Pending && !proxy.IsPinned);
                var maxPending = Math.Clamp(Options.MaxPendingProxies, 1, Options.MaxPoolSize);
                foreach (var result in results)
                {
                    var source = state.Sources.FirstOrDefault(item => item.Id == result.SourceId);
                    if (source is null)
                    {
                        continue;
                    }

                    source.LastScanAt = now;
                    source.LastFound = result.Candidates.Count;
                    source.LastError = result.Error;
                    if (result.Error is not null)
                    {
                        continue;
                    }

                    foreach (var candidate in result.Candidates)
                    {
                        var key = $"{candidate.Protocol}:{candidate.Host.ToLowerInvariant()}:{candidate.Port}";
                        if (quarantined.ContainsKey(key))
                        {
                            continue;
                        }

                        if (existing.TryGetValue(key, out var proxy))
                        {
                            proxy.LastSeenAt = now;
                            if (!proxy.SourceIds.Contains(source.Id))
                            {
                                proxy.SourceIds.Add(source.Id);
                            }
                            updated++;
                            continue;
                        }

                        if (pendingCount >= maxPending)
                        {
                            deferred++;
                            continue;
                        }

                        if (state.Proxies.Count >= Options.MaxPoolSize)
                        {
                            var eviction = SelectOneCapacityEviction(state.Proxies, Options.MaxConsecutiveFailures);
                            if (eviction is null)
                            {
                                deferred++;
                                continue;
                            }

                            RemoveWithQuarantine(state, [eviction], now, Options);
                            existing.Remove(eviction.Key);
                            if (eviction.Status == ProxyStatus.Pending && !eviction.IsPinned)
                            {
                                pendingCount--;
                            }
                            quarantined[eviction.Key] = new ProxyQuarantine
                            {
                                Key = eviction.Key,
                                ExpiresAt = now.AddHours(Math.Max(1, Options.ReaddQuarantineHours))
                            };
                            capacityRemoved++;
                        }

                        proxy = new ProxyRecord
                        {
                            Host = candidate.Host,
                            Port = candidate.Port,
                            Protocol = candidate.Protocol,
                            FirstSeenAt = now,
                            LastSeenAt = now,
                            SourceIds = [source.Id]
                        };
                        state.Proxies.Add(proxy);
                        existing[key] = proxy;
                        pendingCount++;
                        added++;
                    }
                }

                var hardCapacityEvictions = SelectCapacityEvictions(state.Proxies, Options.MaxPoolSize,
                    Options.MaxConsecutiveFailures);
                RemoveWithQuarantine(state, hardCapacityEvictions, now, Options);
                var failed = results.Count(result => result.Error is not null);
                return new PoolOperationResult(false,
                    $"采集完成：新增 {added}，刷新 {updated}，延后 {deferred}，容量清理 {capacityRemoved + hardCapacityEvictions.Count}，失败源 {failed}。",
                    results.Sum(result => result.Candidates.Count), added, updated,
                    capacityRemoved + hardCapacityEvictions.Count, failed);
            }, cancellationToken);
            _lastScanAt = now;
            return SetMessage(summary);
        }
        finally
        {
            Volatile.Write(ref _isScanning, 0);
        }
    }

    public async Task<PoolOperationResult> CheckDueAsync(bool force, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0)
        {
            return new PoolOperationResult(true, "检测任务正在运行。" );
        }

        ResetCheckProgress();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var initialSweepCompleted = await _store.ReadAsync(state => state.InitialSweepCompleted,
                cancellationToken);
            if (!initialSweepCompleted)
            {
                var hasPending = await _store.ReadAsync(state => state.Proxies.Any(proxy =>
                    proxy.Status == ProxyStatus.Pending), cancellationToken);
                if (!hasPending)
                {
                    await _store.WriteAsync(state =>
                    {
                        state.InitialSweepCompleted = true;
                        return 0;
                    }, cancellationToken);
                }
            }

            var proxies = await _store.ReadAsync(state => SelectProxiesForCheck(state, force, now),
                cancellationToken);
            Interlocked.Exchange(ref _checkTotal, proxies.Count);
            if (proxies.Count == 0)
            {
                _lastCheckAt = now;
                MarkCheckFinished();
                return SetMessage(new PoolOperationResult(false, "当前没有到期需要检测的代理。"));
            }

            _lastMessage = $"检测队列已启动：共 {proxies.Count} 个代理，并发 {Options.CheckConcurrency}。";
            var results = new ConcurrentBag<ProxyCheckResult>();
            await Parallel.ForEachAsync(proxies, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Options.CheckConcurrency, 1, 500),
                CancellationToken = cancellationToken
            }, async (proxy, token) =>
            {
                Interlocked.Increment(ref _checkInFlight);
                try
                {
                    var result = await _checker.CheckAsync(proxy, token);
                    results.Add(result);
                    Interlocked.Increment(ref _checkCompleted);
                    if (result.IsAlive)
                    {
                        Interlocked.Increment(ref _checkAlive);
                    }
                    else
                    {
                        Interlocked.Increment(ref _checkFailed);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _checkInFlight);
                }
            });

            var resultMap = results.ToDictionary(result => result.ProxyId);
            var summary = await _store.WriteAsync(state =>
            {
                foreach (var proxy in state.Proxies)
                {
                    if (resultMap.TryGetValue(proxy.Id, out var result))
                    {
                        ApplyCheckResult(proxy, result);
                    }
                }

                if (!state.InitialSweepCompleted && state.Proxies.All(proxy => proxy.Status != ProxyStatus.Pending))
                {
                    state.InitialSweepCompleted = true;
                }

                var alive = results.Count(result => result.IsAlive);
                return new PoolOperationResult(false,
                    $"检测完成：处理 {results.Count}，可用 {alive}，失败 {results.Count - alive}。",
                    results.Count, 0, alive, 0, results.Count - alive);
            }, cancellationToken);
            _lastCheckAt = DateTimeOffset.UtcNow;
            MarkCheckFinished();
            return SetMessage(summary);
        }
        finally
        {
            if (Interlocked.Read(ref _checkFinishedAtUnixMilliseconds) == 0)
            {
                MarkCheckFinished();
            }

            Volatile.Write(ref _isChecking, 0);
        }
    }

    public Task<bool> HasPendingAsync(CancellationToken cancellationToken) =>
        _store.ReadAsync(state => state.Proxies.Any(proxy => proxy.Status == ProxyStatus.Pending), cancellationToken);

    public Task<int> TrimExcessPendingAsync(CancellationToken cancellationToken) =>
        _store.WriteAsync(state =>
        {
            var now = DateTimeOffset.UtcNow;
            var excess = state.Proxies.Where(proxy => proxy.Status == ProxyStatus.Pending && !proxy.IsPinned)
                .OrderByDescending(proxy => proxy.FirstSeenAt)
                .Skip(Math.Clamp(Options.MaxPendingProxies, 1, Options.MaxPoolSize))
                .ToList();
            RemoveWithQuarantine(state, excess, now, Options);
            return excess.Count;
        }, cancellationToken);

    public async Task<PoolOperationResult> PruneAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isPruning, 1, 0) != 0)
        {
            return new PoolOperationResult(true, "清理任务正在运行。" );
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var deadCutoff = now.AddHours(-Math.Clamp(Options.RemoveDeadAfterHours, 1, 24 * 365));
            var unseenCutoff = now.AddHours(-Math.Clamp(Options.RemoveUnseenAfterHours, 1, 24 * 365));
            var removed = await _store.WriteAsync(state =>
            {
                state.Quarantines.RemoveAll(quarantine => quarantine.ExpiresAt <= now);
                var toRemove = state.Proxies.Where(proxy =>
                    !proxy.IsPinned &&
                    ((proxy.Status == ProxyStatus.Dead &&
                      proxy.ConsecutiveFailures >= Math.Max(1, Options.MaxConsecutiveFailures) &&
                      (proxy.QuarantinedUntil is null || proxy.QuarantinedUntil <= now) &&
                      (proxy.LastAliveAt ?? proxy.FirstSeenAt) < deadCutoff) ||
                     ((proxy.Status is ProxyStatus.Dead or ProxyStatus.Pending) && proxy.LastSeenAt < unseenCutoff)))
                    .ToList();
                var removedIds = toRemove.Select(proxy => proxy.Id).ToHashSet();
                var capacityEvictions = SelectCapacityEvictions(
                    state.Proxies.Where(proxy => !removedIds.Contains(proxy.Id)), Options.MaxPoolSize,
                    Options.MaxConsecutiveFailures);
                toRemove.AddRange(capacityEvictions);
                RemoveWithQuarantine(state, toRemove, now, Options);
                return toRemove.Count;
            }, cancellationToken);
            _lastPruneAt = now;
            return SetMessage(new PoolOperationResult(false, $"清理完成：移除 {removed} 个长期失效代理。",
                Removed: removed));
        }
        finally
        {
            Volatile.Write(ref _isPruning, 0);
        }
    }

    private async Task<SourceFetchResult> FetchSourceAsync(ProxySource source, CancellationToken cancellationToken)
    {
        try
        {
            var currentUri = new Uri(source.Url, UriKind.Absolute);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Options.DownloadTimeoutSeconds, 3, 120)));
            var client = _httpClientFactory.CreateClient("proxy-sources");

            for (var redirect = 0; redirect <= 5; redirect++)
            {
                if (currentUri.Scheme is not ("http" or "https") ||
                    (!Options.AllowPrivateNetworks &&
                     !await EndpointSafety.IsPublicHostAsync(currentUri.Host, timeout.Token)))
                {
                    throw new InvalidOperationException("采集源不是安全的公网 HTTP(S) 地址。" );
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    currentUri = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(currentUri, response.Headers.Location);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var content = await ReadLimitedStringAsync(response.Content, timeout.Token);
                return new SourceFetchResult(source.Id,
                    _parser.Parse(content, source.Protocol, Math.Clamp(Options.MaxCandidatesPerSource, 1, 50_000)),
                    null);
            }

            throw new HttpRequestException("采集源重定向次数过多。" );
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                           InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning(exception, "采集源 {SourceName} 下载失败", source.Name);
            var message = exception is TaskCanceledException ? "下载超时" : exception.Message;
            return new SourceFetchResult(source.Id, [], message.Length <= 240 ? message : message[..240]);
        }
    }

    private async Task<string> ReadLimitedStringAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > Options.MaxSourceBytes)
        {
            throw new InvalidOperationException("采集源内容超过大小限制。" );
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var maximum = Math.Clamp(Options.MaxSourceBytes, 1024, 20_000_000);
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (target.Length + read > maximum)
            {
                throw new InvalidOperationException("采集源内容超过大小限制。" );
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(target.ToArray());
    }

    private static List<ProxyRecord> SelectCapacityEvictions(IEnumerable<ProxyRecord> records, int maxPoolSize,
        int maxConsecutiveFailures)
    {
        var items = records.ToList();
        var excess = Math.Max(0, items.Count - Math.Clamp(maxPoolSize, 1, 200_000));
        if (excess == 0)
        {
            return [];
        }

        return items.Where(proxy => !proxy.IsPinned)
            .OrderBy(proxy => EvictionPriority(proxy, maxConsecutiveFailures))
            .ThenByDescending(proxy => proxy.ConsecutiveFailures)
            .ThenBy(proxy => proxy.LastSeenAt)
            .ThenBy(proxy => proxy.FirstSeenAt)
            .Take(excess)
            .ToList();
    }

    private static ProxyRecord? SelectOneCapacityEviction(IEnumerable<ProxyRecord> records,
        int maxConsecutiveFailures) => records
        .Where(proxy => !proxy.IsPinned)
        .OrderBy(proxy => EvictionPriority(proxy, maxConsecutiveFailures))
        .ThenByDescending(proxy => proxy.ConsecutiveFailures)
        .ThenBy(proxy => proxy.LastSeenAt)
        .ThenBy(proxy => proxy.FirstSeenAt)
        .FirstOrDefault();

    private static int EvictionPriority(ProxyRecord proxy, int maxConsecutiveFailures) => proxy.Status switch
    {
        ProxyStatus.Dead when proxy.ConsecutiveFailures >= Math.Max(1, maxConsecutiveFailures) => 0,
        ProxyStatus.Dead when proxy.LastAliveAt is null => 1,
        ProxyStatus.Pending => 2,
        ProxyStatus.Dead => 3,
        _ => 4
    };

    private static void RemoveWithQuarantine(ProxyPoolState state, IEnumerable<ProxyRecord> records,
        DateTimeOffset now, ProxyPoolOptions options)
    {
        var toRemove = records.DistinctBy(proxy => proxy.Id).ToList();
        foreach (var proxy in toRemove)
        {
            state.Quarantines.RemoveAll(quarantine => quarantine.Key.Equals(proxy.Key,
                StringComparison.OrdinalIgnoreCase));
            state.Quarantines.Add(new ProxyQuarantine
            {
                Key = proxy.Key,
                ExpiresAt = now.AddHours(Math.Max(1, options.ReaddQuarantineHours))
            });
        }

        var removedIds = toRemove.Select(proxy => proxy.Id).ToHashSet();
        state.Proxies.RemoveAll(proxy => removedIds.Contains(proxy.Id));
    }

    private bool IsDue(ProxyRecord proxy, DateTimeOffset now)
    {
        if (proxy.LastCheckedAt is null || proxy.Status == ProxyStatus.Pending)
        {
            return true;
        }

        var nextCheckAt = GetNextCheckAt(proxy);
        return nextCheckAt is null || nextCheckAt <= now;
    }

    private IReadOnlyList<ProxyRecord> SelectProxiesForCheck(ProxyPoolState state, bool force,
        DateTimeOffset now)
    {
        var capacity = Math.Clamp(Options.MaxChecksPerCycle, 1, 20_000);
        var pendingBacklog = state.Proxies.Any(proxy => proxy.Status == ProxyStatus.Pending);
        if (pendingBacklog)
        {
            return state.Proxies.Where(proxy => proxy.Status == ProxyStatus.Pending)
                .OrderBy(proxy => proxy.FirstSeenAt)
                .Take(capacity)
                .Select(CloneProxy)
                .ToList();
        }

        var eligible = state.Proxies.Where(proxy => force || IsDue(proxy, now));
        var alive = eligible.Where(proxy => proxy.Status == ProxyStatus.Alive)
            .OrderBy(proxy => proxy.LastCheckedAt).ToList();
        var pending = eligible.Where(proxy => proxy.Status == ProxyStatus.Pending)
            .OrderBy(proxy => proxy.FirstSeenAt).ToList();
        var dead = eligible.Where(proxy => proxy.Status == ProxyStatus.Dead)
            .OrderBy(proxy => GetNextCheckAt(proxy)).ToList();

        var selected = new List<ProxyRecord>(capacity);
        Add(alive, Options.AliveChecksPerCycle);
        Add(pending, Options.PendingChecksPerCycle);
        Add(dead, Options.DeadChecksPerCycle);
        Add(pending, capacity);
        Add(dead, capacity);
        Add(alive, capacity);
        return selected.Select(CloneProxy).ToList();

        void Add(IEnumerable<ProxyRecord> candidates, int limit)
        {
            foreach (var candidate in candidates)
            {
                if (selected.Count >= capacity || selected.Count(item => item.Status == candidate.Status) >= limit)
                {
                    break;
                }

                if (!selected.Any(item => item.Id == candidate.Id))
                {
                    selected.Add(candidate);
                }
            }
        }
    }

    private OperationStateDto GetOperationState()
    {
        var isChecking = Volatile.Read(ref _isChecking) == 1;
        var total = Volatile.Read(ref _checkTotal);
        var completed = Volatile.Read(ref _checkCompleted);
        var inFlight = Volatile.Read(ref _checkInFlight);
        var startedAt = ReadCheckTimestamp(ref _checkStartedAtUnixMilliseconds);
        var finishedAt = ReadCheckTimestamp(ref _checkFinishedAtUnixMilliseconds);
        // Queue progress belongs to the active check operation only. Due records are pool state,
        // not items waiting inside a task queue.
        var waiting = isChecking ? Math.Max(0, total - completed - inFlight) : 0;
        var progress = total == 0 ? 0 : Math.Round(completed * 100d / total, 1);

        return new OperationStateDto(
            Volatile.Read(ref _isScanning) == 1,
            isChecking,
            Volatile.Read(ref _isPruning) == 1,
            _lastScanAt,
            _lastCheckAt,
            ReadCheckTimestamp(ref _nextCheckAtUnixMilliseconds),
            _lastPruneAt,
            _lastMessage,
            new CheckQueueStateDto(
                isChecking,
                waiting,
                total,
                completed,
                inFlight,
                Volatile.Read(ref _checkAlive),
                Volatile.Read(ref _checkFailed),
                Math.Clamp(Options.CheckConcurrency, 1, 500),
                progress,
                startedAt,
                finishedAt));
    }

    private void ResetCheckProgress()
    {
        Interlocked.Exchange(ref _checkTotal, 0);
        Interlocked.Exchange(ref _checkCompleted, 0);
        Interlocked.Exchange(ref _checkInFlight, 0);
        Interlocked.Exchange(ref _checkAlive, 0);
        Interlocked.Exchange(ref _checkFailed, 0);
        Interlocked.Exchange(ref _checkStartedAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref _checkFinishedAtUnixMilliseconds, 0);
    }

    private void MarkCheckFinished() =>
        Interlocked.Exchange(ref _checkFinishedAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static DateTimeOffset? ReadCheckTimestamp(ref long timestamp)
    {
        var value = Interlocked.Read(ref timestamp);
        return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    private PoolOperationResult SetMessage(PoolOperationResult result)
    {
        _lastMessage = result.Message;
        return result;
    }

    private static void ValidateSource(SourceWriteRequest request)
    {
        var name = request.Name.Trim();
        var url = request.Url.Trim();
        if (name.Length is 0 or > 128 || url.Length is 0 or > 2_048 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("请输入名称和有效的公网 HTTP(S) 采集地址。", nameof(request));
        }
    }

    private static bool TryParseProtocol(string? value, out ProxyProtocol protocol) =>
        Enum.TryParse(value, true, out protocol);

    private static int StatusRank(ProxyStatus status) => status switch
    {
        ProxyStatus.Alive => 0,
        ProxyStatus.Pending => 1,
        _ => 2
    };

    private static double SuccessRate(ProxyRecord proxy)
    {
        var total = proxy.SuccessCount + proxy.FailureCount;
        return total == 0 ? -1 : proxy.SuccessCount / (double)total;
    }

    private void ApplyCheckResult(ProxyRecord proxy, ProxyCheckResult result)
    {
        proxy.LastCheckedAt = result.CheckedAt;
        proxy.LatencyMs = result.LatencyMs;
        proxy.ExitIp = result.ExitIp;
        if (result.IsAlive && result.GeoLocation is { } location)
        {
            proxy.GeoLocation = location;
        }
        else if (result.IsAlive)
        {
            _geoIp?.QueueRemoteLookup(result.ExitIp);
        }
        proxy.LastError = result.Error;
        if (result.IsAlive)
        {
            proxy.Status = ProxyStatus.Alive;
            proxy.LastAliveAt = result.CheckedAt;
            proxy.SuccessCount++;
            proxy.ConsecutiveFailures = 0;
            proxy.QuarantinedUntil = null;
        }
        else
        {
            proxy.Status = ProxyStatus.Dead;
            proxy.FailureCount++;
            proxy.ConsecutiveFailures++;
            proxy.QuarantinedUntil = proxy.ConsecutiveFailures >= Math.Max(1, Options.MaxConsecutiveFailures)
                ? result.CheckedAt.AddHours(Math.Max(1, Options.DeadQuarantineHours))
                : null;
        }
    }

    private static ProxyRecord CloneProxy(ProxyRecord proxy) => new()
    {
        Id = proxy.Id,
        Host = proxy.Host,
        Port = proxy.Port,
        Protocol = proxy.Protocol,
        Status = proxy.Status,
        IsPinned = proxy.IsPinned,
        SourceIds = [.. proxy.SourceIds],
        FirstSeenAt = proxy.FirstSeenAt,
        LastSeenAt = proxy.LastSeenAt,
        LastCheckedAt = proxy.LastCheckedAt,
        LastAliveAt = proxy.LastAliveAt,
        LatencyMs = proxy.LatencyMs,
        ExitIp = proxy.ExitIp,
        GeoLocation = proxy.GeoLocation,
        SuccessCount = proxy.SuccessCount,
        FailureCount = proxy.FailureCount,
        ConsecutiveFailures = proxy.ConsecutiveFailures,
        QuarantinedUntil = proxy.QuarantinedUntil,
        LastError = proxy.LastError
    };

    private static ProxySource CloneSource(ProxySource source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Url = source.Url,
        Protocol = source.Protocol,
        Enabled = source.Enabled,
        IsBuiltIn = source.IsBuiltIn,
        LastScanAt = source.LastScanAt,
        LastFound = source.LastFound,
        LastError = source.LastError
    };

    private ProxyDto ToDto(ProxyRecord proxy, IReadOnlyDictionary<Guid, string> sourceNames) => new(
        proxy.Id,
        proxy.Host,
        proxy.Port,
        proxy.Protocol,
        proxy.Status,
        proxy.IsPinned,
        proxy.SourceIds.Where(sourceNames.ContainsKey).Select(id => sourceNames[id]).ToList(),
        proxy.FirstSeenAt,
        proxy.LastSeenAt,
        proxy.LastCheckedAt,
        GetNextCheckAt(proxy),
        proxy.LastAliveAt,
        proxy.LatencyMs,
        proxy.ExitIp,
        proxy.GeoLocation,
        proxy.SuccessCount,
        proxy.FailureCount,
        proxy.ConsecutiveFailures,
        proxy.LastError,
        $"{proxy.Protocol.ToString().ToLowerInvariant()}://{(proxy.Host.Contains(':') ? $"[{proxy.Host}]" : proxy.Host)}:{proxy.Port}");

    private DateTimeOffset? GetNextCheckAt(ProxyRecord proxy)
    {
        if (proxy.LastCheckedAt is null || proxy.Status == ProxyStatus.Pending)
        {
            return null;
        }

        if (proxy.Status == ProxyStatus.Alive)
        {
            return proxy.LastCheckedAt.Value.AddMinutes(Math.Max(1, Options.RecheckAliveMinutes));
        }

        if (proxy.QuarantinedUntil is { } quarantinedUntil)
        {
            return quarantinedUntil;
        }

        var minutes = proxy.ConsecutiveFailures switch
        {
            <= 1 => Math.Max(1, Options.RecheckDeadMinutes),
            2 => Math.Max(1, Options.SecondDeadRetryMinutes),
            _ => Math.Max(1, Options.DeadQuarantineHours) * 60
        };
        return proxy.LastCheckedAt.Value.AddMinutes(minutes);
    }

    private static SourceDto ToDto(ProxySource source) => new(
        source.Id,
        source.Name,
        source.Url,
        source.Protocol,
        source.Enabled,
        source.IsBuiltIn,
        source.LastScanAt,
        source.LastFound,
        source.LastError);

    private sealed record SourceFetchResult(Guid SourceId, IReadOnlyList<ProxyCandidate> Candidates, string? Error);
}
