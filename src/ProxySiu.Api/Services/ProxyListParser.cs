using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Services;

public sealed partial class ProxyListParser(IOptions<ProxyPoolOptions> options)
{
    private readonly ProxyPoolOptions _options = options.Value;

    public IReadOnlyList<ProxyCandidate> Parse(string content, ProxyProtocol fallbackProtocol, int limit)
    {
        var proxies = new Dictionary<string, ProxyCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (proxies.Count >= limit)
            {
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var match = ProxyPattern().Match(trimmed);
            if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var port) ||
                port is < 1 or > 65535)
            {
                continue;
            }

            var host = match.Groups["host"].Value.Trim('[', ']');
            if (!IPAddress.TryParse(host, out var address))
            {
                continue;
            }

            if (!_options.AllowPrivateNetworks && !EndpointSafety.IsPublicAddress(address))
            {
                continue;
            }

            var protocol = ParseProtocol(match.Groups["scheme"].Value, fallbackProtocol);
            var candidate = new ProxyCandidate(host, port, protocol);
            proxies[$"{protocol}:{host}:{port}"] = candidate;
        }

        return proxies.Values.ToList();
    }

    private static ProxyProtocol ParseProtocol(string scheme, ProxyProtocol fallback) =>
        scheme.ToLowerInvariant() switch
        {
            "http" or "https" => ProxyProtocol.Http,
            "socks4" or "socks4a" => ProxyProtocol.Socks4,
            "socks5" => ProxyProtocol.Socks5,
            _ => fallback
        };

    [GeneratedRegex(@"(?:(?<scheme>https?|socks4a?|socks5)://)?(?<host>\[[0-9a-fA-F:]+\]|(?:\d{1,3}\.){3}\d{1,3}):(?<port>\d{1,5})", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyPattern();
}
