using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a published route through a Cloudflare tunnel.
/// This resource tracks a specific hostname-to-endpoint mapping and is associated
/// with a <see cref="CloudflareTunnelResource"/> via a parent relationship.
/// </summary>
/// <remarks>
/// This resource uses <see cref="Aspire.Hosting.ResourceBuilderExtensions.WithParentRelationship{T}"/>
/// to establish the parent-child relationship with the tunnel resource, rather than implementing
/// <see cref="IResourceWithParent{T}"/>.
/// </remarks>
public sealed class PublishedRouteResource(
    [ResourceName] string name,
    string hostname,
    EndpointReference targetEndpoint,
    IResource targetResource,
    CloudflareTunnelResource tunnel)
    : Resource(name), IResourceWithWaitSupport
{
    /// <summary>
    /// Gets the public hostname for this route (e.g., "api.example.com").
    /// </summary>
    public string Hostname { get; } = hostname;

    /// <summary>
    /// Gets the endpoint reference to the target service that will receive traffic.
    /// </summary>
    public EndpointReference TargetEndpoint { get; } = targetEndpoint;

    /// <summary>
    /// Gets the target resource that receives traffic.
    /// </summary>
    public IResource TargetResource { get; } = targetResource;

    /// <summary>
    /// Gets the Cloudflare tunnel resource that this route is associated with.
    /// </summary>
    public CloudflareTunnelResource Tunnel { get; } = tunnel;

    /// <summary>
    /// Gets or sets whether the DNS record was created or updated successfully.
    /// </summary>
    public bool DnsRecordCreated { get; internal set; }
}
