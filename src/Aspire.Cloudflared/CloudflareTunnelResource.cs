using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a Cloudflare Tunnel backed by a cloudflared container.
/// </summary>
public sealed class CloudflareTunnelResource([ResourceName] string name) : ContainerResource(name)
{
    internal const string MetricsEndpointName = "metrics";

    internal const int DefaultMetricsPort = 60123;

    private EndpointReference? _metricsEndpoint;

    /// <summary>
    /// Gets the cloudflared metrics endpoint.
    /// </summary>
    public EndpointReference MetricsEndpoint =>
        _metricsEndpoint ??= new(this, MetricsEndpointName);

    /// <summary>
    /// Gets or sets the Cloudflare tunnel ID (UUID) after creation/discovery.
    /// </summary>
    public string? TunnelId { get; internal set; }

    /// <summary>
    /// Gets or sets the tunnel token used to authenticate the cloudflared connection.
    /// This is populated at runtime in run mode.
    /// </summary>
    public string? TunnelToken { get; internal set; }
}
