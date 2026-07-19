using ProxySiu.Api.Contracts;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Services;

public sealed class ProxyMaintenanceWorker(
    ProxyPoolService pool,
    MaintenanceOperationQueue queue,
    ProxyPoolProfileManager profileManager,
    ILogger<ProxyMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialOptions = profileManager.Current;
        var nextScan = initialOptions.ScanOnStartup
            ? DateTimeOffset.UtcNow.AddSeconds(3)
            : DateTimeOffset.UtcNow.AddMinutes(initialOptions.ScanIntervalMinutes);
        var nextCheck = DateTimeOffset.UtcNow.AddSeconds(8);
        var nextPrune = DateTimeOffset.UtcNow.AddMinutes(2);
        pool.SetNextCheckAt(nextCheck);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var options = profileManager.Current;
                if (now >= nextScan)
                {
                    await RunScheduledAsync(MaintenanceOperationKind.Scan, false, stoppingToken);
                    nextScan = NextRunWithJitter(options.ScanIntervalMinutes, 0.15);
                    nextCheck = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(5, 21));
                    pool.SetNextCheckAt(nextCheck);
                }

                if (now >= nextCheck)
                {
                    await RunScheduledAsync(MaintenanceOperationKind.Check, false, stoppingToken);
                    nextCheck = NextCheckRun(options);
                    pool.SetNextCheckAt(nextCheck);
                }

                if (now >= nextPrune)
                {
                    await RunScheduledAsync(MaintenanceOperationKind.Prune, false, stoppingToken);
                    nextPrune = DateTimeOffset.UtcNow.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Proxy-pool maintenance worker failed; it will retry on the next cycle.");
            }
        }
    }

    private async Task RunScheduledAsync(MaintenanceOperationKind kind, bool force, CancellationToken cancellationToken)
    {
        var submission = queue.Enqueue(kind, force);
        if (!submission.Accepted)
        {
            logger.LogInformation("Skipped scheduled {OperationKind}; operation {OperationId} is already {Status}",
                kind, submission.Operation.Id, submission.Operation.Status);
            return;
        }

        await submission.Completion!.WaitAsync(cancellationToken);
    }

    private static DateTimeOffset NextRunWithJitter(int intervalMinutes, double jitterRatio)
    {
        var intervalSeconds = intervalMinutes * 60;
        var jitterSeconds = Math.Max(1, (int)Math.Round(intervalSeconds * jitterRatio));
        return DateTimeOffset.UtcNow.AddSeconds(
            intervalSeconds + Random.Shared.Next(-jitterSeconds, jitterSeconds + 1));
    }

    private static DateTimeOffset NextCheckRun(ProxyPoolOptions options) =>
        DateTimeOffset.UtcNow.AddMinutes(Random.Shared.Next(
            options.CheckIntervalMinMinutes,
            options.CheckIntervalMaxMinutes + 1));
}
