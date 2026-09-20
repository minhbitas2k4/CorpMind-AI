using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Chunking.Models;
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
using Microsoft.Extensions.Options;

namespace CorpMindAI.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructureDI(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            var openAiSettings = configuration.GetSection("OpenAI:Embeddings").Get<OpenAiEmbeddingSettings>()
                ?? new OpenAiEmbeddingSettings();

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

            services.AddOptions<OpenAiEmbeddingSettings>()
                .Bind(configuration.GetSection("OpenAI:Embeddings"))
                .Validate(settings =>
                {
                    settings.Validate();
                    return true;
                }, "OpenAI embedding settings are invalid.")
                .ValidateOnStart();
            services.AddOptions<QdrantSettings>()
                .Bind(configuration.GetSection("Qdrant"))
                .Validate(settings =>
                {
                    settings.Validate();
                    return true;
                }, "Qdrant settings are invalid.")
                .ValidateOnStart();

            var chunkingOptions = configuration.GetSection("Chunking").Get<ChunkingOptions>()
                ?? new ChunkingOptions();
            chunkingOptions.Validate();
            services.AddSingleton(chunkingOptions);
            var embeddingIndexingOptions = configuration.GetSection("EmbeddingIndexing").Get<EmbeddingIndexingOptions>()
                ?? new EmbeddingIndexingOptions();
            embeddingIndexingOptions.Validate();
            services.AddSingleton(embeddingIndexingOptions);
            var semanticSearchOptions = configuration.GetSection("SemanticSearch").Get<SemanticSearchOptions>()
                ?? new SemanticSearchOptions();
            semanticSearchOptions.Validate();
            services.AddSingleton(semanticSearchOptions);
            var ragOptions = configuration.GetSection("Rag").Get<RagOptions>()
                ?? new RagOptions();
            ragOptions.Validate();
            services.AddSingleton(ragOptions);
            var translationSection = configuration.GetSection("OpenAI:Translation");
            var translationSettings = translationSection.Get<OpenAiQueryTranslationSettings>()
                ?? new OpenAiQueryTranslationSettings();
            if (string.IsNullOrWhiteSpace(translationSection["Endpoint"]))
                translationSettings.Endpoint = openAiSettings.Endpoint;
            if (string.IsNullOrWhiteSpace(translationSection["ApiKey"]))
                translationSettings.ApiKey = openAiSettings.ApiKey;
            translationSettings.Validate();
            services.AddSingleton(Options.Create(translationSettings));
            var answerSection = configuration.GetSection("OpenAI:Answer");
            var answerSettings = answerSection.Get<OpenAiAnswerSettings>()
                ?? new OpenAiAnswerSettings();
            if (string.IsNullOrWhiteSpace(answerSettings.Endpoint))
                answerSettings.Endpoint = openAiSettings.Endpoint;
            if (string.IsNullOrWhiteSpace(answerSettings.ApiKey))
                answerSettings.ApiKey = openAiSettings.ApiKey;
            answerSettings.Validate();
            services.AddSingleton(Options.Create(answerSettings));

            // Repositories
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IDocumentRepository, DocumentRepository>();
            services.AddScoped<IChunkRepository, ChunkRepository>();
            services.AddScoped<IChunkingOutbox, ChunkingOutbox>();
            services.AddScoped<IEmbeddingIndexOutbox, EmbeddingIndexOutbox>();
            services.AddScoped<IEmbeddingIndexSource, EmbeddingIndexSource>();

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
            services.AddScoped<IDocumentChunkingJob, Infrastructure.Jobs.DocumentChunkingJob>();
            services.AddScoped<IChunkingOutboxDispatcher, ChunkingOutboxDispatcher>();
            services.AddScoped<IEmbeddingIndexOutboxDispatcher, EmbeddingIndexOutboxDispatcher>();
            services.AddScoped<IEmbeddingIndexingJob, EmbeddingIndexingJob>();
            services.AddScoped<ITokenCounter, ChunkingTokenCounter>();
            services.AddSingleton<IChunkingExecutionLock>(_ =>
                new PostgreSqlChunkingExecutionLock(connectionString
                    ?? throw new InvalidOperationException("DefaultConnection is required.")));

            // Hangfire Job Scheduler — abstraction để Application layer enqueue jobs mà không phụ thuộc Hangfire
            services.AddScoped<IJobScheduler, HangfireJobScheduler>();

            services.AddHttpClient<IEmbeddingGenerator, OpenAiEmbeddingGenerator>(client =>
            {
                client.BaseAddress = new Uri(openAiSettings.Endpoint.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(openAiSettings.TimeoutSeconds);
            });
            services.AddHttpClient<IQueryTranslator, OpenAiQueryTranslator>(client =>
            {
                client.BaseAddress = new Uri(translationSettings.Endpoint.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(translationSettings.TimeoutSeconds);
            });
            services.AddHttpClient<IAnswerGenerator, OpenAiAnswerGenerator>(client =>
            {
                client.BaseAddress = new Uri(answerSettings.Endpoint.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(answerSettings.TimeoutSeconds);
            });

            var qdrantSettings = configuration.GetSection("Qdrant").Get<QdrantSettings>()
                ?? new QdrantSettings();
            services.AddHttpClient<IVectorStore, QdrantVectorStore>(client =>
            {
                client.BaseAddress = new Uri(qdrantSettings.Endpoint.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(qdrantSettings.TimeoutSeconds);
            });

            return services;
        }
    }
}
