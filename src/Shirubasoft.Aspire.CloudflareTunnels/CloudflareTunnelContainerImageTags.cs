namespace Aspire.Hosting;

internal static class CloudflareTunnelContainerImageTags
{
    public const string Tag = "2026.8.3";
    public const string Registry = "docker.io";
    public const string Image = "cloudflare/cloudflared";
}

internal static class CloudflareTunnelContainerDefaults
{
    public const string MetricsEndpointName = "metrics";
    public const int MetricsPort = 60123;
}
