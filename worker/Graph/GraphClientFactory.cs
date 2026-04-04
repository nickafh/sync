using Azure.Identity;
using Microsoft.Graph;

namespace AFHSync.Worker.Graph;

/// <summary>
/// Singleton factory that creates and exposes a <see cref="GraphServiceClient"/> configured
/// with application credentials (client credentials flow). Supports optional
/// <see cref="DelegatingHandler"/> injection so Plan 02's GraphResilienceHandler can be
/// plugged in via DI without modifying this class.
///
/// When DelegatingHandlers are provided, the Graph HTTP pipeline is built using
/// <see cref="Microsoft.Graph.GraphClientFactory.Create"/> which injects the handlers
/// into the request pipeline. When no handlers are provided, the simple credential-only
/// path is used.
/// </summary>
public class GraphClientFactory
{
    public GraphServiceClient Client { get; }

    public GraphClientFactory(IConfiguration config, IEnumerable<DelegatingHandler>? handlers = null)
    {
        var tenantId = config["Graph:TenantId"]
            ?? throw new InvalidOperationException("Graph:TenantId configuration is required");
        var clientId = config["Graph:ClientId"]
            ?? throw new InvalidOperationException("Graph:ClientId configuration is required");
        var clientSecret = config["Graph:ClientSecret"]
            ?? throw new InvalidOperationException("Graph:ClientSecret configuration is required");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);

        if (handlers?.Any() == true)
        {
            // Build HttpClient with custom handlers injected into the Graph middleware pipeline.
            // Microsoft.Graph.GraphClientFactory.Create with DelegatingHandlers sets up the
            // standard Graph middleware (compression, redirect, retry) plus our custom handlers.
            var handlerArray = handlers.ToArray();
            var httpClient = Microsoft.Graph.GraphClientFactory.Create(handlers: handlerArray);
            Client = new GraphServiceClient(httpClient, credential);
        }
        else
        {
            Client = new GraphServiceClient(credential);
        }
    }
}
