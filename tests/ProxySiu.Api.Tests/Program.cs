using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using ProxySiu.Api.Models;
using ProxySiu.Api.Contracts;
using ProxySiu.Api.Options;
using ProxySiu.Api.Services;
using ProxySiu.Api.Storage;

var tests = new (string Name, Func<Task> Run)[]
{
    ("parser filters private addresses and de-duplicates candidates", ParserFiltersAndDeduplicatesAsync),
    ("endpoint safety rejects non-public addresses", EndpointSafetyAsync),
    ("options validator enforces local-only configuration", OptionsValidatorAsync),
    ("access token issues a protected browser session and read key", AccessTokenAuthAsync),
    ("check planner isolates bootstrap and reserves steady-state quotas", CheckPlannerAsync),
    ("pool retention caps growth and removes stale unseen records", PoolRetentionAsync),
    ("alive country dictionary and country proxy selection stay scoped to live proxies", CountrySelectionAsync),
    ("maintenance queue accepts only one active operation", MaintenanceQueueAsync),
    ("json store preserves a recoverable backup", JsonStoreBackupAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static Task ParserFiltersAndDeduplicatesAsync()
{
    var options = Options.Create(new ProxyPoolOptions { AllowPrivateNetworks = false });
    var parser = new ProxyListParser(options);
    var result = parser.Parse("""
        8.8.8.8:8080
        http://8.8.8.8:8080
        socks5://1.1.1.1:1080
        10.0.0.8:3128
        invalid
        """, ProxyProtocol.Http, 10);

    Assert(result.Count == 2, "Expected two unique public candidates.");
    Assert(result.All(candidate => candidate.Host is not "10.0.0.8"), "Private candidates must be filtered.");
    Assert(result.Any(candidate => candidate.Protocol == ProxyProtocol.Socks5), "Explicit protocol must be retained.");
    return Task.CompletedTask;
}

static Task EndpointSafetyAsync()
{
    Assert(!EndpointSafety.IsPublicAddress(System.Net.IPAddress.Parse("127.0.0.1")), "Loopback must be private.");
    Assert(!EndpointSafety.IsPublicAddress(System.Net.IPAddress.Parse("10.1.2.3")), "RFC1918 address must be private.");
    Assert(!EndpointSafety.IsPublicAddress(System.Net.IPAddress.Parse("2001:db8::1")), "Documentation IPv6 range must be private.");
    Assert(EndpointSafety.IsPublicAddress(System.Net.IPAddress.Parse("8.8.8.8")), "Public IPv4 must be allowed.");
    return Task.CompletedTask;
}

static Task OptionsValidatorAsync()
{
    var validator = new ProxyPoolOptionsValidator();
    var valid = validator.Validate(null, new ProxyPoolOptions
    {
        CheckUrl = "https://example.com/check",
        RequestTimeoutSeconds = 6,
        DownloadTimeoutSeconds = 20,
        CheckConcurrency = 36,
        SourceConcurrency = 3,
        ScanIntervalMinutes = 120,
        CheckIntervalMinMinutes = 5,
        CheckIntervalMaxMinutes = 15,
        RecheckAliveMinutes = 30,
        RecheckDeadMinutes = 180,
        MaxChecksPerCycle = 400,
        MaxCandidatesPerSource = 800,
        MaxSourceBytes = 2_000_000,
        MaxConsecutiveFailures = 3,
        RemoveDeadAfterHours = 24
    });
    Assert(!valid.Failed, "Known valid local configuration must pass.");

    var remote = validator.Validate(null, new ProxyPoolOptions { AllowRemoteAccess = true });
    Assert(remote.Failed, "Remote access must fail validation in the local profile.");

    var profiled = new ProxyPoolOptions
    {
        Profiles = new Dictionary<string, ProxyPoolProfile>
        {
            ["idc-safe"] = new()
            {
                CheckConcurrency = 10,
                MaxChecksPerCycle = 100,
                CheckIntervalMinMinutes = 15,
                CheckIntervalMaxMinutes = 30,
                AliveChecksPerCycle = 50,
                PendingChecksPerCycle = 40,
                DeadChecksPerCycle = 10
            }
        }
    };
    ProxyPoolProfileSelector.Apply(profiled, "IDC-SAFE");
    Assert(profiled.Profile == "IDC-SAFE" && profiled.CheckConcurrency == 10 &&
           profiled.MaxChecksPerCycle == 100 && profiled.CheckIntervalMinMinutes == 15 &&
           profiled.CheckIntervalMaxMinutes == 30,
        "Profile selection must override the runtime check rate.");
    return Task.CompletedTask;
}

static Task AccessTokenAuthAsync()
{
    const string accessToken = "test-token-that-is-long-enough-123456";
    var validator = new ProxyAuthOptionsValidator();
    Assert(validator.Validate(null, new ProxyAuthOptions { Enabled = true, AccessToken = accessToken }).Succeeded,
        "A sufficiently long access token must be accepted.");
    Assert(validator.Validate(null, new ProxyAuthOptions { Enabled = true, AccessToken = "too-short" }).Failed,
        "A short access token must be rejected.");

    var services = new ServiceCollection();
    services.AddDataProtection();
    var serviceProvider = services.BuildServiceProvider();
    var sessions = new ProxySessionService(
        Options.Create(new ProxyAuthOptions { Enabled = true, AccessToken = accessToken, CookieSecure = false }),
        serviceProvider.GetRequiredService<IDataProtectionProvider>());
    var signIn = new DefaultHttpContext();
    Assert(sessions.TrySignIn(accessToken, signIn.Response), "The configured token must sign in.");
    Assert(!sessions.TrySignIn("wrong-token", signIn.Response), "A different token must not sign in.");

    var cookieHeader = signIn.Response.Headers.SetCookie.SingleOrDefault();
    Assert(!string.IsNullOrWhiteSpace(cookieHeader), "Signing in must issue a cookie.");
    var cookiePair = cookieHeader!.Split(';', 2)[0];
    var sessionRequest = new DefaultHttpContext();
    sessionRequest.Request.Headers.Cookie = cookiePair;
    Assert(sessions.TryGetUser(sessionRequest.Request, out _), "The protected session cookie must authenticate.");

    var apiRequest = new DefaultHttpContext();
    apiRequest.Request.Headers.Authorization = $"Bearer {accessToken}";
    Assert(sessions.TryAuthenticateApiKey(apiRequest.Request), "Bearer token must authenticate proxy-read API access.");
    apiRequest.Request.Headers.Authorization = "Bearer incorrect";
    Assert(!sessions.TryAuthenticateApiKey(apiRequest.Request), "An incorrect bearer token must be rejected.");
    return Task.CompletedTask;
}

static Task MaintenanceQueueAsync()
{
    var queue = new MaintenanceOperationQueue();
    var first = queue.Enqueue(MaintenanceOperationKind.Scan);
    var second = queue.Enqueue(MaintenanceOperationKind.Check);

    Assert(first.Accepted, "The first operation must be accepted.");
    Assert(!second.Accepted, "A second operation must be rejected while one is queued.");
    Assert(second.Operation.Id == first.Operation.Id, "The conflict must identify the active operation.");
    return Task.CompletedTask;
}

static Task CheckPlannerAsync()
{
    var options = Options.Create(new ProxyPoolOptions
    {
        CheckConcurrency = 36,
        MaxChecksPerCycle = 400,
        AliveChecksPerCycle = 120,
        PendingChecksPerCycle = 200,
        DeadChecksPerCycle = 80,
        RecheckAliveMinutes = 30,
        RecheckDeadMinutes = 60,
        SecondDeadRetryMinutes = 360
    });
    var profileManager = new ProxyPoolProfileManager(options.Value, new ProxyPoolOptionsValidator());
    var environment = new TestHostEnvironment(Path.GetTempPath());
    var pool = new ProxyPoolService(
        new JsonProxyStore(options, environment, NullLogger<JsonProxyStore>.Instance),
        new ProxyListParser(options),
        new ProxyChecker(profileManager, NullLogger<ProxyChecker>.Instance),
        new TestHttpClientFactory(), profileManager, NullLogger<ProxyPoolService>.Instance);
    var now = DateTimeOffset.UtcNow;

    var bootstrap = new ProxyPoolState
    {
        Proxies = Enumerable.Range(0, 500).Select(index => NewProxy($"8.8.4.{index % 250}", 10_000 + index))
            .Append(AliveProxy("1.1.1.1", 8080, now.AddHours(-1)))
            .ToList()
    };
    var bootstrapSelection = Plan(pool, bootstrap, false, now);
    Assert(bootstrapSelection.Count == 400 && bootstrapSelection.All(proxy => proxy.Status == ProxyStatus.Pending),
        "Bootstrap must spend the whole batch on first checks.");

    var steady = new ProxyPoolState
    {
        InitialSweepCompleted = true,
        Proxies = Enumerable.Range(0, 150).Select(index => AliveProxy($"9.9.0.{index}", 10_000 + index,
                now.AddHours(-1)))
            .Concat(Enumerable.Range(0, 250).Select(index => NewProxy($"8.8.8.{index}", 20_000 + index)))
            .Concat(Enumerable.Range(0, 100).Select(index => DeadProxy($"4.4.4.{index}", 30_000 + index,
                now.AddHours(-2))))
            .ToList()
    };
    var steadySelection = Plan(pool, steady, false, now);
    Assert(steadySelection.Count == 400, "Steady-state selection must fill the batch.");
    Assert(steadySelection.Count(proxy => proxy.Status == ProxyStatus.Alive) == 120, "Alive reserve must be honored.");
    Assert(steadySelection.Count(proxy => proxy.Status == ProxyStatus.Pending) == 200, "Pending reserve must be honored.");
    Assert(steadySelection.Count(proxy => proxy.Status == ProxyStatus.Dead) == 80, "Dead reserve must be honored.");
    return Task.CompletedTask;
}

static async Task PoolRetentionAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "pool.json");
        var options = Options.Create(new ProxyPoolOptions
        {
            DataFile = file,
            MaxPoolSize = 3,
            RemoveUnseenAfterHours = 12,
            RemoveDeadAfterHours = 24 * 365,
            MaxConsecutiveFailures = 3
        });
        var environment = new TestHostEnvironment(directory);
        var store = new JsonProxyStore(options, environment, NullLogger<JsonProxyStore>.Instance);
        var profileManager = new ProxyPoolProfileManager(options.Value, new ProxyPoolOptionsValidator());
        var pool = new ProxyPoolService(store, new ProxyListParser(options),
            new ProxyChecker(profileManager, NullLogger<ProxyChecker>.Instance), new TestHttpClientFactory(),
            profileManager, NullLogger<ProxyPoolService>.Instance);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var healthy = AliveProxy("1.1.1.1", 1001, now);
        var mostFailed = DeadProxy("2.2.2.2", 1002, now);
        mostFailed.ConsecutiveFailures = 8;
        var olderDead = DeadProxy("3.3.3.3", 1003, now.AddHours(-2));
        olderDead.ConsecutiveFailures = 1;
        var pendingOne = NewProxy("4.4.4.4", 1004);
        var pendingTwo = NewProxy("5.5.5.5", 1005);
        await store.WriteAsync(state =>
        {
            state.Proxies.AddRange([healthy, mostFailed, olderDead, pendingOne, pendingTwo]);
            return 0;
        });

        await pool.PruneAsync(CancellationToken.None);
        var capped = await store.ReadAsync(state => state.Proxies.ToList());
        Assert(capped.Count == 3, "The retention limit must cap the pool.");
        Assert(capped.Any(proxy => proxy.Id == healthy.Id), "A live proxy must be retained before dead proxies.");
        Assert(capped.All(proxy => proxy.Id != mostFailed.Id && proxy.Id != olderDead.Id),
            "Dead proxies with more failures or older sightings must be evicted first.");

        var stalePending = NewProxy("6.6.6.6", 1006);
        stalePending.LastSeenAt = now.AddHours(-13);
        var staleDead = DeadProxy("7.7.7.7", 1007, now.AddHours(-13));
        staleDead.LastSeenAt = now.AddHours(-13);
        await store.WriteAsync(state =>
        {
            state.Proxies.Clear();
            state.Proxies.AddRange([healthy, stalePending, staleDead]);
            return 0;
        });

        await pool.PruneAsync(CancellationToken.None);
        var retained = await store.ReadAsync(state => state.Proxies.ToList());
        Assert(retained.Count == 1 && retained[0].Id == healthy.Id,
            "Unseen dead and pending records must be pruned before the normal dead-record timeout.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static async Task CountrySelectionAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "pool.json");
        var options = Options.Create(new ProxyPoolOptions { DataFile = file });
        var environment = new TestHostEnvironment(directory);
        var store = new JsonProxyStore(options, environment, NullLogger<JsonProxyStore>.Instance);
        var profileManager = new ProxyPoolProfileManager(options.Value, new ProxyPoolOptionsValidator());
        var pool = new ProxyPoolService(store, new ProxyListParser(options),
            new ProxyChecker(profileManager, NullLogger<ProxyChecker>.Instance), new TestHttpClientFactory(),
            profileManager, NullLogger<ProxyPoolService>.Instance);
        await store.InitializeAsync();

        var us = AliveProxy("1.1.1.1", 8080, DateTimeOffset.UtcNow);
        us.GeoLocation = new IpGeoLocation("US", "United States", null, null, null);
        var cn = AliveProxy("2.2.2.2", 8080, DateTimeOffset.UtcNow);
        cn.GeoLocation = new IpGeoLocation("CN", "China", null, null, null);
        var dead = DeadProxy("3.3.3.3", 8080, DateTimeOffset.UtcNow);
        dead.GeoLocation = new IpGeoLocation("US", "United States", null, null, null);
        await store.WriteAsync(state =>
        {
            state.Proxies.AddRange([us, cn, dead]);
            return 0;
        });

        var countries = await pool.GetAliveCountriesAsync(null, CancellationToken.None);
        Assert(countries.Count == 2 && countries.Single(country => country.Code == "US").Count == 1,
            "Country dictionary must include only live proxies.");
        var selected = await pool.GetRandomAliveProxyAsync("http", "US", CancellationToken.None);
        Assert(selected?.Id == us.Id, "Country-filtered selection must return the requested live country.");
        var exported = await pool.ExportAliveAsync(null, "CN", CancellationToken.None);
        Assert(exported == "2.2.2.2:8080", "Country-filtered export must contain only matching live proxies.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static async Task JsonStoreBackupAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "pool.json");
        var options = Options.Create(new ProxyPoolOptions { DataFile = file });
        var environment = new TestHostEnvironment(directory);
        var store = new JsonProxyStore(options, environment, NullLogger<JsonProxyStore>.Instance);
        await store.InitializeAsync();

        await store.WriteAsync(state =>
        {
            state.Proxies.Add(NewProxy("8.8.8.8", 8080));
            return 0;
        });
        await store.WriteAsync(state =>
        {
            state.Proxies.Add(NewProxy("1.1.1.1", 8080));
            return 0;
        });

        Assert(File.Exists($"{file}.bak"), "Every pool must have a backup after writing.");
        await File.WriteAllTextAsync(file, "{");

        var recoveredStore = new JsonProxyStore(options, environment, NullLogger<JsonProxyStore>.Instance);
        await recoveredStore.InitializeAsync();
        var recoveredCount = await recoveredStore.ReadAsync(state => state.Proxies.Count);
        Assert(recoveredCount == 1, "Corrupt primary data must recover the previous valid backup.");

        await File.WriteAllTextAsync(file, "{");
        var recoveredAgainStore = new JsonProxyStore(options, environment, NullLogger<JsonProxyStore>.Instance);
        await recoveredAgainStore.InitializeAsync();
        var recoveredAgainCount = await recoveredAgainStore.ReadAsync(state => state.Proxies.Count);
        Assert(recoveredAgainCount == 1, "Recovery must preserve a valid backup for a later failure.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static ProxyRecord NewProxy(string host, int port) => new()
{
    Host = host,
    Port = port,
    Protocol = ProxyProtocol.Http
};

static ProxyRecord AliveProxy(string host, int port, DateTimeOffset checkedAt) => new()
{
    Host = host,
    Port = port,
    Protocol = ProxyProtocol.Http,
    Status = ProxyStatus.Alive,
    LastCheckedAt = checkedAt
};

static ProxyRecord DeadProxy(string host, int port, DateTimeOffset checkedAt) => new()
{
    Host = host,
    Port = port,
    Protocol = ProxyProtocol.Http,
    Status = ProxyStatus.Dead,
    LastCheckedAt = checkedAt,
    ConsecutiveFailures = 1
};

static IReadOnlyList<ProxyRecord> Plan(ProxyPoolService pool, ProxyPoolState state, bool force,
    DateTimeOffset now)
{
    var method = typeof(ProxyPoolService).GetMethod("SelectProxiesForCheck",
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Planner not found.");
    return (IReadOnlyList<ProxyRecord>)method.Invoke(pool, [state, force, now])!;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "ProxySiu.Api.Tests";
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
}

sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
