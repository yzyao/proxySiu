using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using MaxMind.Db;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Services;

public sealed class GeoIpService : IDisposable
{
    private readonly Reader? _reader;
    private readonly GeoIpOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeoIpService> _logger;
    private readonly Channel<string> _remoteQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });
    private readonly ConcurrentDictionary<string, byte> _remoteQueued = new(StringComparer.OrdinalIgnoreCase);

    public GeoIpService(IOptions<GeoIpOptions> options, IHostEnvironment environment,
        IHttpClientFactory httpClientFactory, ILogger<GeoIpService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.DatabasePath))
        {
            return;
        }

        var path = Path.IsPathRooted(_options.DatabasePath)
            ? _options.DatabasePath
            : Path.Combine(environment.ContentRootPath, _options.DatabasePath);
        if (!File.Exists(path))
        {
            logger.LogInformation("Local GeoIP database was not found at {Path}; api.ip.sb remains available.", path);
            return;
        }

        try
        {
            _reader = new Reader(path, FileAccessMode.Memory);
            logger.LogInformation("Loaded local GeoIP database from {Path}.", path);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            logger.LogWarning(exception, "Could not load local GeoIP database from {Path}.", path);
        }
    }

    public int IpSbLookupIntervalSeconds => Math.Clamp(_options.IpSbLookupIntervalSeconds, 1, 60);

    public IpGeoLocation? Lookup(string? ipAddress)
    {
        if (_reader is null || !IPAddress.TryParse(ipAddress, out var address))
        {
            return null;
        }

        try
        {
            var record = _reader.Find<GeoLiteCityRecord>(address);
            if (record is null)
            {
                return null;
            }

            var subdivision = record.Subdivisions?.FirstOrDefault();
            var location = new IpGeoLocation(
                record.Country?.IsoCode,
                PickName(record.Country?.Names),
                subdivision?.IsoCode,
                PickName(subdivision?.Names),
                PickName(record.City?.Names));
            return location.HasValue ? location : null;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException)
        {
            _logger.LogDebug(exception, "Local GeoIP lookup failed for {IpAddress}.", ipAddress);
            return null;
        }
    }

    public void QueueRemoteLookup(string? ipAddress)
    {
        if (!_options.UseIpSb || !IPAddress.TryParse(ipAddress, out var address) ||
            !EndpointSafety.IsPublicAddress(address))
        {
            return;
        }

        var key = address.ToString();
        if (_remoteQueued.TryAdd(key, 0) && !_remoteQueue.Writer.TryWrite(key))
        {
            _remoteQueued.TryRemove(key, out _);
        }
    }

    public IAsyncEnumerable<string> ReadRemoteQueueAsync(CancellationToken cancellationToken) =>
        _remoteQueue.Reader.ReadAllAsync(cancellationToken);

    public async Task<IpGeoLocation?> LookupIpSbAsync(string ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"{_options.IpSbBaseUrl.TrimEnd('/')}/geoip/{Uri.EscapeDataString(ipAddress)}";
            using var response = await _httpClientFactory.CreateClient("geoip-lookup").GetAsync(endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var location = new IpGeoLocation(
                ReadString(root, "country_code"),
                ReadString(root, "country"),
                ReadString(root, "region_code"),
                ReadString(root, "region"),
                ReadString(root, "city"));
            return location.HasValue ? location : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(exception, "api.ip.sb lookup failed for {IpAddress}.", ipAddress);
            return null;
        }
    }

    public void CompleteRemoteLookup(string ipAddress, bool keepCached)
    {
        if (!keepCached)
        {
            _remoteQueued.TryRemove(ipAddress, out _);
        }
    }

    public void Dispose() => _reader?.Dispose();

    private static string? PickName(Dictionary<string, string>? names) => names is null
        ? null
        : names.GetValueOrDefault("zh-CN") ?? names.GetValueOrDefault("en") ?? names.Values.FirstOrDefault();

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class GeoLiteCityRecord
    {
        [MapKey("country")]
        public GeoNameRecord? Country { get; init; }

        [MapKey("subdivisions")]
        public List<GeoNameRecord>? Subdivisions { get; init; }

        [MapKey("city")]
        public GeoNameRecord? City { get; init; }
    }

    private sealed class GeoNameRecord
    {
        [MapKey("iso_code")]
        public string? IsoCode { get; init; }

        [MapKey("names")]
        public Dictionary<string, string>? Names { get; init; }
    }
}
