using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Resource that handles creating/finding the Cloudflare tunnel via API.
/// This is an installer resource that runs before the tunnel container starts.
/// </summary>
/// <remarks>
/// In run mode, this resource uses the Cloudflare API to:
/// 1. Check if a tunnel with the given name exists
/// 2. Create the tunnel if it doesn't exist
/// 3. Retrieve the tunnel token for the cloudflared container
/// 
/// This resource uses <see cref="Aspire.Hosting.ResourceBuilderExtensions.WithParentRelationship{T}"/>
/// to appear as a child of the tunnel in the dashboard.
/// </remarks>
public sealed class CloudflareTunnelInstallerResource(
    [ResourceName] string name,
    CloudflareTunnelResource tunnel) : Resource(name), IResourceWithWaitSupport
{
    /// <summary>
    /// Gets the tunnel resource that this installer is configuring.
    /// </summary>
    public CloudflareTunnelResource Tunnel { get; } = tunnel;

    /// <summary>
    /// Gets or sets whether the tunnel was created (vs found existing).
    /// </summary>
    public bool WasCreated { get; internal set; }
}
