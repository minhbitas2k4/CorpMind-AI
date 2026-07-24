using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace CorpMindAI.Infrastructure.Authorization
{
    public class HangfireSimpleAuthFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();

            // Phải authenticated
            if (httpContext.User?.Identity?.IsAuthenticated != true)
                return false;

            // Phải có role "knowledge_manager"
            return httpContext.User.IsInRole("knowledge_manager");
        }
    }
}
