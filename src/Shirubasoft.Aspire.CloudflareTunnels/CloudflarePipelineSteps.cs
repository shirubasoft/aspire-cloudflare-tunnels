using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shirubasoft.Aspire.CloudflareTunnels;

#pragma warning disable ASPIREPIPELINES001

/// <summary>
/// Creates the deployment pipeline steps contributed by a Cloudflare tunnel resource.
/// </summary>
internal static class CloudflarePipelineSteps
{
    internal const string Tag = "cloudflare";

    public static PipelineStep CreateConfigureRoutesStep(CloudflareTunnelResource tunnel)
    {
        return new PipelineStep
        {
            Name = GetConfigureRoutesStepName(tunnel),
            Description = $"Configure Cloudflare routes for tunnel '{tunnel.Name}'.",
            DependsOnSteps = [WellKnownPipelineSteps.Publish],
            RequiredBySteps = [WellKnownPipelineSteps.Deploy],
            Tags = [Tag],
            Action = async context =>
            {
                var routes = context.Model.Resources
                    .OfType<PublishedRouteResource>()
                    .Where(route => ReferenceEquals(route.Tunnel, tunnel))
                    .ToArray();

                if (routes.Length == 0)
                {
                    context.Logger.LogInformation(
                        "Tunnel '{TunnelName}' has no published routes to configure.",
                        tunnel.Name);
                    return;
                }

                var provisioner = context.Services.GetRequiredService<CloudflareRouteProvisioner>();

                await provisioner.ConfigureRoutesForPipelineAsync(
                    tunnel,
                    routes,
                    context.ExecutionContext,
                    context.Logger,
                    context.CancellationToken);

                context.Summary.Add(
                    $"Cloudflare tunnel '{tunnel.Name}'",
                    string.Join(", ", routes.Select(route => route.Hostname)));
            }
        };
    }

    public static string GetConfigureRoutesStepName(CloudflareTunnelResource tunnel) =>
        $"configure-{tunnel.Name}-cloudflare-routes";
}

#pragma warning restore ASPIREPIPELINES001
