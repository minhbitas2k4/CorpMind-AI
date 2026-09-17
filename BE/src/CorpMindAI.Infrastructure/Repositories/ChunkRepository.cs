using System.Text.Json;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CorpMindAI.Infrastructure.Repositories;

public sealed class ChunkRepository : IChunkRepository
{
    private readonly CorpMindDbContext _context;

    public ChunkRepository(CorpMindDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(
        ChunkingRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateGraph(run);

        if (await _context.ChunkingRuns.AnyAsync(
                existing => existing.Id == run.Id,
                cancellationToken))
        {
            throw new InvalidOperationException($"Chunking run '{run.Id}' already exists.");
        }

        if (run.IsActive && await _context.ChunkingRuns.AnyAsync(
                existing => existing.DocumentId == run.DocumentId &&
                            existing.IsActive &&
                            existing.Id != run.Id,
                cancellationToken))
        {
            throw new InvalidOperationException(
                $"Document {run.DocumentId} already has an active chunking run.");
        }

        await _context.ChunkingRuns.AddAsync(run, cancellationToken);
    }

    public Task<ChunkingRun?> GetActiveRunAsync(
        int documentId,
        CancellationToken cancellationToken = default) =>
        _context.ChunkingRuns
            .Include(run => run.ParentChunks)
            .ThenInclude(parent => parent.ChildChunks)
            .SingleOrDefaultAsync(
                run => run.DocumentId == documentId && run.IsActive,
                cancellationToken);

    public Task<ChunkingRun?> GetByVersionAsync(
        int documentId,
        string sourceContentHash,
        string sourceSchemaVersion,
        string chunkerVersion,
        string configurationJson,
        CancellationToken cancellationToken = default) =>
        _context.ChunkingRuns
            .Include(run => run.ParentChunks)
            .ThenInclude(parent => parent.ChildChunks)
            .SingleOrDefaultAsync(
                run => run.DocumentId == documentId &&
                       run.SourceContentHash == sourceContentHash &&
                       run.SourceSchemaVersion == sourceSchemaVersion &&
                       run.ChunkerVersion == chunkerVersion &&
                       run.ConfigurationJson == configurationJson,
                cancellationToken);

    public Task UpdateAsync(ChunkingRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateGraph(run);
        _context.ChunkingRuns.Update(run);
        return Task.CompletedTask;
    }

    private static void ValidateGraph(ChunkingRun run)
    {
        if (run.DocumentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(run.DocumentId));
        if (run.DepartmentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(run.DepartmentId));
        if (string.IsNullOrWhiteSpace(run.Id) ||
            string.IsNullOrWhiteSpace(run.SourceContentHash) ||
            string.IsNullOrWhiteSpace(run.SourceSchemaVersion) ||
            string.IsNullOrWhiteSpace(run.ChunkerVersion))
        {
            throw new ArgumentException("Chunking run identity and version fields are required.", nameof(run));
        }

        var parents = run.ParentChunks.ToArray();
        var parentIds = parents.Select(parent => parent.Id).ToHashSet(StringComparer.Ordinal);
        if (parentIds.Count != parents.Length)
            throw new ArgumentException("Parent IDs must be unique.", nameof(run));

        if (parents.Any(parent =>
                parent.DocumentId != run.DocumentId ||
                parent.ChunkingRunId != run.Id ||
                parent.DepartmentId != run.DepartmentId))
        {
            throw new ArgumentException(
                "Every parent must belong to the run, document and department.",
                nameof(run));
        }

        var children = parents.SelectMany(parent => parent.ChildChunks).ToArray();
        var childIds = children.Select(child => child.Id).ToHashSet(StringComparer.Ordinal);
        if (childIds.Count != children.Length)
            throw new ArgumentException("Child IDs must be unique.", nameof(run));

        if (children.Any(child =>
                child.DocumentId != run.DocumentId ||
                child.ChunkingRunId != run.Id ||
                child.DepartmentId != run.DepartmentId ||
                !parentIds.Contains(child.ParentChunkId)))
        {
            throw new ArgumentException(
                "Every child must belong to the run, document, department and a parent.",
                nameof(run));
        }

        if (!IsValidStatus(run.Status))
            throw new ArgumentException($"Unsupported chunking run status '{run.Status}'.", nameof(run));

        if (string.IsNullOrWhiteSpace(run.ConfigurationJson))
            throw new ArgumentException("ConfigurationJson is required.", nameof(run));

        try
        {
            using var _ = JsonDocument.Parse(run.ConfigurationJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("ConfigurationJson must contain valid JSON.", nameof(run), exception);
        }
    }

    private static bool IsValidStatus(string status) =>
        status is ChunkingRunStatuses.NotStarted or
            ChunkingRunStatuses.Queued or
            ChunkingRunStatuses.Processing or
            ChunkingRunStatuses.Completed or
            ChunkingRunStatuses.Failed;
}
