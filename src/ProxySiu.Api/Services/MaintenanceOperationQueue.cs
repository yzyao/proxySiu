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
            logger.LogInformation("[任务开始] ID={OperationId} 类型={OperationKind} 开始时间={StartedAt} 强制检测={Force}",
                operation.Snapshot.Id, DescribeKind(operation.Snapshot.Kind), FormatTime(operationStartedAt), operation.Snapshot.Force);
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
                    "[任务完成] ID={OperationId} 类型={OperationKind} 开始={StartedAt} 完成={CompletedAt} 耗时={ElapsedMilliseconds}ms | 处理={Processed} 新增={Added} 更新={Updated} 清理={Removed} 失败={Failed} | {Message}",
                    completed.Id, DescribeKind(completed.Kind), FormatTime(completed.StartedAt),
                    FormatTime(completed.CompletedAt), Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
                    result.Processed, result.Added, result.Updated, result.Removed, result.Failed, result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                var cancelled = Complete(operation, MaintenanceOperationStatus.Cancelled,
                    "Maintenance operation cancelled during shutdown.", null);
                logger.LogWarning("[任务取消] ID={OperationId} 类型={OperationKind} 开始={StartedAt} 结束={CompletedAt}",
                    cancelled.Id, DescribeKind(cancelled.Kind), FormatTime(cancelled.StartedAt),
                    FormatTime(cancelled.CompletedAt));
            }
            catch (Exception exception)
            {
                var failed = Complete(operation, MaintenanceOperationStatus.Failed,
                    "Maintenance operation failed. Check server logs.", null);
                logger.LogError(exception, "[任务失败] ID={OperationId} 类型={OperationKind} 开始={StartedAt} 失败时间={CompletedAt} | {Message}",
                    failed.Id, DescribeKind(failed.Kind), FormatTime(failed.StartedAt), FormatTime(failed.CompletedAt), failed.Message);
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

    private static string DescribeKind(MaintenanceOperationKind kind) => kind switch
    {
        MaintenanceOperationKind.Scan => "采集",
        MaintenanceOperationKind.Check => "检测",
        MaintenanceOperationKind.Refresh => "采集并检测",
        MaintenanceOperationKind.Prune => "清理",
        _ => kind.ToString()
    };

    private static string FormatTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—";

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
