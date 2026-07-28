var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

// Simple hello world web server using nginx
var helloWorld = builder.AddContainer("hello-world", "docker.io/library/nginx", "alpine")
    .WithHttpEndpoint(targetPort: 80, name: "http");

// Create a Cloudflare tunnel with auto-creation enabled
var cloudflareTunnel = builder.AddCloudflareTunnel("my-cloudflare-tunnel");

// Expose the nginx container through the Cloudflare tunnel
// This will create a route from the specified hostname to the container's http endpoint
helloWorld.WithCloudflareTunnel(cloudflareTunnel, hostname: "autocreated.shiruba.dev");

builder.Build().Run();
