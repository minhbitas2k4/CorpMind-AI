using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Settings;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;
using CorpMindAI.Infrastructure.Services;
using CorpMindAI.Infrastructure.Settings;
using CorpMindAI.Infrastructure.Storage;
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

            // Settings — đọc từ appsettings.json, không hard-code
            services.Configure<StorageSettings>(
                configuration.GetSection("StorageSettings"));

            services.Configure<UploadSettings>(
                configuration.GetSection("UploadSettings"));

            // Repositories
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IDocumentRepository, DocumentRepository>();

            // Unit of Work
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<IFileStorage, LocalFileStorage>();

            // Other Services
            services.AddScoped<IJwtService, JwtService>();
            services.AddScoped<PasswordMigrationService>();

            return services;
        }
    }
}
