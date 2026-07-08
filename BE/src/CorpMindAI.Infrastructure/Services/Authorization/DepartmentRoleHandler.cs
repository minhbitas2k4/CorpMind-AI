using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CorpMindAI.Infrastructure.Services.Authorization
{
    public class DepartmentRoleHandler : AuthorizationHandler<DepartmentRoleRequirement>
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public DepartmentRoleHandler(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DepartmentRoleRequirement requirement)
        {
            // Trích xuất HttpContext từ context.Resource trước, fallback qua HttpContextAccessor
            var httpContext = context.Resource as HttpContext ?? _httpContextAccessor.HttpContext;
            if (httpContext == null) return Task.CompletedTask;

            var routeData = httpContext.GetRouteData();
            var deptIdStr = routeData.Values["department_id"]?.ToString()
                            ?? routeData.Values["departmentId"]?.ToString()
                            ?? httpContext.Request.Query["department_id"].ToString();

            if (string.IsNullOrEmpty(deptIdStr) || !int.TryParse(deptIdStr, out int targetDepartmentId))
            {
                return Task.CompletedTask;
            }

            var userDeptRoleClaims = context.User.FindAll("DepartmentRole").Select(c => c.Value);

            // So khớp cấu trúc Claim dạng: "{AllowedRole}:{TargetDepartmentId}"
            foreach (var allowedRole in requirement.AllowedRoles)
            {
                string expectedClaimValue = $"{allowedRole}:{targetDepartmentId}";

                if (userDeptRoleClaims.Contains(expectedClaimValue))
                {
                    context.Succeed(requirement); 
                    return Task.CompletedTask;
                }
            }

            return Task.CompletedTask;
        }
    }
}
