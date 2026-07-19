using ProxySiu.Api.Storage;

namespace ProxySiu.Api.Services;

public sealed class GeoIpEnrichmentWorker(GeoIpService geoIp, JsonProxyStore store,
    ILogger<GeoIpEnrichmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var ipAddress in geoIp.ReadRemoteQueueAsync(stoppingToken))
        {
            var location = await geoIp.LookupIpSbAsync(ipAddress, stoppingToken);
            if (location is not null)
            {
                await store.WriteAsync(state =>
                {
                    foreach (var proxy in state.Proxies.Where(proxy =>
                                 string.Equals(proxy.ExitIp, ipAddress, StringComparison.OrdinalIgnoreCase)))
                    {
                        proxy.GeoLocation = location;
                    }

                    return 0;
                }, stoppingToken);
            }
            else
            {
                logger.LogDebug("No location result was returned for {IpAddress}.", ipAddress);
            }

            geoIp.CompleteRemoteLookup(ipAddress, keepCached: location is not null);
            await Task.Delay(TimeSpan.FromSeconds(geoIp.IpSbLookupIntervalSeconds), stoppingToken);
        }
    }
}
