using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Contracts;

public sealed class ProxyQuery
{
    public string? Q { get; set; }
    public string? Status { get; set; }
    public string? Protocol { get; set; }
    public string? Sort { get; set; }
    public bool? Desc { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

public sealed class ProxyCreateRequest
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Http;
    public bool IsPinned { get; set; } = true;
}

public sealed class SourceWriteRequest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Http;
    public bool Enabled { get; set; } = true;
}

public sealed class ProfileUpdateRequest
{
    public string Profile { get; set; } = string.Empty;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record ProxyDto(
    Guid Id,
    string Host,
    int Port,
    ProxyProtocol Protocol,
    ProxyStatus Status,
    bool IsPinned,
    IReadOnlyList<string> Sources,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? NextCheckAt,
    DateTimeOffset? LastAliveAt,
    long? LatencyMs,
    string? ExitIp,
    int SuccessCount,
    int FailureCount,
    int ConsecutiveFailures,
    string? LastError,
    string Url);

public sealed record SourceDto(
    Guid Id,
    string Name,
    string Url,
    ProxyProtocol Protocol,
    bool Enabled,
    bool IsBuiltIn,
    DateTimeOffset? LastScanAt,
    int LastFound,
    string? LastError);

public sealed record ProtocolSummaryDto(ProxyProtocol Protocol, int Total, int Alive, int Dead, int Pending);

public sealed record CheckQueueStateDto(
    bool IsRunning,
    int Waiting,
    int Total,
    int Completed,
    int InFlight,
    int Alive,
    int Failed,
    int Concurrency,
    double ProgressPercent,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public enum MaintenanceOperationKind
{
    Scan,
    Check,
    Refresh,
    Prune
}

public enum MaintenanceOperationStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record MaintenanceOperationDto(
    Guid Id,
    MaintenanceOperationKind Kind,
    MaintenanceOperationStatus Status,
    bool Force,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Message,
    PoolOperationResult? Result);

public sealed record OperationStateDto(
    bool IsScanning,
    bool IsChecking,
    bool IsPruning,
    DateTimeOffset? LastScanAt,
    DateTimeOffset? LastCheckAt,
    DateTimeOffset? NextCheckAt,
    DateTimeOffset? LastPruneAt,
    string? LastMessage,
    CheckQueueStateDto CheckQueue,
    MaintenanceOperationDto? ActiveOperation = null,
    MaintenanceOperationDto? LastOperation = null);

public sealed record DashboardDto(
    int Total,
    int Alive,
    int Dead,
    int Pending,
    double AvailabilityRate,
    long? AverageLatencyMs,
    int Sources,
    int EnabledSources,
    IReadOnlyList<ProtocolSummaryDto> Protocols,
    OperationStateDto Operations,
    DateTimeOffset UpdatedAt,
    ProxyPoolProfileSummary? Profile = null);

public sealed record PoolOperationResult(
    bool Busy,
    string Message,
    int Processed = 0,
    int Added = 0,
    int Updated = 0,
    int Removed = 0,
    int Failed = 0);
