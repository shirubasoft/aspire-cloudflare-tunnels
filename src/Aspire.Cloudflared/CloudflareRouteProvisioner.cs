using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Cloudflared;

/// <summary>
/// Handles the configuration of routes for Cloudflare tunnels via the Cloudflare API.
/// This class is responsible for creating DNS records and updating tunnel ingress configuration.
/// </summary>
internal sealed class CloudflareRouteProvisioner(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService)
{
    /// <summary>
    /// Configures all published routes associated with a tunnel.
    /// Creates DNS records and updates the tunnel's ingress configuration.
    /// </summary>
    public async Task ConfigureRoutesAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        CancellationToken cancellationToken)
    {
        var logger = loggerService.GetLogger(tunnel);

        try
        {
            foreach (var route in routes)
            {
                await notificationService.PublishUpdateAsync(route, state => state with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info),
                    StartTimeStamp = DateTime.UtcNow
                });
            }

            if (!tunnel.TryGetLastAnnotation<CloudflareTunnelCredentialsAnnotation>(out var credentials))
            {
                logger.LogWarning("Cloudflare API credentials not found for tunnel '{TunnelName}'", tunnel.Name);
                throw new InvalidOperationException("Cloudflare API credentials not available.");
            }

            var apiToken = await credentials.ApiToken.GetValueAsync(cancellationToken);
            var accountId = await credentials.AccountId.GetValueAsync(cancellationToken);

            if (string.IsNullOrEmpty(apiToken) || string.IsNullOrEmpty(accountId))
            {
                throw new InvalidOperationException("Cloudflare API credentials not available.");
            }

            using var client = new CloudflareApiClient(apiToken, accountId);

            // Wait for tunnel to be provisioned
            if (string.IsNullOrEmpty(tunnel.TunnelId))
            {
                logger.LogWarning("Tunnel '{TunnelName}' not yet provisioned, skipping route configuration", tunnel.Name);
                
                throw new InvalidOperationException("Tunnel not yet provisioned.");
            }

            await DoConfigureRoutesAsync(client, tunnel, routes, logger, cancellationToken);

            // Mark all published routes as finished.
            foreach (var route in routes)
            {
                await notificationService.PublishUpdateAsync(route, state => state with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success)
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to configure routes for tunnel '{TunnelName}'", tunnel.Name);

            foreach (var route in routes)
            {
                await notificationService.PublishUpdateAsync(route, state => state with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error),
                    StopTimeStamp = DateTime.UtcNow
                });
            }

            throw;
        }
    }

    private async Task DoConfigureRoutesAsync(
        CloudflareApiClient client,
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        ILogger tunnelLogger,
        CancellationToken cancellationToken)
    {
        var config = await client.GetTunnelConfigurationAsync(tunnel.TunnelId!, cancellationToken)
            ?? new TunnelConfiguration();

        var managedHostnames = routes
            .Select(route => route.Hostname)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Preserve routes managed outside this AppHost while replacing the routes declared
        // here. The catch-all rule must remain last, so it is rebuilt below.
        config.Ingress.RemoveAll(rule =>
            rule.Hostname is null ||
            managedHostnames.Contains(rule.Hostname));

        foreach (var route in routes)
        {
            var logger = loggerService.GetLogger(route);

            // Create DNS record
            logger.LogInformation("Looking up the Cloudflare zone for {Hostname}...", route.Hostname);
            var zone = await FindZoneAsync(client, route.Hostname, cancellationToken);

            if (zone is null)
            {
                var errorMessage = $"Could not find a Cloudflare zone for '{route.Hostname}'. " +
                    $"DNS record creation cannot continue. " +
                    "Make sure the domain is registered in your Cloudflare account and the API token has Zone:Read permission.";
                logger.LogError(errorMessage);

                await notificationService.PublishUpdateAsync(route, state => state with
                {
                    State = new ResourceStateSnapshot("Zone Not Found", KnownResourceStateStyles.Error)
                });

                throw new InvalidOperationException(errorMessage);
            }

            logger.LogInformation("Found zone {ZoneId} for domain {Domain}. Creating DNS CNAME record for {Hostname} -> {TunnelId}.cfargotunnel.com",
                zone.Id, zone.Name, route.Hostname, tunnel.TunnelId);

            try
            {
                await client.CreateTunnelDnsRecordAsync(zone.Id, route.Hostname, tunnel.TunnelId!, cancellationToken: cancellationToken);
                route.DnsRecordCreated = true;
                logger.LogInformation("DNS record created successfully for {Hostname}", route.Hostname);
            }
            catch (CloudflareApiException ex) when (ex.RecordAlreadyExists)
            {
                logger.LogInformation("DNS record for {Hostname} already exists, skipping creation", route.Hostname);
                route.DnsRecordCreated = true;
            }
            catch (CloudflareApiException ex)
            {
                logger.LogError(ex, "Failed to create DNS record for {Hostname}: {Message}", route.Hostname, ex.Message);
                throw;
            }

            var serviceUrl = await BuildServiceUrlAsync(route, cancellationToken);

            config.Ingress.Add(new IngressRule
            {
                Hostname = route.Hostname,
                Service = serviceUrl
            });

            logger.LogInformation("Added ingress rule: {Hostname} -> {Service}", route.Hostname, serviceUrl);
        }

        // Add required catch-all rule
        config.Ingress.Add(new IngressRule
        {
            Service = "http_status:404"
        });

        tunnelLogger.LogInformation("Updating tunnel configuration with {RouteCount} routes...", routes.Count);
        await client.UpdateTunnelConfigurationAsync(tunnel.TunnelId!, config, cancellationToken);
    }

    private static async Task<string> BuildServiceUrlAsync(
        PublishedRouteResource route,
        CancellationToken cancellationToken)
    {
        var serviceUrl = await route.TargetEndpoint.GetValueAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(serviceUrl)
            ? serviceUrl
            : throw new InvalidOperationException(
                $"Endpoint '{route.TargetEndpoint.EndpointName}' for resource '{route.TargetResource.Name}' could not be resolved.");
    }

    private static async Task<CloudflareZoneInfo?> FindZoneAsync(
        CloudflareApiClient client,
        string hostname,
        CancellationToken cancellationToken)
    {
        var labels = hostname
            .TrimEnd('.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < labels.Length - 1; index++)
        {
            var candidate = string.Join('.', labels[index..]);
            var zone = await client.FindZoneByNameAsync(candidate, cancellationToken);

            if (zone is not null)
            {
                return zone;
            }
        }

        return null;
    }
}
