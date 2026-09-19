using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Usecase.Auth.Command;
using CorpMindAI.Application.Chunking;
using CorpMindAI.Application.Chunking.Children;
using CorpMindAI.Application.Chunking.Normalization;
using CorpMindAI.Application.Chunking.Parents;
using CorpMindAI.Application.Chunking.Sections;
using CorpMindAI.Application.Chunking.Validation;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CorpMindAI.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplicationDI(this IServiceCollection services)
        {
            services.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssembly(typeof(LoginCommandHandler).Assembly);
            });
            services.AddScoped<IDocumentNormalizer, StructuredDocumentNormalizer>();
            services.AddScoped<IHeadingDetector, HeadingDetector>();
            services.AddScoped<ISectionBuilder, SectionBuilder>();
            services.AddScoped<IParentChunkBuilder, ParentChunkBuilder>();
            services.AddScoped<IChildChunkBuilder, ChildChunkBuilder>();
            services.AddScoped<IDocumentChunkingOrchestrator, DocumentChunkingOrchestrator>();
            services.AddScoped<IChunkingResultValidator, ChunkingResultValidator>();
            services.AddScoped<IEmbeddingIndexingService, EmbeddingIndexingService>();
            return services;
        }
    }
}
