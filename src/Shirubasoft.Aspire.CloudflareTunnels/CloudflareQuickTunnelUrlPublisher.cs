using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Shirubasoft.Aspire.CloudflareTunnels;

internal sealed partial class CloudflareQuickTunnelUrlPublisher(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService)
{
    private const string PublicEndpointName = "public";
    private static readonly TimeSpan PublicUrlDiscoveryTimeout = TimeSpan.FromSeconds(30);

    public async Task PublishAsync(
        CloudflareQuickTunnelResource tunnel,
        CancellationToken cancellationToken)
    {
        var logger = loggerService.GetLogger(tunnel);
        var publicUrl = await FindPublicUrlAsync(tunnel, cancellationToken);

        if (publicUrl is null)
        {
            logger.LogWarning(
                "Cloudflare Quick Tunnel started without reporting a public URL");
            return;
        }

        tunnel.PublicUrl = publicUrl;

        await notificationService.PublishUpdateAsync(tunnel, snapshot => snapshot with
        {
            Urls =
            [
                ..snapshot.Urls.Where(url =>
                    !string.Equals(url.Name, PublicEndpointName, StringComparison.OrdinalIgnoreCase)),
                new UrlSnapshot(PublicEndpointName, publicUrl, IsInternal: false)
                {
                    DisplayProperties = new("Public endpoint")
                }
            ]
        });

        logger.LogInformation(
            "Cloudflare Quick Tunnel available at {PublicUrl}",
            publicUrl);
    }

    private async Task<string?> FindPublicUrlAsync(
        CloudflareQuickTunnelResource tunnel,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(PublicUrlDiscoveryTimeout);

        try
        {
            await foreach (var lines in loggerService
                .WatchAsync(tunnel)
                .WithCancellation(timeoutSource.Token))
            {
                foreach (var line in lines)
                {
                    var match = QuickTunnelUrl().Match(line.Content);
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    [GeneratedRegex(
        @"https://[a-z0-9-]+\.trycloudflare\.com",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuickTunnelUrl();
}
