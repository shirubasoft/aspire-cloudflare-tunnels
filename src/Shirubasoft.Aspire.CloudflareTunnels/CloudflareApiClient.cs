using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shirubasoft.Aspire.CloudflareTunnels;

/// <summary>
/// Exception thrown when a Cloudflare API call fails.
/// </summary>
public class CloudflareApiException : Exception
{
    public int? ErrorCode { get; }
    public bool RecordAlreadyExists { get; }

    public CloudflareApiException(string message, int? errorCode = null, bool recordAlreadyExists = false)
        : base(message)
    {
        ErrorCode = errorCode;
        RecordAlreadyExists = recordAlreadyExists;
    }
}

/// <summary>
/// HTTP client wrapper for interacting with the Cloudflare Tunnel API.
/// </summary>
public sealed class CloudflareApiClient : IDisposable
{
    private const string BaseUrl = "https://api.cloudflare.com/client/v4";
    private readonly HttpClient _httpClient;
    private readonly string _accountId;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CloudflareApiClient(string apiToken, string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        _accountId = accountId;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
    }

    /// <summary>
    /// Ensures a Cloudflare API response is successful.
    /// </summary>
    private static void EnsureCloudflareSuccess<T>(CloudflareApiResponse<T>? response, string operation)
    {
        if (response is null)
        {
            throw new CloudflareApiException($"{operation}: No response received from Cloudflare API");
        }

        if (!response.Success)
        {
            var errors = response.Errors ?? [];
            var errorMessage = errors.Length > 0
                ? string.Join("; ", errors.Select(e => $"[{e.Code}] {e.Message}"))
                : "Unknown error";

            // Check for "already exists" error (code 81053 or similar)
            var recordAlreadyExists = errors.Any(e =>
                e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                e.Code == 81053 || e.Code == 81057);

            throw new CloudflareApiException(
                $"{operation} failed: {errorMessage}",
                errors.FirstOrDefault()?.Code,
                recordAlreadyExists);
        }
    }

    /// <summary>
    /// Finds a tunnel by name.
    /// </summary>
    public async Task<CloudflareTunnelInfo?> FindTunnelByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<CloudflareTunnelInfo[]>>(JsonOptions, cancellationToken);

        return result?.Result?.FirstOrDefault();
    }

    /// <summary>
    /// Creates a new Cloudflare tunnel.
    /// </summary>
    public async Task<CloudflareTunnelInfo> CreateTunnelAsync(string name, CancellationToken cancellationToken = default)
    {
        // Generate a random tunnel secret (32 bytes, base64 encoded)
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var tunnelSecret = Convert.ToBase64String(secretBytes);

        var request = new CreateTunnelRequest(name, tunnelSecret);

        var response = await _httpClient.PostAsJsonAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel",
            request,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<CloudflareTunnelInfo>>(JsonOptions, cancellationToken);

        return result?.Result ?? throw new InvalidOperationException("Failed to create tunnel: no result returned");
    }

    /// <summary>
    /// Gets the tunnel token for connecting cloudflared to the tunnel.
    /// </summary>
    public async Task<string> GetTunnelTokenAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{tunnelId}/token",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<string>>(JsonOptions, cancellationToken);

        return result?.Result ?? throw new InvalidOperationException("Failed to get tunnel token: no result returned");
    }

    /// <summary>
    /// Gets the current tunnel configuration.
    /// </summary>
    public async Task<TunnelConfiguration?> GetTunnelConfigurationAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{tunnelId}/configurations",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<TunnelConfigurationWrapper>>(JsonOptions, cancellationToken);

        return result?.Result?.Config;
    }

    /// <summary>
    /// Updates the tunnel configuration with new ingress rules.
    /// </summary>
    public async Task UpdateTunnelConfigurationAsync(string tunnelId, TunnelConfiguration config, CancellationToken cancellationToken = default)
    {
        var wrapper = new TunnelConfigurationWrapper(config);

        var response = await _httpClient.PutAsJsonAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{tunnelId}/configurations",
            wrapper,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Finds a zone by domain name.
    /// </summary>
    public async Task<CloudflareZoneInfo?> FindZoneByNameAsync(string domainName, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/client/v4/zones?name={Uri.EscapeDataString(domainName)}&account.id={Uri.EscapeDataString(_accountId)}",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<CloudflareZoneInfo[]>>(JsonOptions, cancellationToken);

        return result?.Result?.FirstOrDefault();
    }

    /// <summary>
    /// Creates a DNS CNAME record pointing to the tunnel.
    /// </summary>
    public async Task<CloudflareDnsRecord> CreateTunnelDnsRecordAsync(
        string zoneId,
        string hostname,
        string tunnelId,
        bool proxied = true,
        CancellationToken cancellationToken = default)
    {
        var request = new CreateDnsRecordRequest(
            Type: "CNAME",
            Name: hostname,
            Content: $"{tunnelId}.cfargotunnel.com",
            Proxied: proxied,
            Ttl: 1 // Auto TTL
        );

        var response = await _httpClient.PostAsJsonAsync(
            $"/client/v4/zones/{zoneId}/dns_records",
            request,
            JsonOptions,
            cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<CloudflareDnsRecord>>(JsonOptions, cancellationToken);

        // Check if we got a success response
        if (result?.Success == true && result.Result != null)
        {
            return result.Result;
        }

        // Check if the record already exists (can be 409 Conflict or 200 with error in body)
        var recordAlreadyExists = result?.Errors?.Any(e =>
            e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            e.Code == 81053 || e.Code == 81057) ?? false;

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict || recordAlreadyExists)
        {
            // Try to find and update existing record
            var existing = await FindDnsRecordAsync(zoneId, hostname, cancellationToken);
            if (existing != null)
            {
                return await UpdateDnsRecordAsync(zoneId, existing.Id, request, cancellationToken);
            }
            // If we can't find it, throw the original error
        }

        // If we got here, it's an error
        EnsureCloudflareSuccess(result, "Create DNS record");
        
        // This shouldn't be reached, but just in case
        throw new CloudflareApiException("Create DNS record failed: no result returned");
    }

    /// <summary>
    /// Finds a DNS record by name.
    /// </summary>
    public async Task<CloudflareDnsRecord?> FindDnsRecordAsync(string zoneId, string name, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/client/v4/zones/{zoneId}/dns_records?name={Uri.EscapeDataString(name)}&type=CNAME",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<CloudflareDnsRecord[]>>(JsonOptions, cancellationToken);

        return result?.Result?.FirstOrDefault();
    }

    /// <summary>
    /// Updates an existing DNS record.
    /// </summary>
    internal async Task<CloudflareDnsRecord> UpdateDnsRecordAsync(
        string zoneId,
        string recordId,
        CreateDnsRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync(
            $"/client/v4/zones/{zoneId}/dns_records/{recordId}",
            request,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<CloudflareDnsRecord>>(JsonOptions, cancellationToken);

        return result?.Result ?? throw new InvalidOperationException("Failed to update DNS record: no result returned");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}

#region API DTOs

internal sealed record CloudflareApiResponse<T>(
    bool Success,
    T? Result,
    CloudflareApiError[]? Errors,
    CloudflareApiMessage[]? Messages);

internal sealed record CloudflareApiError(int Code, string Message);

internal sealed record CloudflareApiMessage(int Code, string Message);

internal sealed record CreateTunnelRequest(string Name, string TunnelSecret);

internal sealed record TunnelConfigurationWrapper(TunnelConfiguration Config);

internal sealed record CreateDnsRecordRequest(
    string Type,
    string Name,
    string Content,
    bool Proxied,
    int Ttl);

/// <summary>
/// Represents a Cloudflare tunnel.
/// </summary>
public sealed record CloudflareTunnelInfo(
    string Id,
    string Name,
    string Status,
    string? CreatedAt,
    string? DeletedAt);

/// <summary>
/// Represents a Cloudflare zone.
/// </summary>
public sealed record CloudflareZoneInfo(
    string Id,
    string Name,
    string Status);

/// <summary>
/// Represents a Cloudflare DNS record.
/// </summary>
public sealed record CloudflareDnsRecord(
    string Id,
    string Type,
    string Name,
    string Content,
    bool Proxied,
    int Ttl);

/// <summary>
/// Represents the tunnel configuration containing ingress rules.
/// </summary>
public sealed class TunnelConfiguration
{
    [JsonPropertyName("ingress")]
    public List<IngressRule> Ingress { get; set; } = [];
}

/// <summary>
/// Represents an ingress rule that routes traffic to a service.
/// </summary>
public sealed class IngressRule
{
    /// <summary>
    /// The hostname to match (null for catch-all rule).
    /// </summary>
    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    /// <summary>
    /// The service URL to route to (e.g., "http://container:port" or "http_status:404").
    /// </summary>
    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// Optional path matching.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

#endregion
