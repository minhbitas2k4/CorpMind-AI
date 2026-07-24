using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Settings;
using CorpMindAI.Application.Usecase.Document.Command;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Jobs;
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

            services.Configure<OcrSettings>(
                configuration.GetSection("OcrSettings"));

            // Repositories
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IDocumentRepository, DocumentRepository>();

            // Unit of Work
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<IFileStorage, LocalFileStorage>();

            // OCR Service — HttpClient với timeout từ config
            var ocrSettings = configuration.GetSection("OcrSettings").Get<OcrSettings>() ?? new OcrSettings();
            services.AddHttpClient<IOcrService, OcrHttpClient>(client =>
            {
                client.BaseAddress = new Uri(ocrSettings.ServiceUrl);
                client.Timeout = TimeSpan.FromSeconds(ocrSettings.TimeoutSeconds);
            });

            // Other Services
            services.AddScoped<IJwtService, JwtService>();
            services.AddScoped<PasswordMigrationService>();

            services.AddScoped<IOcrProcessingJob, Infrastructure.Jobs.OcrProcessingJob>();

            // Hangfire Job Scheduler — abstraction để Application layer enqueue jobs mà không phụ thuộc Hangfire
            services.AddScoped<IJobScheduler, HangfireJobScheduler>();

            return services;
        }
    }
}
