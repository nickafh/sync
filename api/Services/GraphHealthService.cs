using Azure.Identity;
using Microsoft.Graph;

namespace AFHSync.Api.Services;

/// <summary>
/// Validates Entra app registration credentials and Graph API permissions.
/// Per D-11: tests tenant ID, client ID/secret, and required permissions.
/// </summary>
public class GraphHealthService
{
    private readonly IConfiguration _config;
    private readonly ILogger<GraphHealthService> _logger;

    public GraphHealthService(IConfiguration config, ILogger<GraphHealthService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<GraphHealthResult> CheckAsync()
    {
        var tenantId = _config["Graph:TenantId"];
        var clientId = _config["Graph:ClientId"];
        var clientSecret = _config["Graph:ClientSecret"];

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new GraphHealthResult
            {
                IsHealthy = false,
                Message = "Graph credentials not configured",
                Permissions = []
            };
        }

        try
        {
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var client = new GraphServiceClient(credential);

            // Test authentication by reading tenant organization info
            var org = await client.Organization.GetAsync();

            // Check app permissions: try to list one user (requires User.Read.All)
            var users = await client.Users.GetAsync(config =>
            {
                config.QueryParameters.Top = 1;
                config.QueryParameters.Select = ["id", "displayName"];
            });

            return new GraphHealthResult
            {
                IsHealthy = true,
                Message = "Graph connection verified",
                TenantName = org?.Value?.FirstOrDefault()?.DisplayName,
                Permissions =
                [
                    "User.Read.All (verified - user query succeeded)",
                    "Group.Read.All (not tested - requires Phase 2+)",
                    "GroupMember.Read.All (not tested - requires Phase 2+)",
                    "Contacts.ReadWrite (not tested - requires Phase 2+)",
                    "MailboxSettings.Read (not tested - requires Phase 2+)"
                ]
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph health check failed");
            return new GraphHealthResult
            {
                IsHealthy = false,
                Message = $"Graph connection failed: {ex.Message}",
                Permissions = []
            };
        }
    }
}

public class GraphHealthResult
{
    public bool IsHealthy { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? TenantName { get; set; }
    public string[] Permissions { get; set; } = [];
}
