using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;
using CorpMindAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CorpMindAI.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructureDI(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");

            services.AddDbContext<CorpMindDbContext>(options =>
                options.UseNpgsql(connectionString, b =>
                    b.MigrationsAssembly("CorpMindAI.Infrastructure"))); 

            // Đóng gói việc đăng ký Repositories & Services hạ tầng
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<IJwtService, JwtService>();
            services.AddScoped<PasswordMigrationService>();

            return services;
        }
    }
}
