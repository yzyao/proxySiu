using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Services;

public sealed class GeoIpService
{
    private readonly GeoIpOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeoIpService> _logger;
    private readonly Channel<string> _remoteQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });
    private readonly ConcurrentDictionary<string, byte> _remoteQueued = new(StringComparer.OrdinalIgnoreCase);

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

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
