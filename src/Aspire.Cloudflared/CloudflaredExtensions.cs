using Aspire.Cloudflared;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding and configuring Cloudflare Tunnel resources.
/// </summary>
public static class CloudflaredResourceBuilderExtensions
{
    /// <summary>
    /// Adds a Cloudflare tunnel resource to the application with automatic tunnel creation.
    /// The tunnel will be created via the Cloudflare API if it doesn't exist.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the tunnel resource. This will also be used as the tunnel name in Cloudflare.</param>
    /// <param name="metricsPort">Optional port for the metrics endpoint.</param>
    /// <returns>A resource builder for the tunnel.</returns>
    /// <remarks>
    /// This method requires the following parameters to be configured:
    /// - <c>{name}-api-token</c>: A Cloudflare API token with tunnel permissions
    /// - <c>{name}-account-id</c>: Your Cloudflare account ID
    /// 
    /// In run mode, an installer resource creates or finds the tunnel before starting.
    /// In publish mode, a pre-provisioned tunnel token is exposed as a deployment parameter.
    /// </remarks>
    public static IResourceBuilder<CloudflareTunnelResource> AddCloudflareTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? metricsPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var tunnelResource = new CloudflareTunnelResource(name);

        var tunnelBuilder = builder.AddResource(tunnelResource)
            .WithImage(CloudflaredContainerImageTags.Image, CloudflaredContainerImageTags.Tag)
            .WithImageRegistry(CloudflaredContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: metricsPort,
                targetPort: CloudflareTunnelResource.DefaultMetricsPort,
                name: CloudflareTunnelResource.MetricsEndpointName)
            .WithHttpHealthCheck(
                "/ready",
                endpointName: CloudflareTunnelResource.MetricsEndpointName)
            .WithArgs(
            [
                "tunnel",
                "--no-autoupdate",
                "--metrics",
                $"0.0.0.0:{CloudflareTunnelResource.DefaultMetricsPort}",
                "run"
            ]);

        if (builder.ExecutionContext.IsRunMode)
        {
#pragma warning disable ASPIREINTERACTION001
            var accountIdParameter = builder
                .AddParameter($"{name}-account-id", secret: false)
                .WithDescription("The Cloudflare account ID.")
                .WithCustomInput(p => new()
                {
                    InputType = InputType.Text,
                    Value = null,
                    Name = p.Name,
                    Placeholder = "Enter your Cloudflare account ID",
                    Description = p.Description,
                    Required = true
                });

            var apiTokenParameter = builder
                .AddParameter($"{name}-api-token", secret: true)
                .WithDescription("The Cloudflare API token with tunnel permissions.")
                .WithCustomInput(p => new()
                {
                    InputType = InputType.Text,
                    Value = null,
                    Name = p.Name,
                    Placeholder = "Enter your Cloudflare API token",
                    Description = p.Description,
                    Required = true
                });
#pragma warning restore ASPIREINTERACTION001

            tunnelBuilder.WithAnnotation(new CloudflareTunnelCredentialsAnnotation(
                apiTokenParameter.Resource,
                accountIdParameter.Resource));

            var installerBuilder = AddTunnelInstaller(builder, tunnelBuilder);

            tunnelBuilder.WithEnvironment(context =>
            {
                if (!string.IsNullOrEmpty(tunnelResource.TunnelToken))
                {
                    context.EnvironmentVariables["TUNNEL_TOKEN"] = tunnelResource.TunnelToken;
                }
                else 
                {
                    throw new InvalidOperationException("Cloudflare tunnel token not available yet.");
                }
            });

            tunnelBuilder.WaitForCompletion(installerBuilder);
        }
        else
        {
            var tunnelTokenParameter = builder
                .AddParameter($"{name}-tunnel-token", secret: true)
                .WithDescription("The Cloudflare tunnel token. Get this from the Cloudflare dashboard or by running 'cloudflared tunnel token <tunnel-name>'.");

            tunnelBuilder.WithEnvironment("TUNNEL_TOKEN", tunnelTokenParameter.Resource);
        }

        return tunnelBuilder;
    }

    /// <summary>
    /// Exposes a resource's endpoint through a Cloudflare tunnel with the specified hostname.
    /// Creates DNS record and configures tunnel ingress routing.
    /// </summary>
    /// <typeparam name="T">The type of resource with endpoints.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tunnel">The Cloudflare tunnel to route through.</param>
    /// <param name="hostname">The public hostname for this route (e.g., "api.example.com").</param>
    /// <param name="endpointName">The name of the endpoint to expose. Defaults to "http".</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<T> WithCloudflareTunnel<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel,
        string hostname,
        string endpointName = "http")
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tunnel);
        ArgumentException.ThrowIfNullOrEmpty(hostname);

        var endpoint = builder.GetEndpoint(
            endpointName,
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork);

        // Register the reference so Aspire configures container-to-host tunneling when
        // the target is a host process and container DNS when it is another container.
        tunnel.WithReference(endpoint);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            AddPublishedRoute(builder.ApplicationBuilder, tunnel, builder.Resource, hostname, endpoint);
        }

        return builder;
    }

    private static IResourceBuilder<CloudflareTunnelInstallerResource> AddTunnelInstaller(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel)
    {
        var installerName = $"{tunnel.Resource.Name}-installer";
        var installer = new CloudflareTunnelInstallerResource(installerName, tunnel.Resource);

        builder.Services.TryAddSingleton<CloudflareTunnelProvisioner>();
        builder.Services.TryAddSingleton<CloudflareRouteProvisioner>();
        builder.Services.TryAddEventingSubscriber<CloudflareTunnelEventingSubscriber>();

        var installerBuilder = builder.AddResource(installer)
            .WithParentRelationship(tunnel.Resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Tunnel Installer",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, "Cloudflare API")
                ]
            });

        return installerBuilder;
    }

    private static void AddPublishedRoute(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel,
        IResource targetResource,
        string hostname,
        EndpointReference endpoint)
    {
        var safeName = hostname.Replace(".", "-").Replace(":", "-");
        var routeName = $"{tunnel.Resource.Name}-route-{safeName}";

        var route = new PublishedRouteResource(
            routeName,
            hostname,
            endpoint,
            targetResource,
            tunnel.Resource);

        builder.AddResource(route)
            .WithParentRelationship(tunnel.Resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Cloudflare Route",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, "Cloudflare API"),
                    new("Hostname", hostname)
                ]
            });
    }
}
