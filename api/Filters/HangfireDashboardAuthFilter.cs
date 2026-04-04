using Hangfire.Dashboard;

namespace AFHSync.Api.Filters;

/// <summary>
/// Hangfire dashboard authorization filter.
/// In production, this is internal-only (nginx blocks /hangfire from public).
/// The API already requires JWT auth on all controller endpoints.
/// For the dashboard, allow access if the request has a valid JWT cookie.
/// </summary>
public class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true;
    }
}
