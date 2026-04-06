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
            // Start with the SDK's default middleware (redirect, compress, etc.)
            // then remove the built-in RetryHandler since we replace it with our
            // Polly-based GraphResilienceHandler.
            var defaults = Microsoft.Graph.GraphClientFactory.CreateDefaultHandlers()
                .Where(h => h is not Microsoft.Kiota.Http.HttpClientLibrary.Middleware.RetryHandler)
                .ToList();

            // Prepend our custom handlers (e.g. GraphResilienceHandler) before SDK defaults.
            var allHandlers = new List<DelegatingHandler>(handlers);
            allHandlers.AddRange(defaults);

            var httpClient = Microsoft.Graph.GraphClientFactory.Create(handlers: allHandlers.ToArray());
            Client = new GraphServiceClient(httpClient, credential);
        }
        else
        {
            Client = new GraphServiceClient(credential);
        }
    }
}
