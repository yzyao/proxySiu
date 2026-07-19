using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace ProxySiu.Api.Options;

public sealed class ProxyAuthOptions
{
    public const string SectionName = "ProxyAuth";

    public bool Enabled { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public bool CookieSecure { get; set; } = true;
    public int SessionHours { get; set; } = 12;
}

public sealed class ProxyAuthOptionsValidator : IValidateOptions<ProxyAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyAuthOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (options.AccessToken.Length < 24)
        {
            failures.Add("ProxyAuth:AccessToken must contain at least 24 characters when authentication is enabled.");
        }

        if (options.SessionHours is < 1 or > 168)
        {
            failures.Add("ProxyAuth:SessionHours must be between 1 and 168.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class ProxySessionService(IOptions<ProxyAuthOptions> options, IDataProtectionProvider dataProtectionProvider)
{
    public const string CookieName = "proxysiu_session";
    private readonly ProxyAuthOptions _options = options.Value;
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("ProxySiu.Session.v1");

    public bool IsEnabled => _options.Enabled;

    public bool TrySignIn(string? accessToken, HttpResponse response)
    {
        if (!_options.Enabled || !Matches(accessToken, _options.AccessToken))
        {
            return false;
        }

        var expiresAt = DateTimeOffset.UtcNow.AddHours(_options.SessionHours);
        var ticket = _protector.Protect($"session\n{expiresAt.ToUnixTimeSeconds()}");
        response.Cookies.Append(CookieName, ticket, CreateCookieOptions(expiresAt));
        return true;
    }

    public bool TryGetUser(HttpRequest request, out string? username)
    {
        username = null;
        if (!_options.Enabled)
        {
            return true;
        }

        if (!request.Cookies.TryGetValue(CookieName, out var ticket))
        {
            return false;
        }

        try
        {
            var values = _protector.Unprotect(ticket).Split('\n', 2);
            if (values.Length != 2 || !long.TryParse(values[1], out var expiresAtUnix) ||
                DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix) <= DateTimeOffset.UtcNow ||
                values[0] != "session")
            {
                return false;
            }

            username = "token";
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void SignOut(HttpResponse response) => response.Cookies.Delete(CookieName, new CookieOptions
    {
        Path = "/",
        Secure = _options.CookieSecure,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict
    });

    public bool TryAuthenticateApiKey(HttpRequest request)
    {
        if (!_options.Enabled)
        {
            return true;
        }

        var suppliedToken = request.Headers.TryGetValue("X-API-Key", out var apiKey)
            ? apiKey.ToString()
            : ReadBearerToken(request.Headers.Authorization);
        return Matches(suppliedToken, _options.AccessToken);
    }

    private CookieOptions CreateCookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = _options.CookieSecure,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/",
        Expires = expiresAt
    };

    private static bool Matches(string? supplied, string expected)
    {
        if (supplied is null)
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static string? ReadBearerToken(string? authorization)
    {
        const string prefix = "Bearer ";
        return authorization is not null && authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }
}
