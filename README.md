# Shirubasoft.Aspire.CloudflareTunnels

An Aspire integration for [Cloudflare Tunnels](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/), enabling you to expose local services to the internet through Cloudflare's network during development and deployment.

## Features

- **Automatic Tunnel Creation**: Tunnels are automatically created via the Cloudflare API during local development
- **DNS Record Management**: DNS CNAME records are automatically created for your hostnames
- **Ingress Configuration**: Tunnel routes are automatically configured to point to your services
- **Aspire Dashboard Integration**: Monitor tunnel status directly in the Aspire dashboard
- **Aspire Pipeline Integration**: Configure deployment-specific routes as a standard `aspire deploy` pipeline step

## Installation

Install the NuGet package in your AppHost project:

```bash
dotnet add package Shirubasoft.Aspire.CloudflareTunnels
```

Or add it directly to your AppHost `.csproj` file:

```xml
<ItemGroup>
  <PackageReference Include="Shirubasoft.Aspire.CloudflareTunnels" Version="*" />
</ItemGroup>
```

If you are upgrading from `Shirubasoft.Aspire.Cloudflared`, replace the package reference with the new package ID. The AppHost APIs remain `AddCloudflareTunnel` and `WithCloudflareTunnel`.

## Quick Start

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add your service (e.g., a simple nginx container)
var webApp = builder.AddContainer("web-app", "docker.io/library/nginx", "alpine")
    .WithHttpEndpoint(targetPort: 80, name: "http");

// Create a Cloudflare tunnel
var tunnel = builder.AddCloudflareTunnel("my-tunnel");

// Expose your service through the tunnel
webApp.WithCloudflareTunnel(tunnel, hostname: "myapp.example.com");

builder.Build().Run();
```

When you run the application, Aspire will prompt you for:
- **Account ID**: Your Cloudflare account ID
- **API Token**: A Cloudflare API token with the required permissions

## Getting Your Cloudflare Account ID

1. Log in to the [Cloudflare Dashboard](https://dash.cloudflare.com/)
2. Select any domain in your account
3. On the **Overview** page, scroll down to find **Account ID** in the right sidebar
4. Copy the Account ID value

## Creating a Cloudflare API Token

The integration requires a Cloudflare API token with specific permissions to create tunnels and DNS records.

### Step-by-Step Guide

1. Go to the [Cloudflare Dashboard](https://dash.cloudflare.com/)
2. Navigate to **My Profile** → **API Tokens** (or for account tokens: **Manage Account** → **API Tokens**)
3. Click **Create Token**
4. Select **Create Custom Token**
5. Configure the token with the following settings:

### Required Permissions

| Permission | Access Level | Scope | Description |
|------------|--------------|-------|-------------|
| **Account** → **Cloudflare Tunnel** | Edit | Your Account | Create, read, and manage tunnels |
| **Zone** → **Zone** | Read | All zones (or specific zones) | Look up zone IDs for DNS records |
| **Zone** → **DNS** | Edit | All zones (or specific zones) | Create CNAME records for tunnel routes |

### Token Configuration Screenshot Reference

Your custom token configuration should look like this:

```
Token name: Aspire Cloudflared Development

Permissions:
├── Account
│   └── Cloudflare Tunnel: Edit
└── Zone
    ├── Zone: Read
    └── DNS: Edit

Account Resources: Include → Your Account
Zone Resources: Include → All zones (or specific zones you'll use)
```

6. Click **Continue to summary**
7. Review the permissions and click **Create Token**
8. **Copy the token immediately** — it will only be shown once!

### Verifying Your Token

You can verify your token works by running:

```bash
curl "https://api.cloudflare.com/client/v4/user/tokens/verify" \
  --header "Authorization: Bearer YOUR_API_TOKEN"
```

A successful response will show:

```json
{
  "success": true,
  "result": {
    "status": "active"
  }
}
```

## API Reference

### AddCloudflareTunnel

Creates a Cloudflare tunnel resource that can be used to expose services.

```csharp
IResourceBuilder<CloudflareTunnelResource> AddCloudflareTunnel(
    this IDistributedApplicationBuilder builder,
    string name,
    int? metricsPort = null)
```

**Parameters:**
- `name`: The name of the tunnel (used in Cloudflare and as the resource name in Aspire)
- `metricsPort`: Optional port for the cloudflared metrics endpoint

**Required Parameters (prompted at runtime):**
- `{name}-account-id`: Your Cloudflare account ID
- `{name}-api-token`: Your Cloudflare API token with tunnel permissions

### WithCloudflareTunnel

Exposes a resource's endpoint through a Cloudflare tunnel.

```csharp
IResourceBuilder<T> WithCloudflareTunnel<T>(
    this IResourceBuilder<T> builder,
    IResourceBuilder<CloudflareTunnelResource> tunnel,
    string hostname,
    string endpointName = "http")
    where T : IResourceWithEndpoints
```

**Parameters:**
- `tunnel`: The Cloudflare tunnel resource to route through
- `hostname`: The public hostname for this route (e.g., `"api.example.com"`)
- `endpointName`: The name of the endpoint to expose (defaults to `"http"`)

## Complete Example

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Create a Cloudflare tunnel
var tunnel = builder.AddCloudflareTunnel("my-cloudflare-tunnel");

// Example 1: Expose an nginx container
var nginx = builder.AddContainer("hello-world", "docker.io/library/nginx", "alpine")
    .WithHttpEndpoint(targetPort: 80, name: "http");

nginx.WithCloudflareTunnel(tunnel, hostname: "hello.example.com");

// Example 2: Expose a .NET project
var api = builder.AddProject<Projects.MyApi>("api")
    .WithHttpEndpoint(port: 5000, name: "http");

api.WithCloudflareTunnel(tunnel, hostname: "api.example.com");

// Example 3: Expose multiple services through the same tunnel
var frontend = builder.AddContainer("frontend", "my-frontend", "latest")
    .WithHttpEndpoint(targetPort: 3000, name: "http");

frontend.WithCloudflareTunnel(tunnel, hostname: "app.example.com");

builder.Build().Run();
```

## Deployment Considerations

### Production Deployment

During **local development** (run mode), the integration automatically:
- Creates the tunnel if it doesn't exist
- Retrieves the tunnel token
- Configures DNS records and ingress routes

For **deployment**, pre-provision the tunnel and provide these Aspire parameters:

| Parameter | Purpose |
|-----------|---------|
| `{tunnel-name}-account-id` | Finds the existing tunnel and its DNS zones |
| `{tunnel-name}-api-token` | Configures Cloudflare DNS and ingress routes |
| `{tunnel-name}-tunnel-token` | Authenticates the deployed cloudflared connector |

1. **Create the tunnel** if it does not already exist:

   ```bash
   cloudflared tunnel create my-tunnel-name
   ```

2. **Get the tunnel token**:

   ```bash
   cloudflared tunnel token my-tunnel-name
   ```

   Or retrieve it from the [Cloudflare Zero Trust Dashboard](https://one.dash.cloudflare.com/) under **Networks** → **Tunnels**.

3. **Supply the parameters through your pipeline's secret store**. Aspire maps dashes in parameter names to underscores in environment-variable names:

   ```bash
   export Parameters__my_cloudflare_tunnel_account_id="..."
   export Parameters__my_cloudflare_tunnel_api_token="..."
   export Parameters__my_cloudflare_tunnel_tunnel_token="..."

   aspire deploy --non-interactive
   ```

`aspire deploy` contributes a `configure-{tunnel-name}-cloudflare-routes` step to the AppHost deployment pipeline. The step:

- runs after Aspire publishes the deployment artifacts
- uses the selected compute environment to resolve each service's deployment URL
- finds the pre-provisioned Cloudflare tunnel
- creates the DNS records and updates the tunnel ingress configuration
- is required by Aspire's standard `deploy` step

You can inspect or run the step directly:

```bash
aspire deploy --list-steps
aspire do configure-my-cloudflare-tunnel-cloudflare-routes --non-interactive
```

`aspire publish` remains side-effect free. It emits the cloudflared resource and tunnel-token parameter, but it does not call the Cloudflare API.

### Why Manual Token for Deployment?

Automatic tunnel creation is intentionally limited to local run mode. In a deployment pipeline:

- The Cloudflare API token stays in the pipeline environment and is not passed to the deployed cloudflared container
- The deployed container receives only the tunnel-scoped token
- Existing infrastructure remains under explicit lifecycle control
- This follows the principle of least privilege

## How It Works

### Run Mode (Local Development)

1. **Tunnel Installer** creates/finds the tunnel via Cloudflare API
2. **Tunnel Token** is retrieved and passed to the cloudflared container
3. **Route Installer** creates DNS CNAME records pointing to `{tunnel-id}.cfargotunnel.com`
4. **Ingress Configuration** is updated with routes from hostname to your services
5. **Cloudflared Container** starts and connects to Cloudflare's network

### Publish and Deploy Mode

1. **Publish** emits the cloudflared container and a secret tunnel-token parameter without external side effects
2. **Deploy Pipeline** resolves target URLs from Aspire's selected compute environment
3. **Cloudflare Pipeline Step** configures DNS and ingress for the existing tunnel
4. **Cloudflared Container** starts with the provided tunnel token

## Troubleshooting

### "Zone Not Found" Error

This means the API token doesn't have access to the domain's zone. Ensure:
- The domain is registered in your Cloudflare account
- The API token has **Zone: Read** permission for that zone
- The zone is active (not pending)

### "Tunnel Token Not Available" Error

The tunnel installer hasn't completed yet. This usually resolves automatically. If it persists:
- Check the Aspire dashboard for installer status
- Verify your API token has **Cloudflare Tunnel: Edit** permission
- Check the logs for API errors

### DNS Record Already Exists

This is normal if you've run the application before. The integration will skip creating duplicate records.

### Tunnel Not Connecting

- Verify your API token is valid and not expired
- Check that the tunnel token is correct
- Retrieve the token again if the tunnel was recreated or its credentials were rotated
- Ensure no firewall is blocking outbound connections to Cloudflare

## Integration Tests

The live integration test uses the sample AppHost to exercise both `aspire deploy` and local run mode:

```bash
export CLOUDFLARE_ACCOUNT_ID="..."
export CLOUDFLARE_API_TOKEN="..."
export CLOUDFLARE_TUNNEL_TOKEN="..."

./scripts/run-integration-tests.sh
```

The script deploys the Docker Compose environment, verifies the public nginx route, destroys the test deployment, starts the local AppHost, and verifies the route again. GitHub Actions calls the same script with repository secrets.

## Security Best Practices

1. **Never commit API tokens** to source control
2. **Use User Secrets** for local development:
   ```bash
   dotnet user-secrets set "Parameters:my-tunnel-api-token" "your-token-here"
   dotnet user-secrets set "Parameters:my-tunnel-account-id" "your-account-id"
   ```
3. **Rotate tokens** if they may have been exposed
4. **Limit token scope** to only the zones you need
5. **Use account-owned tokens** for shared development environments

## License

See [LICENSE](LICENSE) for details.
