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
    ("initial sweep status tracks pending candidates", InitialSweepStatusAsync),
    ("pool capacity stays bounded and removes stale unseen records", PoolRetentionAsync),
    ("new candidates enter a full pool without displacing recovery dead records", PartitionedAdmissionAsync),
    ("alive country dictionary and country proxy selection stay scoped to live proxies", CountrySelectionAsync),
    ("maintenance queue accepts only one active operation", MaintenanceQueueAsync),
    ("existing pools merge new built-in source seeds without overwriting edits", BuiltInSourceSeedMergeAsync),
    ("json store preserves a recoverable backup", JsonStoreBackupAsync),
    ("idle maintenance does not rewrite pool data or backups", IdleMaintenanceAsync),
    ("streamed responses enforce the byte limit without buffering the full body", LimitedResponseAsync),
    ("geoip queue retries overflow and bounds completed lookup retention", GeoIpRetentionAsync),
    ("geoip enrichment batches saves and flushes on graceful shutdown", GeoIpBatchPersistenceAsync),
    ("parser stops allocating after the requested candidate limit", ParserAllocationAsync),
    ("cancelled saves are retried even when the next update changes nothing", CancelledSaveRetryAsync)
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

static Task ParserAllocationAsync()
{
    var parser = new ProxyListParser(Options.Create(new ProxyPoolOptions()));
    parser.Parse("8.8.8.8:8080", ProxyProtocol.Http, 1);
    var content = string.Join('\n', Enumerable.Repeat("8.8.8.8:8080", 100_000));
    var before = GC.GetAllocatedBytesForCurrentThread();
    var result = parser.Parse(content, ProxyProtocol.Http, 1);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Assert(result.Count == 1, "The requested candidate must still be parsed.");
    Assert(allocated < 64 * 1024, "A one-candidate parse must not allocate strings for the rest of the source.");
    Console.WriteLine($"INFO parser: {content.Length:N0} input characters, {allocated:N0} allocated bytes at limit=1");
    return Task.CompletedTask;
}

static async Task IdleMaintenanceAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "pool.json");
        var options = Options.Create(new ProxyPoolOptions { DataFile = file });
        var store = new JsonProxyStore(options, new TestHostEnvironment(directory), NullLogger<JsonProxyStore>.Instance);
        var profileManager = new ProxyPoolProfileManager(options.Value, new ProxyPoolOptionsValidator());
        var pool = new ProxyPoolService(store, new ProxyListParser(options),
            new ProxyChecker(profileManager, NullLogger<ProxyChecker>.Instance), new TestHttpClientFactory(),
            profileManager, NullLogger<ProxyPoolService>.Instance);
        await store.WriteAsync(state =>
        {
            state.Proxies.AddRange(Enumerable.Range(0, 6000)
                .Select(index => AliveProxy("8.8.8.8", 1000 + index, DateTimeOffset.UtcNow)));
            return 0;
        });
        var primary = await File.ReadAllBytesAsync(file);
        var backup = await File.ReadAllBytesAsync($"{file}.bak");
        var updatedAt = await store.ReadAsync(state => state.UpdatedAt);
        var marker = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, marker);
        File.SetLastWriteTimeUtc($"{file}.bak", marker);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            Assert(await pool.TrimExcessPendingAsync(CancellationToken.None) == 0, "No pending proxies should be removed.");
        }
        await pool.PruneAsync(CancellationToken.None);
        await pool.DeleteProxyAsync(Guid.NewGuid(), CancellationToken.None);
        await pool.DeleteSourceAsync(Guid.NewGuid(), CancellationToken.None);
        Assert(File.GetLastWriteTimeUtc(file) == marker && File.GetLastWriteTimeUtc($"{file}.bak") == marker,
            "No-op maintenance must not rewrite either the primary file or the backup.");
        var primaryAfter = await File.ReadAllBytesAsync(file);
        var backupAfter = await File.ReadAllBytesAsync($"{file}.bak");
        Assert(primary.SequenceEqual(primaryAfter) && backup.SequenceEqual(backupAfter),
            "No-op maintenance must preserve bytes.");
        Assert(updatedAt == await store.ReadAsync(state => state.UpdatedAt), "No-op maintenance must preserve UpdatedAt.");

        using var document = System.Text.Json.JsonDocument.Parse(primary);
        var indented = System.Text.Json.JsonSerializer.Serialize(document.RootElement,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"INFO 6000-proxy fixture: compact={primary.Length:N0} bytes, indented={System.Text.Encoding.UTF8.GetByteCount(indented):N0} bytes; 100 idle sweeps left both files untouched");

        await store.WriteAsync(state =>
        {
            state.Quarantines.Add(new ProxyQuarantine { Key = "expired", ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) });
            return 0;
        });
        await pool.PruneAsync(CancellationToken.None);
        var reloaded = new JsonProxyStore(options, new TestHostEnvironment(directory), NullLogger<JsonProxyStore>.Instance);
        Assert(await reloaded.ReadAsync(state => state.Quarantines.Count) == 0,
            "Removing only expired quarantine entries must still be persisted.");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task LimitedResponseAsync()
{
    using var encoded = new StringContent("测试", System.Text.Encoding.Unicode);
    Assert(await LimitedHttpContent.ReadStringAsync(encoded, 1024, CancellationToken.None) == "测试",
        "Declared response encoding must still be honored.");
    using var bom = new ByteArrayContent([0xef, 0xbb, 0xbf, (byte)'{', (byte)'}']);
    Assert(await LimitedHttpContent.ReadStringAsync(bom, 1024, CancellationToken.None) == "{}",
        "A UTF-8 BOM must not interfere with JSON parsing.");
    using var exact = new StringContent(new string('x', 1024));
    Assert((await LimitedHttpContent.ReadStringAsync(exact, 1024, CancellationToken.None)).Length == 1024,
        "A response exactly at the byte limit must be accepted.");
    using var declared = new StringContent(new string('x', 1025));
    try
    {
        await LimitedHttpContent.ReadStringAsync(declared, 1024, CancellationToken.None);
        throw new Exception("A declared oversized response must be rejected.");
    }
    catch (InvalidOperationException) { }

    using var stream = new NonSeekableReadStream(new byte[2_000_000]);
    using var streamed = new StreamContent(stream);
    Assert(streamed.Headers.ContentLength is null, "The streaming fixture must have no declared length.");
    try
    {
        await LimitedHttpContent.ReadStringAsync(streamed, 1024, CancellationToken.None);
        throw new Exception("A streamed oversized response must be rejected.");
    }
    catch (InvalidOperationException) { }
    Assert(stream.BytesRead <= 1025, "Reject streamed overflow before reading or allocating the full response.");
}

static Task GeoIpRetentionAsync()
{
    using var service = new GeoIpService(Options.Create(new GeoIpOptions { UseIpSb = true }),
        new TestHttpClientFactory(), NullLogger<GeoIpService>.Instance);
    service.QueueRemoteLookup("8.8.8.8");
    service.QueueRemoteLookup("8.8.8.8");
    Assert(service.TryReadRemoteLookup(out var first) && first == "8.8.8.8" && !service.TryReadRemoteLookup(out _),
        "Pending IPs must be deduplicated.");
    service.CompleteRemoteLookup(first, keepCached: false);
    service.QueueRemoteLookup(first);
    Assert(service.TryReadRemoteLookup(out _), "Failed IP lookups must be retryable.");
    service.CompleteRemoteLookup(first, keepCached: true);
    service.QueueRemoteLookup(first);
    Assert(!service.TryReadRemoteLookup(out _), "Completed lookups must be cached.");

    for (var index = 0; index < 10_000; index++)
    {
        service.QueueRemoteLookup($"11.0.{index / 256}.{index % 256}");
    }
    service.QueueRemoteLookup("12.1.1.1");
    Assert(service.TryReadRemoteLookup(out var drained), "The full queue must contain entries.");
    service.CompleteRemoteLookup(drained, keepCached: true);
    service.QueueRemoteLookup("12.1.1.1");
    var foundOverflowRetry = false;
    while (service.TryReadRemoteLookup(out var ip))
    {
        foundOverflowRetry |= ip == "12.1.1.1";
        service.CompleteRemoteLookup(ip, keepCached: true);
    }
    Assert(foundOverflowRetry, "An IP rejected by a full queue must remain retryable once space is available.");
    var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)typeof(GeoIpService)
        .GetField("_remoteQueued", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    var completed = (Microsoft.Extensions.Caching.Memory.MemoryCache)typeof(GeoIpService)
        .GetField("_completedLookups", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    Assert(pending.IsEmpty, "Completed IPs must not accumulate in pending deduplication storage.");
    Assert(completed.Count <= 10_000, "The completed lookup cache must remain bounded.");
    return Task.CompletedTask;
}

static async Task CancelledSaveRetryAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var options = Options.Create(new ProxyPoolOptions { DataFile = Path.Combine(directory, "pool.json") });
        var store = new JsonProxyStore(options, new TestHostEnvironment(directory), NullLogger<JsonProxyStore>.Instance);
        await store.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await store.WriteIfChangedAsync(state =>
            {
                state.Proxies.Add(NewProxy("8.8.8.8", 8080));
                cancellation.Cancel();
                return (0, true);
            }, cancellation.Token);
            throw new Exception("The cancelled save must fail.");
        }
        catch (OperationCanceledException) { }
        await store.WriteIfChangedAsync(_ => (0, false));
        var reloaded = new JsonProxyStore(options, new TestHostEnvironment(directory), NullLogger<JsonProxyStore>.Instance);
        Assert(await reloaded.ReadAsync(state => state.Proxies.Count) == 1,
            "A no-op retry must flush a previously interrupted mutation.");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task GeoIpBatchPersistenceAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "pool.json");
        var options = Options.Create(new ProxyPoolOptions { DataFile = file });
        var store = new JsonProxyStore(options, new TestHostEnvironment(directory), NullLogger<JsonProxyStore>.Instance);
        string[] ips = ["8.8.8.8", "1.1.1.1", "9.9.9.9"];
        await store.WriteAsync(state =>
        {
            state.Proxies.AddRange(ips.Select(ip =>
            {
                var proxy = AliveProxy(ip, 8080, DateTimeOffset.UtcNow);
                proxy.ExitIp = ip;
                return proxy;
            }));
            return 0;
        });
        using var service = new GeoIpService(Options.Create(new GeoIpOptions { IpSbLookupIntervalSeconds = 1 }),
            new StaticHttpClientFactory("""{"country_code":"AU","country":"Australia"}"""),
            NullLogger<GeoIpService>.Instance);
        foreach (var ip in ips) service.QueueRemoteLookup(ip);
        using var worker = new GeoIpEnrichmentWorker(service, store, NullLogger<GeoIpEnrichmentWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!await store.ReadAsync(state => state.Proxies.All(proxy => proxy.GeoLocation?.CountryCode == "AU"), timeout.Token))
            {
                await Task.Delay(25, timeout.Token);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        Assert(worker.ExecuteTask?.IsFaulted != true,
            $"The batch worker must remain healthy after draining the queue: {worker.ExecuteTask?.Exception}");
        using var backup = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync($"{file}.bak"));
        Assert(backup.RootElement.GetProperty("proxies").EnumerateArray()
            .All(proxy => proxy.GetProperty("geoLocation").ValueKind == System.Text.Json.JsonValueKind.Null),
            "All three locations must be saved together so the backup contains the state before enrichment.");

        // Block the second lookup after the first has completed, then stop the worker mid-batch.
        var shutdownFactory = new BlockingSecondLookupFactory();
        using var shutdownService = new GeoIpService(Options.Create(new GeoIpOptions { IpSbLookupIntervalSeconds = 1 }),
            shutdownFactory, NullLogger<GeoIpService>.Instance);
        await store.WriteAsync(state =>
        {
            foreach (var proxy in state.Proxies) proxy.GeoLocation = null;
            return 0;
        });
        foreach (var ip in ips.Take(2)) shutdownService.QueueRemoteLookup(ip);
        using var shutdownWorker = new GeoIpEnrichmentWorker(shutdownService, store,
            NullLogger<GeoIpEnrichmentWorker>.Instance);
        await shutdownWorker.StartAsync(CancellationToken.None);
        try
        {
            await shutdownFactory.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await shutdownWorker.StopAsync(CancellationToken.None);
        }
        Assert(shutdownWorker.ExecuteTask?.IsFaulted != true,
            $"Graceful shutdown must not fault the worker: {shutdownWorker.ExecuteTask?.Exception}");
        var reloaded = new JsonProxyStore(options, new TestHostEnvironment(directory), NullLogger<JsonProxyStore>.Instance);
        Assert(await reloaded.ReadAsync(state => state.Proxies.Single(proxy => proxy.ExitIp == ips[0])
                .GeoLocation?.CountryCode) == "AU", "Graceful shutdown must flush the first completed lookup.");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
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
        InitialSweepCompleted = true,
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
        Proxies = Enumerable.Range(0, 250).Select(index => AliveProxy($"9.9.0.{index}", 10_000 + index,
                now.AddHours(-1)))
            .Concat(Enumerable.Range(0, 250).Select(index => DeadProxy($"4.4.4.{index}", 30_000 + index,
                now.AddHours(-2))))
            .ToList()
    };
    var steadySelection = Plan(pool, steady, false, now);
    Assert(steadySelection.Count == 400, "Steady-state selection must fill the batch.");
    Assert(steadySelection.Count(proxy => proxy.Status == ProxyStatus.Alive) >= 120,
        "Alive reserve must be honored after the pending backlog is clear.");
    Assert(steadySelection.Count(proxy => proxy.Status == ProxyStatus.Dead) >= 80,
        "Dead reserve must be honored after the pending backlog is clear.");
    return Task.CompletedTask;
}

static async Task InitialSweepStatusAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var options = Options.Create(new ProxyPoolOptions
        {
            DataFile = Path.Combine(directory, "pool.json"),
            MaxPendingProxies = 2
        });
        var environment = new TestHostEnvironment(directory);
        var store = new JsonProxyStore(options, environment, NullLogger<JsonProxyStore>.Instance);
        var profileManager = new ProxyPoolProfileManager(options.Value, new ProxyPoolOptionsValidator());
        var pool = new ProxyPoolService(store, new ProxyListParser(options),
            new ProxyChecker(profileManager, NullLogger<ProxyChecker>.Instance), new TestHttpClientFactory(),
            profileManager, NullLogger<ProxyPoolService>.Instance);
        await store.InitializeAsync();

        Assert(!await pool.HasPendingAsync(CancellationToken.None),
            "An empty pool must not block scheduled scans.");
        await store.WriteAsync(state =>
        {
            state.Proxies.Add(NewProxy("8.8.8.8", 8080));
            state.Proxies.Add(NewProxy("1.1.1.1", 8080));
            state.Proxies.Add(NewProxy("9.9.9.9", 8080));
            return 0;
        });
        Assert(await pool.HasPendingAsync(CancellationToken.None),
            "Pending proxies must keep the initial sweep active.");
        await store.WriteAsync(state =>
        {
            state.InitialSweepCompleted = true;
            return 0;
        });
        Assert(await pool.HasPendingAsync(CancellationToken.None),
            "New pending proxies must be drained immediately even after the initial sweep completed.");
        Assert(await pool.TrimExcessPendingAsync(CancellationToken.None) == 1,
            "The pending buffer must remove excess automatically discovered candidates.");
        Assert(await store.ReadAsync(state => state.Proxies.Count) == 2,
            "The pending buffer must retain only its configured capacity.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
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
            MaxPoolSize = 4,
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
        var recovering = DeadProxy("2.2.2.2", 1002, now);
        recovering.LastAliveAt = now.AddHours(-1);
        recovering.ConsecutiveFailures = 1;
        var mostFailed = DeadProxy("3.3.3.3", 1003, now);
        mostFailed.ConsecutiveFailures = 8;
        var neverAlive = DeadProxy("4.4.4.4", 1004, now.AddHours(-2));
        neverAlive.ConsecutiveFailures = 1;
        var pendingOne = NewProxy("5.5.5.5", 1005);
        await store.WriteAsync(state =>
        {
            state.Proxies.AddRange([healthy, recovering, mostFailed, neverAlive, pendingOne]);
            return 0;
        });

        await pool.PruneAsync(CancellationToken.None);
        var capped = await store.ReadAsync(state => state.Proxies.ToList());
        Assert(capped.Count == 4, "The total pool limit must cap the pool.");
        Assert(capped.Any(proxy => proxy.Id == healthy.Id), "A live proxy must be retained before dead proxies.");
        Assert(capped.Any(proxy => proxy.Id == recovering.Id),
            "A proxy that was recently alive must retain a recovery retry slot.");
        Assert(capped.All(proxy => proxy.Id != mostFailed.Id),
            "Repeatedly failing dead proxies must be evicted before pending candidates when total capacity is exceeded.");

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

static async Task PartitionedAdmissionAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "pool.json");
        var options = Options.Create(new ProxyPoolOptions
        {
            DataFile = file,
            MaxPoolSize = 6,
            MaxCandidatesPerSource = 10
        });
        var environment = new TestHostEnvironment(directory);
        var store = new JsonProxyStore(options, environment, NullLogger<JsonProxyStore>.Instance);
        var profileManager = new ProxyPoolProfileManager(options.Value, new ProxyPoolOptionsValidator());
        var pool = new ProxyPoolService(store, new ProxyListParser(options),
            new ProxyChecker(profileManager, NullLogger<ProxyChecker>.Instance),
            new StaticHttpClientFactory("9.9.9.1:8080"), profileManager,
            NullLogger<ProxyPoolService>.Instance);
        await store.InitializeAsync();

        var recovering = DeadProxy("1.1.1.1", 8080, DateTimeOffset.UtcNow);
        recovering.LastAliveAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        recovering.ConsecutiveFailures = 1;
        var repeatedlyFailing = DeadProxy("2.2.2.2", 8080, DateTimeOffset.UtcNow);
        repeatedlyFailing.ConsecutiveFailures = 8;
        await store.WriteAsync(state =>
        {
            state.Proxies.AddRange([recovering, repeatedlyFailing,
                AliveProxy("3.3.3.3", 8080, DateTimeOffset.UtcNow),
                AliveProxy("4.4.4.4", 8080, DateTimeOffset.UtcNow),
                AliveProxy("5.5.5.5", 8080, DateTimeOffset.UtcNow),
                AliveProxy("6.6.6.6", 8080, DateTimeOffset.UtcNow)]);
            state.Sources.Add(new ProxySource
            {
                Name = "test",
                Url = "https://8.8.8.8/proxies.txt",
                Protocol = ProxyProtocol.Http
            });
            return 0;
        });

        var scan = await pool.ScanAsync(CancellationToken.None);
        var stateAfterScan = await store.ReadAsync(state => state.Proxies.ToList());
        Assert(scan.Added == 1, "A new candidate must enter even when the pool is full.");
        Assert(stateAfterScan.Count == 6, "The total pool must remain capped at its configured maximum.");
        Assert(stateAfterScan.Any(proxy => proxy.Id == recovering.Id),
            "New candidates must not displace a recovery dead proxy.");
        Assert(stateAfterScan.All(proxy => proxy.Id != repeatedlyFailing.Id),
            "Repeatedly failing dead proxies must make room before recovery candidates.");
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
        var usSecond = AliveProxy("1.1.1.2", 8080, DateTimeOffset.UtcNow);
        usSecond.GeoLocation = new IpGeoLocation("US", "United States", null, null, null);
        var usThird = AliveProxy("1.1.1.3", 8080, DateTimeOffset.UtcNow);
        usThird.GeoLocation = new IpGeoLocation("US", "United States", null, null, null);
        var usFourth = AliveProxy("1.1.1.4", 8080, DateTimeOffset.UtcNow);
        usFourth.GeoLocation = new IpGeoLocation("US", "United States", null, null, null);
        var cn = AliveProxy("2.2.2.2", 8080, DateTimeOffset.UtcNow);
        cn.GeoLocation = new IpGeoLocation("CN", "China", null, null, null);
        var dead = DeadProxy("3.3.3.3", 8080, DateTimeOffset.UtcNow);
        dead.GeoLocation = new IpGeoLocation("US", "United States", null, null, null);
        await store.WriteAsync(state =>
        {
            state.Proxies.AddRange([us, usSecond, usThird, usFourth, cn, dead]);
            return 0;
        });

        var countries = await pool.GetAliveCountriesAsync(null, CancellationToken.None);
        Assert(countries.Count == 2 && countries.Single(country => country.Code == "US").Count == 4,
            "Country dictionary must include only live proxies.");
        var selected = await pool.GetRandomAliveProxyAsync("http", "US", CancellationToken.None);
        Assert(selected?.GeoLocation?.CountryCode == "US", "Country-filtered selection must return the requested live country.");
        var defaultSelected = await pool.GetRandomAliveProxyAsync("http", null, CancellationToken.None);
        Assert(defaultSelected?.GeoLocation?.CountryCode != "US",
            "Default proxy selection must exclude US proxies.");
        var selectedBatch = await pool.GetRandomAliveProxiesAsync("http", "US", 10, CancellationToken.None);
        Assert(selectedBatch.Count == 4 && selectedBatch.All(proxy => proxy.GeoLocation?.CountryCode == "US") &&
               selectedBatch.Select(proxy => proxy.Id).Distinct().Count() == 4,
            "Batch selection must return each matching live proxy at most once.");
        var exported = await pool.ExportAliveAsync(null, "CN", CancellationToken.None);
        Assert(exported == "2.2.2.2:8080", "Country-filtered export must contain only matching live proxies.");
        var defaultExport = await pool.ExportAliveAsync(null, null, CancellationToken.None);
        Assert(defaultExport == "2.2.2.2:8080", "Default proxy export must exclude US proxies.");
        var page = await pool.GetProxiesAsync(new ProxyQuery { Country = "CN", Page = 1, PageSize = 30 },
            CancellationToken.None);
        Assert(page.Total == 1 && page.Items.Single().Id == cn.Id,
            "The management proxy list must filter records by country.");
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

static async Task BuiltInSourceSeedMergeAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxySiu-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "pool.json");
        var environment = new TestHostEnvironment(directory);
        var firstOptions = Options.Create(new ProxyPoolOptions
        {
            DataFile = file,
            Sources =
            [
                new ProxySourceSeed
                {
                    Name = "Seed A",
                    Url = "https://example.com/a.txt",
                    Protocol = ProxyProtocol.Http
                }
            ]
        });
        var firstStore = new JsonProxyStore(firstOptions, environment, NullLogger<JsonProxyStore>.Instance);
        await firstStore.InitializeAsync();
        var seedId = await firstStore.ReadAsync(state => state.Sources.Single().Id);

        await firstStore.WriteAsync(state =>
        {
            var existing = state.Sources.Single(source => source.Id == seedId);
            existing.Name = "Locally renamed seed";
            existing.Enabled = false;
            state.Sources.Add(new ProxySource
            {
                Name = "Manual source",
                Url = "https://example.com/manual.txt",
                Protocol = ProxyProtocol.Socks5
            });
            return 0;
        });

        var secondOptions = Options.Create(new ProxyPoolOptions
        {
            DataFile = file,
            Sources =
            [
                new ProxySourceSeed
                {
                    Name = "Seed A",
                    Url = "https://example.com/a.txt",
                    Protocol = ProxyProtocol.Http
                },
                new ProxySourceSeed
                {
                    Name = "Seed B",
                    Url = "https://example.com/b.txt",
                    Protocol = ProxyProtocol.Socks4
                }
            ]
        });
        var secondStore = new JsonProxyStore(secondOptions, environment, NullLogger<JsonProxyStore>.Instance);
        await secondStore.InitializeAsync();
        var sources = await secondStore.ReadAsync(state => state.Sources.ToList());

        Assert(sources.Count == 3, "Existing pools must receive each missing seed exactly once.");
        var preserved = sources.Single(source => source.Id == seedId);
        Assert(preserved.Name == "Locally renamed seed" && !preserved.Enabled,
            "Seed synchronization must preserve local source edits.");
        var added = sources.Single(source => source.Url == "https://example.com/b.txt");
        Assert(added.IsBuiltIn && added.Protocol == ProxyProtocol.Socks4,
            "A missing configured seed must be added as a built-in source.");
        Assert(sources.Any(source => source.Url == "https://example.com/manual.txt" && !source.IsBuiltIn),
            "Manual sources must survive built-in seed synchronization.");

        var restartedStore = new JsonProxyStore(secondOptions, environment, NullLogger<JsonProxyStore>.Instance);
        await restartedStore.InitializeAsync();
        var restartedCount = await restartedStore.ReadAsync(state => state.Sources.Count);
        Assert(restartedCount == 3, "Seed synchronization must be idempotent across restarts.");
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

sealed class StaticHttpClientFactory(string content) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new StaticHttpHandler(content));
}

sealed class StaticHttpHandler(string content) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(content) });
}

sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
    public long BytesRead { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await base.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }
}

sealed class BlockingSecondLookupFactory : IHttpClientFactory
{
    public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public HttpClient CreateClient(string name) => new(new Handler(this));

    private sealed class Handler(BlockingSecondLookupFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref owner._calls) == 2)
            {
                owner.SecondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"country_code":"AU","country":"Australia"}""")
            };
        }
    }
}
