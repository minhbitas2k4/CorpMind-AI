using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Services;
using CorpMindAI.Application.Settings;
using CorpMindAI.Application.Usecase.Search.Query;
using CorpMindAI.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CorpMindAI.Tests.Unit.Search;

[Trait("TestType", "Unit")]
public sealed class SemanticSearchTests
{
    [Fact]
    public async Task Search_applies_the_department_filter_and_returns_database_content()
    {
        var users = new FakeUsers(new User
        {
            Id = 7,
            Status = "active",
            UserRoles =
            {
                new UserRole
                {
                    DepartmentId = 12,
                    Role = new Role { RoleName = "knowledge_contributor" }
                }
            }
        });
        var vectors = new FakeVectors
        {
            Hits = new[] { new VectorSearchHit("child-1", 42, 12, "run-1", 0.91) }
        };
        var source = new FakeSource
        {
            SearchChunks = new Dictionary<string, SemanticSearchChunk>
            {
                ["child-1"] = new(
                    "child-1", 42, 12, "parent-1", new[] { "Policy" }, "raw answer", 2, 2, new[] { "c1" })
            }
        };
        var translator = new FakeTranslator();
        var handler = new SemanticSearchQueryHandler(
            users,
            new FakeEmbeddings(),
            vectors,
            source,
            translator,
            new SemanticSearchOptions(),
            NullLogger<SemanticSearchQueryHandler>.Instance);

        var result = await handler.Handle(new SemanticSearchQuery(7, 12, "where is the policy?"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("raw answer", Assert.Single(result.Data!).Content);
        Assert.Equal(new[] { 12 }, vectors.DepartmentIds);
        Assert.Equal(1, vectors.EnsureCalls);
        Assert.Equal(0, translator.CallCount);
    }

    [Fact]
    public async Task Search_rejects_a_user_without_department_membership_before_embedding()
    {
        var embeddings = new FakeEmbeddings();
        var handler = new SemanticSearchQueryHandler(
            new FakeUsers(new User { Id = 7, Status = "active" }),
            embeddings,
            new FakeVectors(),
            new FakeSource(),
            new FakeTranslator(),
            new SemanticSearchOptions(),
            NullLogger<SemanticSearchQueryHandler>.Instance);

        var result = await handler.Handle(new SemanticSearchQuery(7, 12, "secret"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, embeddings.CallCount);
    }

    [Fact]
    public async Task Search_uses_configured_candidate_limit_and_score_threshold()
    {
        var users = new FakeUsers(new User
        {
            Id = 7,
            Status = "active",
            UserRoles =
            {
                new UserRole
                {
                    DepartmentId = 12,
                    Role = new Role { RoleName = "knowledge_contributor" }
                }
            }
        });
        var vectors = new FakeVectors
        {
            Hits = new[]
            {
                new VectorSearchHit("child-1", 42, 12, "run-1", 0.9),
                new VectorSearchHit("child-2", 42, 12, "run-1", 0.8)
            }
        };
        var source = new FakeSource
        {
            SearchChunks = new Dictionary<string, SemanticSearchChunk>
            {
                ["child-1"] = new(
                    "child-1", 42, 12, "parent-1", new[] { "Policy" }, "raw answer", 2, 2, new[] { "c1" }),
                ["child-2"] = new(
                    "child-2", 42, 12, "parent-1", new[] { "Policy" }, "second answer", 2, 2, new[] { "c2" })
            }
        };
        var handler = new SemanticSearchQueryHandler(
            users,
            new FakeEmbeddings(),
            vectors,
            source,
            new FakeTranslator(),
            new SemanticSearchOptions { CandidateLimit = 20, MinimumScore = 0.4 },
            NullLogger<SemanticSearchQueryHandler>.Instance);

        var result = await handler.Handle(new SemanticSearchQuery(7, 12, "policy", 1), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Data!);
        Assert.Equal(20, vectors.RequestedLimit);
        Assert.Equal(0.4, vectors.MinimumScore);
    }

    [Fact]
    public async Task Search_excludes_heading_only_chunks_from_returned_results()
    {
        var users = new FakeUsers(new User
        {
            Id = 7,
            Status = "active",
            UserRoles =
            {
                new UserRole
                {
                    DepartmentId = 12,
                    Role = new Role { RoleName = "knowledge_contributor" }
                }
            }
        });
        var vectors = new FakeVectors
        {
            Hits = new[]
            {
                new VectorSearchHit("heading", 42, 12, "run-1", 0.9),
                new VectorSearchHit("content", 42, 12, "run-1", 0.8)
            }
        };
        var source = new FakeSource
        {
            SearchChunks = new Dictionary<string, SemanticSearchChunk>
            {
                ["heading"] = new(
                    "heading", 42, 12, "parent-1", new[] { "Document Control" }, "Document Control", 1, 1, new[] { "c1" }),
                ["content"] = new(
                    "content", 42, 12, "parent-1", new[] { "Document Control" }, "Effective date: September 14, 2026.", 1, 1, new[] { "c2" })
            }
        };
        var handler = new SemanticSearchQueryHandler(
            users,
            new FakeEmbeddings(),
            vectors,
            source,
            new FakeTranslator(),
            new SemanticSearchOptions { CandidateLimit = 2 },
            NullLogger<SemanticSearchQueryHandler>.Instance);

        var result = await handler.Handle(new SemanticSearchQuery(7, 12, "effective date", 1), CancellationToken.None);

        var hit = Assert.Single(result.Data!);
        Assert.Equal("content", hit.ChildChunkId);
    }

    [Fact]
    public async Task Search_uses_translated_query_when_original_score_is_below_threshold()
    {
        var users = CreateAuthorizedUsers();
        var embeddings = new FakeEmbeddings();
        var translator = new FakeTranslator
        {
            Translation = "When are privileged access reviews performed?"
        };
        var vectors = new FakeVectors();
        vectors.SearchResponses.Enqueue(new[]
        {
            new VectorSearchHit("unrelated", 27, 12, "run-1", 0.20)
        });
        vectors.SearchResponses.Enqueue(new[]
        {
            new VectorSearchHit("answer", 28, 12, "run-2", 0.81)
        });
        var source = new FakeSource
        {
            SearchChunks = new Dictionary<string, SemanticSearchChunk>
            {
                ["unrelated"] = new(
                    "unrelated", 27, 12, "parent-1", new[] { "Governance" }, "unrelated text", 1, 1, new[] { "c1" }),
                ["answer"] = new(
                    "answer", 28, 12, "parent-2", new[] { "Privileged Access Reviews" }, "January, April, July, and October.", 3, 3, new[] { "c2" })
            }
        };
        var handler = new SemanticSearchQueryHandler(
            users,
            embeddings,
            vectors,
            source,
            translator,
            new SemanticSearchOptions { TranslationFallbackScoreThreshold = 0.25 },
            NullLogger<SemanticSearchQueryHandler>.Instance);

        var result = await handler.Handle(
            new SemanticSearchQuery(7, 12, "Việc rà soát quyền truy cập đặc quyền được thực hiện khi nào?", 1),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("answer", Assert.Single(result.Data!).ChildChunkId);
        Assert.Equal(1, translator.CallCount);
        Assert.Equal(2, embeddings.CallCount);
        Assert.Equal(2, vectors.SearchCallCount);
        Assert.Equal(
            new[]
            {
                "Việc rà soát quyền truy cập đặc quyền được thực hiện khi nào?",
                "When are privileged access reviews performed?"
            },
            embeddings.Inputs);
    }

    [Fact]
    public async Task Search_merges_translated_hits_and_removes_duplicate_chunks()
    {
        var vectors = new FakeVectors();
        vectors.SearchResponses.Enqueue(new[]
        {
            new VectorSearchHit("shared", 28, 12, "run-1", 0.20)
        });
        vectors.SearchResponses.Enqueue(new[]
        {
            new VectorSearchHit("answer", 28, 12, "run-1", 0.90),
            new VectorSearchHit("shared", 28, 12, "run-1", 0.80)
        });
        var source = new FakeSource
        {
            SearchChunks = new Dictionary<string, SemanticSearchChunk>
            {
                ["shared"] = new(
                    "shared", 28, 12, "parent-1", new[] { "Reviews" }, "shared content", 3, 3, new[] { "c1" }),
                ["answer"] = new(
                    "answer", 28, 12, "parent-1", new[] { "Reviews" }, "answer content", 3, 3, new[] { "c2" })
            }
        };
        var handler = new SemanticSearchQueryHandler(
            CreateAuthorizedUsers(),
            new FakeEmbeddings(),
            vectors,
            source,
            new FakeTranslator { Translation = "translated query" },
            new SemanticSearchOptions { TranslationFallbackScoreThreshold = 0.25 },
            NullLogger<SemanticSearchQueryHandler>.Instance);

        var result = await handler.Handle(
            new SemanticSearchQuery(7, 12, "truy vấn", 3),
            CancellationToken.None);

        Assert.Equal(new[] { "answer", "shared" }, result.Data!.Select(item => item.ChildChunkId));
        Assert.Equal(0.80, result.Data![1].Score);
    }

    [Fact]
    public async Task Search_returns_original_hits_when_translation_fallback_fails()
    {
        var vectors = new FakeVectors
        {
            Hits = new[] { new VectorSearchHit("original", 27, 12, "run-1", 0.20) }
        };
        var source = new FakeSource
        {
            SearchChunks = new Dictionary<string, SemanticSearchChunk>
            {
                ["original"] = new(
                    "original", 27, 12, "parent-1", new[] { "Policy" }, "original result", 1, 1, new[] { "c1" })
            }
        };
        var handler = new SemanticSearchQueryHandler(
            CreateAuthorizedUsers(),
            new FakeEmbeddings(),
            vectors,
            source,
            new FakeTranslator { Failure = new QueryTranslationException("translation unavailable") },
            new SemanticSearchOptions { TranslationFallbackScoreThreshold = 0.25 },
            NullLogger<SemanticSearchQueryHandler>.Instance);

        var result = await handler.Handle(
            new SemanticSearchQuery(7, 12, "truy vấn", 1),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("original", Assert.Single(result.Data!).ChildChunkId);
        Assert.Equal(1, vectors.SearchCallCount);
    }

    private static FakeUsers CreateAuthorizedUsers() => new(new User
    {
        Id = 7,
        Status = "active",
        UserRoles =
        {
            new UserRole
            {
                DepartmentId = 12,
                Role = new Role { RoleName = "knowledge_contributor" }
            }
        }
    });

    private sealed class FakeUsers(User user) : IUserRepository
    {
        public Task<User?> GetUserAsync(string email) => Task.FromResult<User?>(user);
        public Task<User?> GetUserById(int id) => Task.FromResult<User?>(id == user.Id ? user : null);
        public Task<UserAuthorizationState?> GetAuthorizationStateAsync(int id) => Task.FromResult<UserAuthorizationState?>(null);
    }

    private sealed class FakeEmbeddings : IEmbeddingGenerator
    {
        public int CallCount { get; private set; }
        public List<string> Inputs { get; } = new();
        public string Model => "text-embedding-3-small";
        public int Dimensions => 3;
        public Task<IReadOnlyList<EmbeddingVector>> GenerateAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Inputs.AddRange(inputs);
            return Task.FromResult<IReadOnlyList<EmbeddingVector>>(inputs.Select(_ => new EmbeddingVector(new float[] { 1, 2, 3 })).ToArray());
        }
    }

    private sealed class FakeTranslator : IQueryTranslator
    {
        public string Translation { get; init; } = "translated query";
        public QueryTranslationException? Failure { get; init; }
        public int CallCount { get; private set; }

        public Task<string> TranslateToAlternateLanguageAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(Translation);
        }
    }

    private sealed class FakeVectors : IVectorStore
    {
        public IReadOnlyList<VectorSearchHit> Hits { get; init; } = Array.Empty<VectorSearchHit>();
        public IReadOnlyCollection<int> DepartmentIds { get; private set; } = Array.Empty<int>();
        public int RequestedLimit { get; private set; }
        public double? MinimumScore { get; private set; }
        public int EnsureCalls { get; private set; }
        public int SearchCallCount { get; private set; }
        public Queue<IReadOnlyList<VectorSearchHit>> SearchResponses { get; } = new();
        public Task EnsureCollectionAsync(CancellationToken cancellationToken = default) { EnsureCalls++; return Task.CompletedTask; }
        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ActivateRunAsync(string chunkingRunId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteOtherRunsAsync(int documentId, string activeChunkingRunId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(IReadOnlyList<float> queryVector, IReadOnlyCollection<int> departmentIds, int limit, double? minimumScore = null, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            DepartmentIds = departmentIds;
            RequestedLimit = limit;
            MinimumScore = minimumScore;
            return Task.FromResult(
                SearchResponses.Count > 0
                    ? SearchResponses.Dequeue()
                    : Hits);
        }
    }

    private sealed class FakeSource : IEmbeddingIndexSource
    {
        public IReadOnlyDictionary<string, SemanticSearchChunk> SearchChunks { get; init; } = new Dictionary<string, SemanticSearchChunk>();
        public Task<IReadOnlyList<EmbeddingSourceChunk>> GetChunksForRunAsync(int documentId, string chunkingRunId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EmbeddingSourceChunk>>(Array.Empty<EmbeddingSourceChunk>());
        public Task<IReadOnlyDictionary<string, SemanticSearchChunk>> GetChunksByIdsAsync(IReadOnlyCollection<string> childChunkIds, int departmentId, CancellationToken cancellationToken = default) => Task.FromResult(SearchChunks);
    }
}
