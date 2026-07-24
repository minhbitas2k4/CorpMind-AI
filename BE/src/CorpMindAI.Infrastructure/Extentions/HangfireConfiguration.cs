using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CorpMindAI.Infrastructure.Extentions
{
    public static class HangfireConfiguration
    {
        public static IServiceCollection AddConfigureHangfire(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddHangfire(x =>
            {
                x.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                 .UseSimpleAssemblyNameTypeSerializer()
                 .UseRecommendedSerializerSettings()
                 .UsePostgreSqlStorage(y =>
                 {
                     y.UseNpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
                 });
            });

            return services;
        }

        public static IServiceCollection AddHangfireServerWithConfig(
        this IServiceCollection services, IConfiguration configuration)
        {
            var section = configuration.GetSection("HangfireSettings");

            var serverName = section.GetValue<string>("ServerName") ?? "DocuMind-Worker";
            var workerCount = section.GetValue<int?>("WorkerCount") ?? 5;
            var queues = section.GetSection("Queues").Get<string[]>()
                         ?? new[] { "default", "processing" };

            services.AddHangfireServer(x =>
            {
                x.ServerName = serverName;
                x.WorkerCount = workerCount;
                x.Queues = queues;
            });

            return services;
        }
    }
}
