using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stores the Cloudflare credentials used to provision a tunnel.
/// </summary>
public sealed record CloudflareTunnelCredentialsAnnotation(
    ParameterResource ApiToken,
    ParameterResource AccountId) : IResourceAnnotation;
