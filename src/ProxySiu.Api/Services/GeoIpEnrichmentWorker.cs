using ProxySiu.Api.Storage;
using ProxySiu.Api.Models;

namespace ProxySiu.Api.Services;

public sealed class GeoIpEnrichmentWorker(GeoIpService geoIp, JsonProxyStore store,
    ILogger<GeoIpEnrichmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var ipAddress in geoIp.ReadRemoteQueueAsync(stoppingToken))
        {
            // Coalesce a backlog of lookups into one durable save instead of rewriting the pool per IP.
            var locations = new Dictionary<string, IpGeoLocation>(StringComparer.OrdinalIgnoreCase);
            var currentIp = ipAddress;
            var lookupInProgress = true;
            var startedAt = System.Diagnostics.Stopwatch.StartNew();
            var saved = false;
            try
            {
                do
                {
                    var location = await geoIp.LookupIpSbAsync(currentIp, stoppingToken);
                    if (location is not null)
                    {
                        locations[currentIp] = location;
                    }
                    else
                    {
                        geoIp.CompleteRemoteLookup(currentIp, keepCached: false);
                        logger.LogDebug("No location result was returned for {IpAddress}.", currentIp);
                    }
                    lookupInProgress = false;

                    await Task.Delay(TimeSpan.FromSeconds(geoIp.IpSbLookupIntervalSeconds), stoppingToken);
                    if (locations.Count >= 20 || startedAt.Elapsed >= TimeSpan.FromSeconds(30) ||
                        !geoIp.TryReadRemoteLookup(out var nextIp))
                    {
                        break;
                    }

                    currentIp = nextIp;
                    lookupInProgress = true;
                } while (true);

                if (locations.Count > 0)
                {
                    await SaveLocationsAsync(locations, stoppingToken);
                }

                saved = true;
            }
            finally
            {
                try
                {
                    // Flush a partial batch during graceful shutdown; a forced kill can still lose this metadata.
                    if (!saved && stoppingToken.IsCancellationRequested && locations.Count > 0)
                    {
                        await SaveLocationsAsync(locations, CancellationToken.None);
                        saved = true;
                    }
                }
                finally
                {
                    foreach (var ip in locations.Keys)
                    {
                        geoIp.CompleteRemoteLookup(ip, keepCached: saved);
                    }

                    if (lookupInProgress)
                    {
                        geoIp.CompleteRemoteLookup(currentIp, keepCached: false);
                    }
                }
            }
        }
    }

    private Task<int> SaveLocationsAsync(Dictionary<string, IpGeoLocation> locations, CancellationToken cancellationToken) =>
        store.WriteIfChangedAsync(state =>
        {
            var changed = false;
            foreach (var proxy in state.Proxies)
            {
                if (proxy.ExitIp is not null && locations.TryGetValue(proxy.ExitIp, out var location) &&
                    proxy.GeoLocation != location)
                {
                    proxy.GeoLocation = location;
                    changed = true;
                }
            }

            return (0, changed);
        }, cancellationToken);
}
