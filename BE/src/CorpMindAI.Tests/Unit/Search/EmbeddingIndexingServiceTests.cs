using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Services;
using CorpMindAI.Application.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CorpMindAI.Tests.Unit.Search;

[Trait("TestType", "Unit")]
public sealed class EmbeddingIndexingServiceTests
{
    [Fact]
    public async Task Indexing_batches_chunks_then_activates_and_cleans_old_runs()
    {
        var source = new FakeSource
        {
            Chunks = new[]
            {
                new EmbeddingSourceChunk("c1", 5, 9, "run-1", "hash", "v1", "6.0", "one"),
                new EmbeddingSourceChunk("c2", 5, 9, "run-1", "hash", "v1", "6.0", "two"),
                new EmbeddingSourceChunk("c3", 5, 9, "run-1", "hash", "v1", "6.0", "three")
            }
        };
        var vectors = new FakeVectors();
        var outbox = new FakeOutbox();
        var service = new EmbeddingIndexingService(
            source,
            new FakeEmbeddings(),
            vectors,
            outbox,
            new EmbeddingIndexingOptions { BatchSize = 2 },
            NullLogger<EmbeddingIndexingService>.Instance);

        await service.ExecuteAsync(5, "run-1");

        Assert.Equal(2, vectors.UpsertBatches.Count);
        Assert.Equal(1, vectors.ActivateCalls);
        Assert.Equal(1, vectors.DeleteCalls);
        Assert.Equal((5, "run-1"), outbox.Completed);
    }

    private sealed class FakeSource : IEmbeddingIndexSource
    {
        public IReadOnlyList<EmbeddingSourceChunk> Chunks { get; init; } = Array.Empty<EmbeddingSourceChunk>();
        public Task<IReadOnlyList<EmbeddingSourceChunk>> GetChunksForRunAsync(int documentId, string chunkingRunId, CancellationToken cancellationToken = default) => Task.FromResult(Chunks);
        public Task<IReadOnlyDictionary<string, SemanticSearchChunk>> GetChunksByIdsAsync(IReadOnlyCollection<string> childChunkIds, int departmentId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, SemanticSearchChunk>>(new Dictionary<string, SemanticSearchChunk>());
    }

    private sealed class FakeEmbeddings : IEmbeddingGenerator
    {
        public string Model => "text-embedding-3-small";
        public int Dimensions => 3;
        public Task<IReadOnlyList<EmbeddingVector>> GenerateAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EmbeddingVector>>(inputs.Select(_ => new EmbeddingVector(new float[] { 1, 2, 3 })).ToArray());
    }

    private sealed class FakeVectors : IVectorStore
    {
        public List<IReadOnlyList<VectorPoint>> UpsertBatches { get; } = new();
        public int ActivateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public Task EnsureCollectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken = default) { UpsertBatches.Add(points); return Task.CompletedTask; }
        public Task ActivateRunAsync(string chunkingRunId, CancellationToken cancellationToken = default) { ActivateCalls++; return Task.CompletedTask; }
        public Task DeleteOtherRunsAsync(int documentId, string activeChunkingRunId, CancellationToken cancellationToken = default) { DeleteCalls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(IReadOnlyList<float> queryVector, IReadOnlyCollection<int> departmentIds, int limit, double? minimumScore = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchHit>>(Array.Empty<VectorSearchHit>());
    }

    private sealed class FakeOutbox : IEmbeddingIndexOutbox
    {
        public (int DocumentId, string RunId) Completed { get; private set; }
        public Task AddPendingAsync(int documentId, string chunkingRunId, bool reindexCompleted = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkCompletedAsync(int documentId, string chunkingRunId, CancellationToken cancellationToken = default) { Completed = (documentId, chunkingRunId); return Task.CompletedTask; }
    }
}
