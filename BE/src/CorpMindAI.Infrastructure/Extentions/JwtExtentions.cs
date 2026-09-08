using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Authorization;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Services.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace CorpMindAI.Infrastructure.Extentions
{
    public static class JwtExtentions
    {
        public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddHttpContextAccessor();
            
            services.AddScoped<IAuthorizationHandler, DepartmentRoleHandler>();

            var jwtSettings = configuration.GetSection("JwtSettings");
            var secretKey = jwtSettings["SecretKey"] ?? throw new InvalidOperationException("JWT Secret not configured");

            // Cấu hình Authentication
            services.AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(x =>
            {
                x.SaveToken = true;
                x.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        try
                        {
                            var principal = context.Principal;
                            var userIdValue = principal?.FindFirst("sub")?.Value
                                              ?? principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                            var tokenVersionValue = principal?.FindFirst("token_version")?.Value;
                            var tokenDepartmentId = principal?.FindFirst("department_id")?.Value ?? string.Empty;

                            if (!int.TryParse(userIdValue, out var userId) ||
                                !int.TryParse(tokenVersionValue, out var tokenVersion))
                            {
                                context.Fail("Token does not contain the required authorization claims.");
                                return;
                            }

                            var userRepository = context.HttpContext.RequestServices
                                .GetRequiredService<IUserRepository>();
                            var currentState = await userRepository.GetAuthorizationStateAsync(userId);

                            if (currentState is null ||
                                !string.Equals(currentState.Status, "active", StringComparison.OrdinalIgnoreCase) ||
                                currentState.TokenVersion != tokenVersion ||
                                !string.Equals(
                                    tokenDepartmentId,
                                    currentState.DepartmentId?.ToString() ?? string.Empty,
                                    StringComparison.Ordinal) ||
                                !new HashSet<string>(
                                    principal!.FindAll("DepartmentRole").Select(c => c.Value),
                                    StringComparer.Ordinal)
                                .SetEquals(currentState.DepartmentRoles))
                            {
                                context.Fail("Token is no longer authorized.");
                            }
                        }
                        catch
                        {
                            // Fail closed if the current authorization state cannot be checked.
                            context.Fail("Authorization state could not be validated.");
                        }
                    }
                };
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true, 
                    ValidIssuer = jwtSettings["Issuer"],
                    ValidAudience = jwtSettings["Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                    ClockSkew = TimeSpan.Zero
                };
            });

            // Cấu hình Authorization Policies 
            services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();

                // Quyền quản lý tài liệu phòng ban 
                options.AddPolicy("CanManageDepartmentDocument", policy =>
                    policy.Requirements.Add(new DepartmentRoleRequirement("knowledge_manager", "system_admin")));

                
                options.AddPolicy("CanContributeKnowledge", policy =>
                    policy.Requirements.Add(new DepartmentRoleRequirement("knowledge_contributor", "knowledge_manager", "system_admin")));
                // Quyền đóng góp, tải lên tài liệu 
                options.AddPolicy("CanContributeAndManagerKnowledge", policy =>
                    policy.Requirements.Add(new DepartmentRoleRequirement("knowledge_contributor", "knowledge_manager")));
            });

            return services;
        }
    }
}
