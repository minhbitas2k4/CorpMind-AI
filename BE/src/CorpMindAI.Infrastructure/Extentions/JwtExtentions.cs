using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Authorization;
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
            
            // Xử lý Captive Dependency: Đăng ký Authorization Handler là Scoped 
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
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true, // Bắt buộc kiểm tra chữ ký mã hóa của Token
                    ValidIssuer = jwtSettings["Issuer"],
                    ValidAudience = jwtSettings["Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                    ClockSkew = TimeSpan.Zero
                };
            });

            // Cấu hình Authorization Policies 
            services.AddAuthorization(options =>
            {
                // 1. Quyền quản lý tài liệu phòng ban (Yêu cầu role knowledge_manager hoặc system_admin)
                options.AddPolicy("CanManageDepartmentDocument", policy =>
                    policy.Requirements.Add(new DepartmentRoleRequirement("knowledge_manager", "system_admin")));

                // 2. Quyền đóng góp, tải lên tài liệu (Yêu cầu contributor hoặc manager phòng ban đó)
                options.AddPolicy("CanContributeKnowledge", policy =>
                    policy.Requirements.Add(new DepartmentRoleRequirement("knowledge_contributor", "knowledge_manager", "system_admin")));
            });

            return services;
        }
    }
}
