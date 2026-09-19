using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Settings;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Services;

public interface IEmbeddingIndexingService
{
    Task ExecuteAsync(int documentId, string chunkingRunId, CancellationToken cancellationToken = default);
}

public sealed class EmbeddingIndexingService : IEmbeddingIndexingService
{
    private readonly IEmbeddingIndexSource _source;
    private readonly IEmbeddingGenerator _embeddings;
    private readonly IVectorStore _vectors;
    private readonly IEmbeddingIndexOutbox _outbox;
    private readonly IChunkingExecutionLock? _executionLock;
    private readonly EmbeddingIndexingOptions _options;
    private readonly ILogger<EmbeddingIndexingService> _logger;

    public EmbeddingIndexingService(
        IEmbeddingIndexSource source,
        IEmbeddingGenerator embeddings,
        IVectorStore vectors,
        IEmbeddingIndexOutbox outbox,
        EmbeddingIndexingOptions options,
        ILogger<EmbeddingIndexingService> logger,
        IChunkingExecutionLock? executionLock = null)
    {
        _source = source;
        _embeddings = embeddings;
        _vectors = vectors;
        _outbox = outbox;
        _executionLock = executionLock;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger;
    }

    public async Task ExecuteAsync(
        int documentId,
        string chunkingRunId,
        CancellationToken cancellationToken = default)
    {
        if (documentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(documentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkingRunId);

        if (_executionLock is null)
        {
            await ExecuteCoreAsync(documentId, chunkingRunId, cancellationToken);
            return;
        }

        await using var lockHandle = await _executionLock.AcquireAsync(documentId, cancellationToken);
        await ExecuteCoreAsync(documentId, chunkingRunId, cancellationToken);
    }

    private async Task ExecuteCoreAsync(
        int documentId,
        string chunkingRunId,
        CancellationToken cancellationToken)
    {

        var chunks = await _source.GetChunksForRunAsync(documentId, chunkingRunId, cancellationToken);
        if (chunks.Count == 0)
        {
            await _outbox.MarkCompletedAsync(documentId, chunkingRunId, cancellationToken);
            _logger.LogWarning(
                "No active chunks found for DocumentId {DocumentId}, ChunkingRunId {ChunkingRunId}; indexing is skipped.",
                documentId,
                chunkingRunId);
            return;
        }

        await _vectors.EnsureCollectionAsync(cancellationToken);
        foreach (var batch in chunks.Chunk(_options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = batch.Select(chunk => chunk.ContextualizedContent).ToArray();
            var embeddings = await _embeddings.GenerateAsync(inputs, cancellationToken);
            if (embeddings.Count != batch.Length)
                throw new InvalidOperationException("Embedding provider returned a different number of vectors than inputs.");

            var points = batch.Zip(embeddings, (chunk, embedding) =>
                    new VectorPoint(
                        chunk.Id,
                        chunk.DocumentId,
                        chunk.DepartmentId,
                        chunk.ChunkingRunId,
                        chunk.SourceContentHash,
                        chunk.SourceSchemaVersion,
                        chunk.ChunkerVersion,
                        $"{_embeddings.Model}:{_embeddings.Dimensions}",
                        embedding.Values))
                .ToArray();
            await _vectors.UpsertAsync(points, cancellationToken);
        }

        // Points are written inactive and become searchable only after every
        // chunk in the run has been embedded successfully.
        await _vectors.ActivateRunAsync(chunkingRunId, cancellationToken);
        await _vectors.DeleteOtherRunsAsync(documentId, chunkingRunId, cancellationToken);
        await _outbox.MarkCompletedAsync(documentId, chunkingRunId, cancellationToken);
        _logger.LogInformation(
            "Indexed {ChunkCount} child chunk(s) for DocumentId {DocumentId}, ChunkingRunId {ChunkingRunId}.",
            chunks.Count,
            documentId,
            chunkingRunId);
    }
}
