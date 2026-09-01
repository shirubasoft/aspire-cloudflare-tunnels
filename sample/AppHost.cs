var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

// Simple hello world web server using nginx
var helloWorld = builder.AddContainer("hello-world", "docker.io/library/nginx", "alpine")
    .WithHttpEndpoint(targetPort: 80, name: "http");

if (args.Contains("--quick-tunnel", StringComparer.OrdinalIgnoreCase))
{
    builder.AddCloudflareQuickTunnel("my-cloudflare-quick-tunnel")
        .WithReference(helloWorld);
}
else
{
    var cloudflareTunnel = builder.AddCloudflareTunnel("my-cloudflare-tunnel");

    helloWorld.WithCloudflareTunnel(
        cloudflareTunnel,
        hostname: "autocreated.shiruba.dev");
}

builder.Build().Run();
