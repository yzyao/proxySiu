using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Services;

public sealed class ProxyChecker(IOptions<ProxyPoolOptions> options, ILogger<ProxyChecker> logger)
{
    private readonly ProxyPoolOptions _options = options.Value;

    public async Task<ProxyCheckResult> CheckAsync(ProxyRecord proxy, CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!_options.AllowPrivateNetworks &&
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
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.CheckUrl);
            request.Headers.UserAgent.ParseAdd("ProxySiu-Checker/1.0");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 2, 60)));
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            stopwatch.Stop();

            return new ProxyCheckResult(proxy.Id, true, stopwatch.ElapsedMilliseconds,
                ExtractIp(body), null, checkedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            logger.LogDebug(exception, "代理 {Host}:{Port} 检测失败", proxy.Host, proxy.Port);
            var message = exception is TaskCanceledException ? "检测超时" : exception.Message;
            return new ProxyCheckResult(proxy.Id, false, null, null, Truncate(message), checkedAt);
        }
    }

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static string? ExtractIp(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("ip", out var property))
            {
                return property.GetString();
            }
        }
        catch (JsonException)
        {
            var value = body.Trim();
            if (IPAddress.TryParse(value, out _))
            {
                return value;
            }
        }

        return null;
    }

    private static string Truncate(string value) => value.Length <= 240 ? value : value[..240];
}
