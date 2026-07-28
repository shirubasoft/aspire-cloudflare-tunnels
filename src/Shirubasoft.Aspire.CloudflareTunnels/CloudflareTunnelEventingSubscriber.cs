using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Shirubasoft.Aspire.CloudflareTunnels;

/// <summary>
/// Registers the runtime behavior for Cloudflare Tunnel resources.
/// </summary>
internal sealed class CloudflareTunnelEventingSubscriber :
    IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext context,
        CancellationToken cancellationToken)
    {
        var model = context.ServiceProvider.GetRequiredService<DistributedApplicationModel>();

        foreach (var installer in model.Resources.OfType<CloudflareTunnelInstallerResource>())
        {
            eventing.Subscribe<InitializeResourceEvent>(installer, async (@event, ct) =>
            {
                var provisioner = @event.Services.GetRequiredService<CloudflareTunnelProvisioner>();
                await provisioner.ProvisionAsync(installer, ct);
            });
        }

        foreach (var tunnel in model.Resources.OfType<CloudflareTunnelResource>())
        {
            var routes = model.Resources
                .OfType<PublishedRouteResource>()
                .Where(route => ReferenceEquals(route.Tunnel, tunnel))
                .ToArray();

            if (routes.Length == 0)
            {
                continue;
            }

            eventing.Subscribe<ResourceReadyEvent>(tunnel, async (@event, ct) =>
            {
                var provisioner = @event.Services.GetRequiredService<CloudflareRouteProvisioner>();
                await provisioner.ConfigureRoutesAsync(tunnel, routes, ct);
            });
        }

        return Task.CompletedTask;
    }
}
