using System.Text.Json.Serialization;

namespace ProxySiu.Api.Models;

public enum ProxyProtocol
{
    Http,
    Socks4,
    Socks5
}

public enum ProxyStatus
{
    Pending,
    Alive,
    Dead
}

public sealed class ProxyPoolState
{
    public int Version { get; set; } = 2;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool InitialSweepCompleted { get; set; }
    public List<ProxyRecord> Proxies { get; set; } = [];
    public List<ProxySource> Sources { get; set; } = [];
    public List<ProxyQuarantine> Quarantines { get; set; } = [];
}

public sealed class ProxyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Host { get; set; }
    public int Port { get; set; }
    public ProxyProtocol Protocol { get; set; }
    public ProxyStatus Status { get; set; } = ProxyStatus.Pending;
    public bool IsPinned { get; set; }
    public List<Guid> SourceIds { get; set; } = [];
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastAliveAt { get; set; }
    public long? LatencyMs { get; set; }
    public string? ExitIp { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? QuarantinedUntil { get; set; }
    public string? LastError { get; set; }

    [JsonIgnore]
    public string Key => $"{Protocol}:{Host.ToLowerInvariant()}:{Port}";
}

public sealed class ProxyQuarantine
{
    public required string Key { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ProxySource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Url { get; set; }
    public ProxyProtocol Protocol { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }
    public int LastFound { get; set; }
    public string? LastError { get; set; }
}

public readonly record struct ProxyCandidate(string Host, int Port, ProxyProtocol Protocol);

public sealed record ProxyCheckResult(
    Guid ProxyId,
    bool IsAlive,
    long? LatencyMs,
    string? ExitIp,
    string? Error,
    DateTimeOffset CheckedAt);
