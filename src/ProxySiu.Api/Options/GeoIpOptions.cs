namespace ProxySiu.Api.Options;

public sealed class GeoIpOptions
{
    public const string SectionName = "GeoIp";

    public bool Enabled { get; set; } = true;
    public string DatabasePath { get; set; } = "data/GeoLite2-City.mmdb";
    public bool UseIpSb { get; set; } = true;
    public int IpSbLookupIntervalSeconds { get; set; } = 2;
    public string IpSbBaseUrl { get; set; } = "https://api.ip.sb";
}
