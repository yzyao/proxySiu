using System.Diagnostics;
using System.Threading.Channels;
using ProxySiu.Api.Contracts;

namespace ProxySiu.Api.Services;

public sealed record MaintenanceOperationSubmission(
    bool Accepted,
    MaintenanceOperationDto Operation,
    Task<MaintenanceOperationDto>? Completion);

public sealed class MaintenanceOperationQueue
{
    private readonly Channel<QueuedOperation> _channel = Channel.CreateBounded<QueuedOperation>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly object _gate = new();
    private QueuedOperation? _current;
    private MaintenanceOperationDto? _lastCompleted;

    public MaintenanceOperationSubmission Enqueue(MaintenanceOperationKind kind, bool force = false)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                return new MaintenanceOperationSubmission(false, _current.Snapshot, null);
            }

            var queuedAt = DateTimeOffset.UtcNow;
            var operation = new QueuedOperation(new MaintenanceOperationDto(
                Guid.NewGuid(), kind, MaintenanceOperationStatus.Queued, force, queuedAt,
                null, null, null, null));
            _current = operation;
            if (_channel.Writer.TryWrite(operation))
            {
                return new MaintenanceOperationSubmission(true, operation.Snapshot, operation.Completion.Task);
            }

            _current = null;
            operation.Completion.TrySetResult(operation.Snapshot with
            {
                Status = MaintenanceOperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                Message = "Maintenance queue is unavailable."
            });
            return new MaintenanceOperationSubmission(false, operation.Snapshot, null);
        }
    }

    public MaintenanceOperationDto? GetOperation(Guid id)
    {
        lock (_gate)
        {
            if (_current?.Snapshot.Id == id)
            {
                return _current.Snapshot;
            }

            return _lastCompleted?.Id == id ? _lastCompleted : null;
        }
    }

    public (MaintenanceOperationDto? Active, MaintenanceOperationDto? Last) GetState()
    {
        lock (_gate)
        {
            return (_current?.Snapshot, _lastCompleted);
        }
    }

    internal async Task ProcessAsync(ProxyPoolService pool, ILogger logger, CancellationToken stoppingToken)
    {
        await foreach (var operation in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            Start(operation);
            var operationStartedAt = operation.Snapshot.StartedAt;
            logger.LogInformation("Maintenance operation {OperationId} ({OperationKind}) started at {StartedAt}; force={Force}",
                operation.Snapshot.Id, operation.Snapshot.Kind, operationStartedAt, operation.Snapshot.Force);
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                var result = operation.Snapshot.Kind switch
                {
                    MaintenanceOperationKind.Scan => await pool.ScanAsync(stoppingToken),
                    MaintenanceOperationKind.Check => await pool.CheckDueAsync(operation.Snapshot.Force, stoppingToken),
                    MaintenanceOperationKind.Refresh => await RefreshAsync(pool, stoppingToken),
                    MaintenanceOperationKind.Prune => await pool.PruneAsync(stoppingToken),
                    _ => throw new ArgumentOutOfRangeException()
                };
                var completed = Complete(operation, MaintenanceOperationStatus.Completed, result.Message, result);
                logger.LogInformation(
                    "Maintenance operation {OperationId} ({OperationKind}) completed; started={StartedAt}, completed={CompletedAt}, elapsedMs={ElapsedMilliseconds}, processed={Processed}, added={Added}, updated={Updated}, removed={Removed}, failed={Failed}",
                    completed.Id, completed.Kind, completed.StartedAt, completed.CompletedAt,
                    Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds), result.Processed, result.Added,
                    result.Updated, result.Removed, result.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                var cancelled = Complete(operation, MaintenanceOperationStatus.Cancelled,
                    "Maintenance operation cancelled during shutdown.", null);
                logger.LogWarning("Maintenance operation {OperationId} ({OperationKind}) cancelled; started={StartedAt}, completed={CompletedAt}",
                    cancelled.Id, cancelled.Kind, cancelled.StartedAt, cancelled.CompletedAt);
            }
            catch (Exception exception)
            {
                var failed = Complete(operation, MaintenanceOperationStatus.Failed,
                    "Maintenance operation failed. Check server logs.", null);
                logger.LogError(exception, "Maintenance operation {OperationId} ({OperationKind}) failed; started={StartedAt}, completed={CompletedAt}",
                    failed.Id, failed.Kind, failed.StartedAt, failed.CompletedAt);
            }
        }
    }

    private static async Task<PoolOperationResult> RefreshAsync(ProxyPoolService pool, CancellationToken cancellationToken)
    {
        var scan = await pool.ScanAsync(cancellationToken);
        if (scan.Busy)
        {
            return scan;
        }

        var check = await pool.CheckDueAsync(false, cancellationToken);
        return new PoolOperationResult(
            check.Busy,
            $"{scan.Message} {check.Message}",
            scan.Processed + check.Processed,
            scan.Added,
            scan.Updated,
            check.Removed,
            scan.Failed + check.Failed);
    }

    private void Start(QueuedOperation operation)
    {
        lock (_gate)
        {
            operation.Snapshot = operation.Snapshot with
            {
                Status = MaintenanceOperationStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                Message = "Maintenance operation is running."
            };
        }
    }

    private MaintenanceOperationDto Complete(QueuedOperation operation, MaintenanceOperationStatus status, string message,
        PoolOperationResult? result)
    {
        MaintenanceOperationDto completed;
        lock (_gate)
        {
            completed = operation.Snapshot with
            {
                Status = status,
                CompletedAt = DateTimeOffset.UtcNow,
                Message = message,
                Result = result
            };
            operation.Snapshot = completed;
            _lastCompleted = completed;
            if (ReferenceEquals(_current, operation))
            {
                _current = null;
            }
        }

        operation.Completion.TrySetResult(completed);
        return completed;
    }

    private sealed class QueuedOperation(MaintenanceOperationDto snapshot)
    {
        public MaintenanceOperationDto Snapshot { get; set; } = snapshot;
        public TaskCompletionSource<MaintenanceOperationDto> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class MaintenanceOperationWorker(
    MaintenanceOperationQueue queue,
    ProxyPoolService pool,
    ILogger<MaintenanceOperationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        queue.ProcessAsync(pool, logger, stoppingToken);
}
