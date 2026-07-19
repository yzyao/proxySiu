using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Contracts;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;
using ProxySiu.Api.Services;
using ProxySiu.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddInMemoryCollection(LoadDotEnvConfiguration(builder.Environment.ContentRootPath));
builder.Configuration.AddEnvironmentVariables();
var selectedProfile = ResolveProfile(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddSingleton<ProxyPoolOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ProxyPoolOptions>>(serviceProvider =>
    serviceProvider.GetRequiredService<ProxyPoolOptionsValidator>());
builder.Services.AddOptions<ProxyPoolOptions>()
    .Bind(builder.Configuration.GetSection(ProxyPoolOptions.SectionName))
    .ValidateOnStart();
builder.Services.PostConfigure<ProxyPoolOptions>(options => ProxyPoolProfileSelector.Apply(options, selectedProfile));
builder.Services.AddSingleton(serviceProvider => new ProxyPoolProfileManager(builder.Configuration,
    serviceProvider.GetRequiredService<ProxyPoolOptionsValidator>(), selectedProfile));
builder.Services.AddSingleton<ProxyAuthOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ProxyAuthOptions>>(serviceProvider =>
    serviceProvider.GetRequiredService<ProxyAuthOptionsValidator>());
builder.Services.AddOptions<ProxyAuthOptions>()
    .Bind(builder.Configuration.GetSection(ProxyAuthOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<GeoIpOptions>()
    .Bind(builder.Configuration.GetSection(GeoIpOptions.SectionName));
builder.Services.AddDataProtection();
builder.Services.AddSingleton<ProxySessionService>();
builder.Services.AddSingleton<GeoIpService>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(builder.Configuration.GetSection("CorsOrigins").Get<string[]>() ??
                     ["http://localhost:5173", "http://127.0.0.1:5173"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.AddHttpClient("proxy-sources", client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxySiu/1.0 (+local proxy pool manager)");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false
    });
builder.Services.AddHttpClient("geoip-lookup", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxySiu-GeoIP-Lookup/1.0");
    });

builder.Services.AddSingleton<JsonProxyStore>();
builder.Services.AddSingleton<ProxyListParser>();
builder.Services.AddSingleton<ProxyChecker>();
builder.Services.AddSingleton<ProxyPoolService>();
builder.Services.AddSingleton<MaintenanceOperationQueue>();
builder.Services.AddHostedService<MaintenanceOperationWorker>();
builder.Services.AddHostedService<ProxyMaintenanceWorker>();
builder.Services.AddHostedService<GeoIpEnrichmentWorker>();

var app = builder.Build();

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    var options = context.RequestServices.GetRequiredService<ProxyPoolProfileManager>().Current;
    if (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress) && !options.AllowInternalNetworkAccess)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "This local-stability build only permits loopback access."
        });
        return;
    }

    await next(context);
});

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new
    {
        message = "The server could not process the request. Check the server logs."
    });
}));

app.UseCors();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isAnonymousEndpoint = path.Equals("/api/auth/login") || path.Equals("/api/auth/session") ||
                              path.Equals("/api/health") || HttpMethods.IsOptions(context.Request.Method);
    var isApiKeyEndpoint = path.Equals("/api/proxy/random") || path.Equals("/api/proxy/plain") ||
                           path.Equals("/api/proxy/countries");
    var sessions = context.RequestServices.GetRequiredService<ProxySessionService>();
    if (path.StartsWithSegments("/api") && !isAnonymousEndpoint &&
        !(isApiKeyEndpoint && sessions.TryAuthenticateApiKey(context.Request)) &&
        !sessions.TryGetUser(context.Request, out _))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { message = "Authentication is required." });
        return;
    }

    await next(context);
});

await app.Services.GetRequiredService<JsonProxyStore>().InitializeAsync();

var api = app.MapGroup("/api");

api.MapPost("/auth/login", (LoginRequest request, ProxySessionService sessions, HttpContext context) =>
    sessions.TrySignIn(request.Token, context.Response)
        ? Results.Ok(new { authenticated = true })
        : Results.Unauthorized());

api.MapGet("/auth/session", (ProxySessionService sessions, HttpContext context) =>
    sessions.TryGetUser(context.Request, out var username)
        ? Results.Ok(new { authenticated = true, username })
        : Results.Unauthorized());

api.MapPost("/auth/logout", (ProxySessionService sessions, HttpContext context) =>
{
    sessions.SignOut(context.Response);
    return Results.NoContent();
});

api.MapGet("/health", async (JsonProxyStore store, ProxyPoolProfileManager profileManager,
    CancellationToken cancellationToken) =>
{
    var state = await store.ReadAsync(value => new
    {
        status = "ok",
        profile = profileManager.GetSummary(),
        proxies = value.Proxies.Count,
        sources = value.Sources.Count,
        value.UpdatedAt
    }, cancellationToken);
    return Results.Ok(state);
});

api.MapGet("/dashboard", async (ProxyPoolService pool, MaintenanceOperationQueue queue,
    ProxyPoolProfileManager profileManager,
    CancellationToken cancellationToken) =>
{
    var dashboard = await pool.GetDashboardAsync(cancellationToken);
    var operations = queue.GetState();
    return Results.Ok(dashboard with
    {
        Operations = dashboard.Operations with
        {
            ActiveOperation = operations.Active,
            LastOperation = operations.Last
        },
        Profile = profileManager.GetSummary()
    });
});

api.MapGet("/proxies", async ([AsParameters] ProxyQuery query, ProxyPoolService pool,
        CancellationToken cancellationToken) =>
{
    try
    {
        ValidateProxyQuery(query);
        return Results.Ok(await pool.GetProxiesAsync(query, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

api.MapPost("/proxies", async (ProxyCreateRequest request, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await pool.AddProxyAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

api.MapDelete("/proxies/{id:guid}", async (Guid id, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
    await pool.DeleteProxyAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

api.MapPost("/proxies/{id:guid}/check", async (Guid id, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
{
    var proxy = await pool.CheckProxyAsync(id, cancellationToken);
    return proxy is null ? Results.NotFound(new { message = "Proxy does not exist." }) : Results.Ok(proxy);
});

api.MapGet("/proxy/countries", async (string? protocol, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
{
    try
    {
        ValidateProtocol(protocol);
        return Results.Ok(await pool.GetAliveCountriesAsync(protocol, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

api.MapGet("/proxy/random", async (string? protocol, string? country, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
{
    try
    {
        ValidateProtocol(protocol);
        ValidateCountry(country);
        var proxy = await pool.GetRandomAliveProxyAsync(protocol, country, cancellationToken);
        return proxy is null
            ? Results.NotFound(new { message = "No matching live proxy is currently available." })
            : Results.Ok(proxy);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

api.MapGet("/proxy/plain", async (string? protocol, string? country, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
{
    try
    {
        ValidateProtocol(protocol);
        ValidateCountry(country);
        return Results.Text(await pool.ExportAliveAsync(protocol, country, cancellationToken), "text/plain; charset=utf-8");
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

api.MapGet("/sources", async (ProxyPoolService pool, CancellationToken cancellationToken) =>
    Results.Ok(await pool.GetSourcesAsync(cancellationToken)));

api.MapPost("/sources", async (SourceWriteRequest request, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await pool.AddSourceAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

api.MapPut("/sources/{id:guid}", async (Guid id, SourceWriteRequest request, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
{
    try
    {
        var source = await pool.UpdateSourceAsync(id, request, cancellationToken);
        return source is null ? Results.NotFound(new { message = "Source does not exist." }) : Results.Ok(source);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

api.MapDelete("/sources/{id:guid}", async (Guid id, ProxyPoolService pool,
    CancellationToken cancellationToken) =>
    await pool.DeleteSourceAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

api.MapGet("/settings/profile", (ProxyPoolProfileManager profileManager) =>
    Results.Ok(profileManager.GetSummary()));

api.MapPut("/settings/profile", (ProfileUpdateRequest request, ProxyPoolProfileManager profileManager,
    MaintenanceOperationQueue queue) =>
{
    if (string.IsNullOrWhiteSpace(request.Profile))
    {
        return Results.BadRequest(new { message = "profile is required." });
    }

    if (queue.GetState().Active is not null)
    {
        return Results.Conflict(new { message = "Wait for the active maintenance operation to finish before switching profile." });
    }

    return profileManager.TrySwitch(request.Profile, out var profile, out var error)
        ? Results.Ok(profile)
        : Results.BadRequest(new { message = error });
});

api.MapGet("/operations/{id:guid}", (Guid id, MaintenanceOperationQueue queue) =>
{
    var operation = queue.GetOperation(id);
    return operation is null ? Results.NotFound(new { message = "Operation does not exist." }) : Results.Ok(operation);
});

api.MapPost("/actions/scan", (MaintenanceOperationQueue queue) =>
    QueueOperation(queue, MaintenanceOperationKind.Scan));

api.MapPost("/actions/check", ([FromQuery] bool force, MaintenanceOperationQueue queue) =>
    QueueOperation(queue, MaintenanceOperationKind.Check, force));

api.MapPost("/actions/refresh", (MaintenanceOperationQueue queue) =>
    QueueOperation(queue, MaintenanceOperationKind.Refresh));

api.MapPost("/actions/prune", (MaintenanceOperationQueue queue) =>
    QueueOperation(queue, MaintenanceOperationKind.Prune));

app.Map("/api/{**path}", () => Results.NotFound(new { message = "API route does not exist." }));

app.Run();

static IResult QueueOperation(MaintenanceOperationQueue queue, MaintenanceOperationKind kind, bool force = false)
{
    var submission = queue.Enqueue(kind, force);
    return submission.Accepted
        ? Results.Accepted($"/api/operations/{submission.Operation.Id}", submission.Operation)
        : Results.Conflict(new
        {
            message = "Another maintenance operation is already queued or running.",
            operation = submission.Operation
        });
}

static void ValidateProxyQuery(ProxyQuery query)
{
    if (query.Page is < 1 or > 10_000)
    {
        throw new ArgumentException("page must be between 1 and 10000.");
    }

    if (query.PageSize is < 10 or > 200)
    {
        throw new ArgumentException("pageSize must be between 10 and 200.");
    }

    ValidateEnum<ProxyStatus>(query.Status, "status");
    ValidateEnum<ProxyProtocol>(query.Protocol, "protocol");
    ValidateCountry(query.Country);

    var allowedSorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "address", "protocol", "status", "latency", "successRate", "lastChecked", "firstSeen"
    };
    if (!string.IsNullOrWhiteSpace(query.Sort) && !allowedSorts.Contains(query.Sort))
    {
        throw new ArgumentException("sort is not supported.");
    }
}

static void ValidateProtocol(string? value) => ValidateEnum<ProxyProtocol>(value, "protocol");

static void ValidateCountry(string? value)
{
    if (!string.IsNullOrWhiteSpace(value) &&
        (value.Trim().Length != 2 || !value.Trim().All(char.IsAsciiLetter)))
    {
        throw new ArgumentException("country must be a two-letter ISO country code.");
    }
}

static void ValidateEnum<TEnum>(string? value, string parameterName) where TEnum : struct, Enum
{
    if (!string.IsNullOrWhiteSpace(value) && !Enum.TryParse<TEnum>(value, true, out _))
    {
        throw new ArgumentException($"{parameterName} is invalid.");
    }
}

static string ResolveProfile(IConfiguration configuration, string contentRootPath)
{
    var processProfile = Environment.GetEnvironmentVariable("PROXYSIU_PROFILE");
    if (!string.IsNullOrWhiteSpace(processProfile))
    {
        return processProfile.Trim();
    }

    var configuredProfile = configuration[$"{ProxyPoolOptions.SectionName}:Profile"];
    if (!string.IsNullOrWhiteSpace(configuredProfile))
    {
        return configuredProfile.Trim();
    }

    var dotenvPath = FindDotEnvPath(contentRootPath);
    if (dotenvPath is null)
    {
        return "high-throughput";
    }

    foreach (var rawLine in File.ReadLines(dotenvPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            continue;
        }

        var key = line[..separator].Trim();
        if (!key.Equals("PROXYSIU_PROFILE", StringComparison.OrdinalIgnoreCase) &&
            !key.Equals("ProxyPool__Profile", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        return line[(separator + 1)..].Trim().Trim('"', '\'');
    }

    return "high-throughput";
}

static IReadOnlyDictionary<string, string?> LoadDotEnvConfiguration(string contentRootPath)
{
    var dotenvPath = FindDotEnvPath(contentRootPath);
    if (dotenvPath is null)
    {
        return new Dictionary<string, string?>();
    }

    var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var rawLine in File.ReadLines(dotenvPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            continue;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim().Trim('"', '\'');
        switch (key.ToUpperInvariant())
        {
            case "PROXYSIU_ACCESS_TOKEN":
                configuration["ProxyAuth:AccessToken"] = value;
                break;
            case "PROXYSIU_COOKIE_SECURE":
                configuration["ProxyAuth:CookieSecure"] = value;
                break;
            case "PROXYSIU_PROFILE":
                configuration["ProxyPool:Profile"] = value;
                break;
            case "PROXYSIU_MAX_POOL_SIZE":
                configuration["ProxyPool:MaxPoolSize"] = value;
                break;
            case "PROXYSIU_REMOVE_UNSEEN_AFTER_HOURS":
                configuration["ProxyPool:RemoveUnseenAfterHours"] = value;
                break;
            case "PROXYSIU_GEOIP_USE_IP_SB":
                configuration["GeoIp:UseIpSb"] = value;
                break;
            case "PROXYSIU_GEOIP_IP_SB_LOOKUP_INTERVAL_SECONDS":
                configuration["GeoIp:IpSbLookupIntervalSeconds"] = value;
                break;
        }
    }

    return configuration;
}

static string? FindDotEnvPath(string contentRootPath)
{
    for (var directory = new DirectoryInfo(contentRootPath); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, ".env");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

public partial class Program;
