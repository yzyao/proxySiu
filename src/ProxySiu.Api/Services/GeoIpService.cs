using System.Net;
using MaxMind.Db;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Services;

public sealed class GeoIpService : IDisposable
{
    private readonly Reader? _reader;
    private readonly ILogger<GeoIpService> _logger;

    public GeoIpService(IOptions<GeoIpOptions> options, IHostEnvironment environment, ILogger<GeoIpService> logger)
    {
        _logger = logger;
        var settings = options.Value;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.DatabasePath))
        {
            return;
        }

        var path = Path.IsPathRooted(settings.DatabasePath)
            ? settings.DatabasePath
            : Path.Combine(environment.ContentRootPath, settings.DatabasePath);
        if (!File.Exists(path))
        {
            logger.LogWarning("GeoIP database was not found at {Path}; location display is disabled.", path);
            return;
        }

        try
        {
            _reader = new Reader(path);
            logger.LogInformation("Loaded GeoIP database from {Path}.", path);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            logger.LogWarning(exception, "Could not load GeoIP database from {Path}; location display is disabled.", path);
        }
    }

    public IpGeoLocation? Lookup(string? ipAddress)
    {
        if (_reader is null || !IPAddress.TryParse(ipAddress, out var address))
        {
            return null;
        }

        try
        {
            var record = _reader.Find<GeoLiteCityRecord>(address);
            if (record is null)
            {
                return null;
            }

            var subdivision = record.Subdivisions?.FirstOrDefault();
            var location = new IpGeoLocation(
                record.Country?.IsoCode,
                PickName(record.Country?.Names),
                subdivision?.IsoCode,
                PickName(subdivision?.Names),
                PickName(record.City?.Names));
            return location.HasValue ? location : null;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException)
        {
            _logger.LogDebug(exception, "GeoIP lookup failed for {IpAddress}.", ipAddress);
            return null;
        }
    }

    public void Dispose() => _reader?.Dispose();

    private static string? PickName(Dictionary<string, string>? names)
    {
        if (names is null)
        {
            return null;
        }

        return names.GetValueOrDefault("zh-CN") ?? names.GetValueOrDefault("en") ?? names.Values.FirstOrDefault();
    }

    private sealed class GeoLiteCityRecord
    {
        [MapKey("country")]
        public GeoNameRecord? Country { get; init; }

        [MapKey("subdivisions")]
        public List<GeoNameRecord>? Subdivisions { get; init; }

        [MapKey("city")]
        public GeoNameRecord? City { get; init; }
    }

    private sealed class GeoNameRecord
    {
        [MapKey("iso_code")]
        public string? IsoCode { get; init; }

        [MapKey("names")]
        public Dictionary<string, string>? Names { get; init; }
    }
}
