using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Services;

public sealed class ProxyChecker(ProxyPoolProfileManager profileManager, ILogger<ProxyChecker> logger)
{
    public async Task<ProxyCheckResult> CheckAsync(ProxyRecord proxy, CancellationToken cancellationToken)
    {
        var options = profileManager.Current;
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!options.AllowPrivateNetworks &&
                !await EndpointSafety.IsPublicHostAsync(proxy.Host, cancellationToken))
            {
                return new ProxyCheckResult(proxy.Id, false, null, null,
                    "代理地址不是可公开路由的地址。", checkedAt);
            }

            var scheme = proxy.Protocol switch
            {
                ProxyProtocol.Socks4 => "socks4",
                ProxyProtocol.Socks5 => "socks5",
                _ => "http"
            };
            var proxyUri = new Uri($"{scheme}://{FormatHost(proxy.Host)}:{proxy.Port}");
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, options.CheckUrl);
            request.Headers.UserAgent.ParseAdd("ProxySiu-Checker/1.0");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 2, 60)));
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            stopwatch.Stop();

            var checkResponse = ParseCheckResponse(body);
            return new ProxyCheckResult(proxy.Id, true, stopwatch.ElapsedMilliseconds,
                checkResponse.ExitIp, null, checkedAt, checkResponse.GeoLocation);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            logger.LogDebug(exception, "代理 {Host}:{Port} 检测失败", proxy.Host, proxy.Port);
            var message = exception is TaskCanceledException ? "检测超时" : exception.Message;
            return new ProxyCheckResult(proxy.Id, false, null, null, Truncate(message), checkedAt);
        }
    }

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static CheckResponse ParseCheckResponse(string body)
    {
        var json = body.Trim();
        if (!json.StartsWith('{'))
        {
            var opening = json.IndexOf('(');
            var closing = json.LastIndexOf(')');
            if (opening >= 0 && closing > opening)
            {
                json = json[(opening + 1)..closing];
            }
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var location = new IpGeoLocation(
                ReadString(root, "country_code"),
                ReadString(root, "country"),
                ReadString(root, "region_code"),
                ReadString(root, "region"),
                ReadString(root, "city"));
            return new CheckResponse(ReadString(root, "ip"), location.HasValue ? location : null);
        }
        catch (JsonException)
        {
            var value = body.Trim();
            if (IPAddress.TryParse(value, out _))
            {
                return new CheckResponse(value, null);
            }
        }

        return new CheckResponse(null, null);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record CheckResponse(string? ExitIp, IpGeoLocation? GeoLocation);

    private static string Truncate(string value) => value.Length <= 240 ? value : value[..240];
}
