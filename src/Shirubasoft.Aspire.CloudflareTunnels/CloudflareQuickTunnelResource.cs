using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an account-free Cloudflare Quick Tunnel backed by a cloudflared container.
/// </summary>
public sealed class CloudflareQuickTunnelResource([ResourceName] string name) : ContainerResource(name)
{
    private EndpointReference? _metricsEndpoint;

    /// <summary>
    /// Gets the cloudflared metrics endpoint.
    /// </summary>
    public EndpointReference MetricsEndpoint =>
        _metricsEndpoint ??= new(this, CloudflareTunnelContainerDefaults.MetricsEndpointName);

    /// <summary>
    /// Gets the public URL assigned by Cloudflare after the tunnel starts.
    /// </summary>
    public string? PublicUrl { get; internal set; }

    internal EndpointReference? TargetEndpoint { get; set; }
}
