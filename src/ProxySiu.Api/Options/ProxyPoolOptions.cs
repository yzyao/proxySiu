using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using ProxySiu.Api.Models;

namespace ProxySiu.Api.Options;

public sealed class ProxyPoolOptions
{
    public const string SectionName = "ProxyPool";

    public string Profile { get; set; } = "high-throughput";
    public Dictionary<string, ProxyPoolProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DataFile { get; set; } = "data/proxy-pool.json";
    public string CheckUrl { get; set; } = "https://api.ipify.org?format=json";
    public int RequestTimeoutSeconds { get; set; } = 8;
    public int DownloadTimeoutSeconds { get; set; } = 20;
    public int CheckConcurrency { get; set; } = 36;
    public int SourceConcurrency { get; set; } = 2;
    public int ScanIntervalMinutes { get; set; } = 120;
    public int CheckIntervalMinMinutes { get; set; } = 5;
    public int CheckIntervalMaxMinutes { get; set; } = 15;
    public int RecheckAliveMinutes { get; set; } = 30;
    public int RecheckDeadMinutes { get; set; } = 60;
    public int SecondDeadRetryMinutes { get; set; } = 360;
    public int DeadQuarantineHours { get; set; } = 24;
    public int ReaddQuarantineHours { get; set; } = 24;
    public int AliveChecksPerCycle { get; set; } = 120;
    public int PendingChecksPerCycle { get; set; } = 200;
    public int DeadChecksPerCycle { get; set; } = 80;
    public int MaxChecksPerCycle { get; set; } = 400;
    public int MaxCandidatesPerSource { get; set; } = 800;
    public int MaxSourceBytes { get; set; } = 2_000_000;
    public int MaxConsecutiveFailures { get; set; } = 3;
    public int RemoveDeadAfterHours { get; set; } = 24;
    public bool ScanOnStartup { get; set; } = true;
    public bool AllowRemoteAccess { get; set; }
    public bool AllowInternalNetworkAccess { get; set; }
    public bool AllowPrivateNetworks { get; set; }
    public List<ProxySourceSeed> Sources { get; set; } = [];
}

public sealed class ProxyPoolProfile
{
    public int? CheckConcurrency { get; set; }
    public int? SourceConcurrency { get; set; }
    public int? ScanIntervalMinutes { get; set; }
    public int? CheckIntervalMinMinutes { get; set; }
    public int? CheckIntervalMaxMinutes { get; set; }
    public int? RecheckAliveMinutes { get; set; }
    public int? RecheckDeadMinutes { get; set; }
    public int? SecondDeadRetryMinutes { get; set; }
    public int? AliveChecksPerCycle { get; set; }
    public int? PendingChecksPerCycle { get; set; }
    public int? DeadChecksPerCycle { get; set; }
    public int? MaxChecksPerCycle { get; set; }

    public void ApplyTo(ProxyPoolOptions options)
    {
        options.CheckConcurrency = CheckConcurrency ?? options.CheckConcurrency;
        options.SourceConcurrency = SourceConcurrency ?? options.SourceConcurrency;
        options.ScanIntervalMinutes = ScanIntervalMinutes ?? options.ScanIntervalMinutes;
        options.CheckIntervalMinMinutes = CheckIntervalMinMinutes ?? options.CheckIntervalMinMinutes;
        options.CheckIntervalMaxMinutes = CheckIntervalMaxMinutes ?? options.CheckIntervalMaxMinutes;
        options.RecheckAliveMinutes = RecheckAliveMinutes ?? options.RecheckAliveMinutes;
        options.RecheckDeadMinutes = RecheckDeadMinutes ?? options.RecheckDeadMinutes;
        options.SecondDeadRetryMinutes = SecondDeadRetryMinutes ?? options.SecondDeadRetryMinutes;
        options.AliveChecksPerCycle = AliveChecksPerCycle ?? options.AliveChecksPerCycle;
        options.PendingChecksPerCycle = PendingChecksPerCycle ?? options.PendingChecksPerCycle;
        options.DeadChecksPerCycle = DeadChecksPerCycle ?? options.DeadChecksPerCycle;
        options.MaxChecksPerCycle = MaxChecksPerCycle ?? options.MaxChecksPerCycle;
    }
}

public static class ProxyPoolProfileSelector
{
    public static void Apply(ProxyPoolOptions options, string profileName)
    {
        var profile = options.Profiles.FirstOrDefault(entry =>
            entry.Key.Equals(profileName, StringComparison.OrdinalIgnoreCase)).Value;
        if (profile is null)
        {
            throw new InvalidOperationException($"ProxyPool profile '{profileName}' does not exist.");
        }

        profile.ApplyTo(options);
        options.Profile = profileName;
    }
}

public sealed record ProxyPoolProfileSummary(
    string Name,
    int CheckConcurrency,
    int MaxChecksPerCycle,
    int CheckIntervalMinMinutes,
    int CheckIntervalMaxMinutes,
    int AliveChecksPerCycle,
    int PendingChecksPerCycle,
    int DeadChecksPerCycle);

public sealed class ProxyPoolProfileManager
{
    private readonly IConfiguration? _configuration;
    private readonly ProxyPoolOptionsValidator _validator;
    private readonly object _gate = new();
    private ProxyPoolOptions _current;

    public ProxyPoolProfileManager(IConfiguration configuration, ProxyPoolOptionsValidator validator,
        string initialProfile)
    {
        _configuration = configuration;
        _validator = validator;
        _current = BuildProfile(initialProfile);
    }

    public ProxyPoolProfileManager(ProxyPoolOptions initialOptions, ProxyPoolOptionsValidator validator)
    {
        _validator = validator;
        _current = initialOptions;
    }

    public ProxyPoolOptions Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public ProxyPoolProfileSummary GetSummary()
    {
        var current = Current;
        return new ProxyPoolProfileSummary(current.Profile, current.CheckConcurrency,
            current.MaxChecksPerCycle, current.CheckIntervalMinMinutes, current.CheckIntervalMaxMinutes,
            current.AliveChecksPerCycle, current.PendingChecksPerCycle, current.DeadChecksPerCycle);
    }

    public bool TrySwitch(string profileName, out ProxyPoolProfileSummary? profile, out string? error)
    {
        try
        {
            var candidate = BuildProfile(profileName);
            lock (_gate)
            {
                _current = candidate;
            }

            profile = GetSummary();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or OptionsValidationException)
        {
            profile = null;
            error = exception.Message;
            return false;
        }
    }

    private ProxyPoolOptions BuildProfile(string profileName)
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("Profile switching is not available for this options instance.");
        }

        var options = _configuration.GetSection(ProxyPoolOptions.SectionName).Get<ProxyPoolOptions>() ?? new();
        ProxyPoolProfileSelector.Apply(options, profileName);
        var validation = _validator.Validate(null, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(ProxyPoolOptions.SectionName, typeof(ProxyPoolOptions),
                validation.Failures);
        }

        return options;
    }
}

public sealed class ProxyPoolOptionsValidator : IValidateOptions<ProxyPoolOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyPoolOptions options)
    {
        var failures = new List<string>();

        Require(!string.IsNullOrWhiteSpace(options.DataFile), "DataFile must be configured.");
        Require(IsHttpUrl(options.CheckUrl), "CheckUrl must be an absolute HTTP(S) URL.");
        Require(InRange(options.RequestTimeoutSeconds, 2, 60), "RequestTimeoutSeconds must be between 2 and 60.");
        Require(InRange(options.DownloadTimeoutSeconds, 3, 120), "DownloadTimeoutSeconds must be between 3 and 120.");
        Require(InRange(options.CheckConcurrency, 1, 100), "CheckConcurrency must be between 1 and 100.");
        Require(InRange(options.SourceConcurrency, 1, 16), "SourceConcurrency must be between 1 and 16.");
        Require(InRange(options.ScanIntervalMinutes, 1, 10_080), "ScanIntervalMinutes must be between 1 and 10080.");
        Require(InRange(options.CheckIntervalMinMinutes, 1, 10_080), "CheckIntervalMinMinutes must be between 1 and 10080.");
        Require(InRange(options.CheckIntervalMaxMinutes, options.CheckIntervalMinMinutes, 10_080),
            "CheckIntervalMaxMinutes must be greater than or equal to CheckIntervalMinMinutes.");
        Require(InRange(options.RecheckAliveMinutes, 1, 43_200), "RecheckAliveMinutes must be between 1 and 43200.");
        Require(InRange(options.RecheckDeadMinutes, 1, 43_200), "RecheckDeadMinutes must be between 1 and 43200.");
        Require(InRange(options.SecondDeadRetryMinutes, 1, 43_200), "SecondDeadRetryMinutes must be between 1 and 43200.");
        Require(InRange(options.DeadQuarantineHours, 1, 8_760), "DeadQuarantineHours must be between 1 and 8760.");
        Require(InRange(options.ReaddQuarantineHours, 1, 8_760), "ReaddQuarantineHours must be between 1 and 8760.");
        Require(InRange(options.AliveChecksPerCycle, 0, 20_000), "AliveChecksPerCycle must be between 0 and 20000.");
        Require(InRange(options.PendingChecksPerCycle, 0, 20_000), "PendingChecksPerCycle must be between 0 and 20000.");
        Require(InRange(options.DeadChecksPerCycle, 0, 20_000), "DeadChecksPerCycle must be between 0 and 20000.");
        Require(InRange(options.MaxChecksPerCycle, 1, 20_000), "MaxChecksPerCycle must be between 1 and 20000.");
        Require(options.AliveChecksPerCycle + options.PendingChecksPerCycle + options.DeadChecksPerCycle <=
                options.MaxChecksPerCycle,
            "The per-cycle queue quotas cannot exceed MaxChecksPerCycle.");
        Require(InRange(options.MaxCandidatesPerSource, 1, 50_000), "MaxCandidatesPerSource must be between 1 and 50000.");
        Require(InRange(options.MaxSourceBytes, 1_024, 20_000_000), "MaxSourceBytes must be between 1024 and 20000000.");
        Require(InRange(options.MaxConsecutiveFailures, 1, 100), "MaxConsecutiveFailures must be between 1 and 100.");
        Require(InRange(options.RemoveDeadAfterHours, 1, 8_760), "RemoveDeadAfterHours must be between 1 and 8760.");
        Require(!options.AllowRemoteAccess,
            "AllowRemoteAccess is not supported in the local-stability profile. Keep the service behind loopback access.");

        foreach (var source in options.Sources)
        {
            Require(!string.IsNullOrWhiteSpace(source.Name) && source.Name.Trim().Length <= 128,
                "Every built-in source name must contain 1 to 128 characters.");
            Require(source.Url.Length <= 2_048 && IsHttpUrl(source.Url),
                "Every built-in source URL must be an absolute HTTP(S) URL of at most 2048 characters.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }
    }

    private static bool InRange(int value, int minimum, int maximum) => value >= minimum && value <= maximum;

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

public sealed class ProxySourceSeed
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    public ProxyProtocol Protocol { get; set; }
    public bool Enabled { get; set; } = true;
}
