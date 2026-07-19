using Microsoft.Extensions.Options;
using ProxySiu.Api.Models;

namespace ProxySiu.Api.Options;

public sealed class ProxyPoolOptions
{
    public const string SectionName = "ProxyPool";

    public string DataFile { get; set; } = "data/proxy-pool.json";
    public string CheckUrl { get; set; } = "https://api.ipify.org?format=json";
    public int RequestTimeoutSeconds { get; set; } = 8;
    public int DownloadTimeoutSeconds { get; set; } = 20;
    public int CheckConcurrency { get; set; } = 36;
    public int SourceConcurrency { get; set; } = 2;
    public int ScanIntervalMinutes { get; set; } = 120;
    public int CheckIntervalMinutes { get; set; } = 10;
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
    public bool AllowPrivateNetworks { get; set; }
    public List<ProxySourceSeed> Sources { get; set; } = [];
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
        Require(InRange(options.CheckIntervalMinutes, 1, 10_080), "CheckIntervalMinutes must be between 1 and 10080.");
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
