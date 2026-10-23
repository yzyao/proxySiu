using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Services;

public sealed class GeoIpService : IDisposable
{
    private const int LookupCapacity = 10_000;
    private readonly GeoIpOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeoIpService> _logger;
    private readonly Channel<string> _remoteQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(LookupCapacity)
    {
        // TryWrite must return false when full so the pending deduplication entry can be removed.
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    });
    private readonly ConcurrentDictionary<string, byte> _remoteQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly MemoryCache _completedLookups = new(new MemoryCacheOptions { SizeLimit = LookupCapacity });

    public GeoIpService(IOptions<GeoIpOptions> options, IHttpClientFactory httpClientFactory,
        ILogger<GeoIpService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public int IpSbLookupIntervalSeconds => Math.Clamp(_options.IpSbLookupIntervalSeconds, 1, 60);

    public void QueueRemoteLookup(string? ipAddress)
    {
        if (!_options.UseIpSb || !IPAddress.TryParse(ipAddress, out var address) ||
            !EndpointSafety.IsPublicAddress(address))
        {
            return;
        }

        var key = address.ToString();
        if (!_completedLookups.TryGetValue(key, out _) &&
            _remoteQueued.TryAdd(key, 0) && !_remoteQueue.Writer.TryWrite(key))
        {
            _remoteQueued.TryRemove(key, out _);
        }
    }

    public IAsyncEnumerable<string> ReadRemoteQueueAsync(CancellationToken cancellationToken) =>
        _remoteQueue.Reader.ReadAllAsync(cancellationToken);

    public bool TryReadRemoteLookup(out string ipAddress) => _remoteQueue.Reader.TryRead(out ipAddress!);

    public async Task<IpGeoLocation?> LookupIpSbAsync(string ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"{_options.IpSbBaseUrl.TrimEnd('/')}/geoip/{Uri.EscapeDataString(ipAddress)}";
            using var client = _httpClientFactory.CreateClient("geoip-lookup");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var content = await LimitedHttpContent.ReadStringAsync(response.Content,
                LimitedHttpContent.MaximumLookupBytes, timeout.Token);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var location = new IpGeoLocation(
                ReadString(root, "country_code"),
                ReadString(root, "country"),
                ReadString(root, "region_code"),
                ReadString(root, "region"),
                ReadString(root, "city"));
            return location.HasValue ? location : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                         JsonException or InvalidOperationException)
        {
            _logger.LogDebug(exception, "api.ip.sb lookup failed for {IpAddress}.", ipAddress);
            return null;
        }
    }

    public void CompleteRemoteLookup(string ipAddress, bool keepCached)
    {
        if (keepCached)
        {
            _completedLookups.Set(ipAddress, true, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6)
            });
        }

        _remoteQueued.TryRemove(ipAddress, out _);
    }

    public void Dispose() => _completedLookups.Dispose();

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
