using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Cloudflared;

/// <summary>
/// Handles the provisioning of Cloudflare tunnels via the Cloudflare API.
/// This class is responsible for creating/finding tunnels and retrieving their tokens.
/// </summary>
internal sealed class CloudflareTunnelProvisioner(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService)
{
    /// <summary>
    /// Provisions a tunnel by creating it if it doesn't exist, or finding the existing one.
    /// Updates the tunnel resource with the tunnel ID and token.
    /// </summary>
    public async Task ProvisionAsync(CloudflareTunnelInstallerResource installer, CancellationToken cancellationToken)
    {
        var tunnel = installer.Tunnel;
        var logger = loggerService.GetLogger(installer);

        try
        {
            await notificationService.PublishUpdateAsync(installer, state => state with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info),
                StartTimeStamp = DateTime.UtcNow
            });

            await DoProvisionAsync(installer, tunnel, logger, cancellationToken);

            await notificationService.PublishUpdateAsync(installer, state => state with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Finished, KnownResourceStateStyles.Success),
                StopTimeStamp = DateTime.UtcNow,
                Properties = [
                    ..state.Properties,
                    new("TunnelId", tunnel.TunnelId ?? "Unknown")
                ]
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision tunnel '{TunnelName}'", tunnel.Name);

            await notificationService.PublishUpdateAsync(installer, state => state with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error),
                StopTimeStamp = DateTime.UtcNow
            });

            throw;
        }
    }

    private async Task DoProvisionAsync(
        CloudflareTunnelInstallerResource installer,
        CloudflareTunnelResource tunnel,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!tunnel.TryGetLastAnnotation<CloudflareTunnelCredentialsAnnotation>(out var credentials))
        {
            throw new InvalidOperationException("Cloudflare API credentials not configured on tunnel resource.");
        }

        var apiToken = await credentials.ApiToken.GetValueAsync(cancellationToken);
        var accountId = await credentials.AccountId.GetValueAsync(cancellationToken);

        if (string.IsNullOrEmpty(apiToken) || string.IsNullOrEmpty(accountId))
        {
            throw new InvalidOperationException("Cloudflare API token and account ID are required.");
        }

        using var client = new CloudflareApiClient(apiToken, accountId);

        // Find or create the tunnel
        logger.LogInformation("Looking for existing tunnel '{TunnelName}'...", tunnel.Name);
        var existingTunnel = await client.FindTunnelByNameAsync(tunnel.Name, cancellationToken);

        if (existingTunnel is null)
        {
            logger.LogInformation("Creating new Cloudflare tunnel '{TunnelName}'...", tunnel.Name);
            existingTunnel = await client.CreateTunnelAsync(tunnel.Name, cancellationToken);
            installer.WasCreated = true;
            logger.LogInformation("Created tunnel with ID {TunnelId}", existingTunnel.Id);
        }
        else
        {
            logger.LogInformation("Found existing tunnel '{TunnelName}' with ID {TunnelId}", tunnel.Name, existingTunnel.Id);
        }

        tunnel.TunnelId = existingTunnel.Id;

        // Get the tunnel token
        logger.LogInformation("Retrieving tunnel token...");
        var tunnelToken = await client.GetTunnelTokenAsync(existingTunnel.Id, cancellationToken);
        tunnel.TunnelToken = tunnelToken;

        logger.LogInformation("Tunnel '{TunnelName}' provisioned successfully", tunnel.Name);
    }
}
