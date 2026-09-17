using System.Globalization;
using System.Text.Json;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Validation;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Chunking;

// Coordinates one deterministic OCR-to-chunks lifecycle.
public sealed class DocumentChunkingOrchestrator : IDocumentChunkingOrchestrator
{
    private readonly IDocumentRepository _documents;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDocumentNormalizer _normalizer;
    private readonly IHeadingDetector _headingDetector;
    private readonly ISectionBuilder _sectionBuilder;
    private readonly IParentChunkBuilder _parentBuilder;
    private readonly IChildChunkBuilder _childBuilder;
    private readonly ITokenCounter _tokenCounter;
    private readonly ILogger<DocumentChunkingOrchestrator> _logger;
    private readonly IChunkingResultValidator _validator;
    private readonly IChunkingExecutionLock _executionLock;
    private readonly ChunkingOptions _options;

    public DocumentChunkingOrchestrator(
        IDocumentRepository documents,
        IUnitOfWork unitOfWork,
        IDocumentNormalizer normalizer,
        IHeadingDetector headingDetector,
        ISectionBuilder sectionBuilder,
        IParentChunkBuilder parentBuilder,
        IChildChunkBuilder childBuilder,
        ITokenCounter tokenCounter,
        ILogger<DocumentChunkingOrchestrator> logger,
        IChunkingResultValidator validator,
        IChunkingExecutionLock executionLock,
        ChunkingOptions options)
    {
        _documents = documents;
        _unitOfWork = unitOfWork;
        _normalizer = normalizer;
        _headingDetector = headingDetector;
        _sectionBuilder = sectionBuilder;
        _parentBuilder = parentBuilder;
        _childBuilder = childBuilder;
        _tokenCounter = tokenCounter;
        _logger = logger;
        _validator = validator;
        _executionLock = executionLock;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task ExecuteAsync(int documentId, CancellationToken cancellationToken = default)
    {
        if (documentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(documentId));

        await using var executionLock = await _executionLock.AcquireAsync(documentId, cancellationToken);
        RunIdentity? identity = null;
        var reactivatingCompletedRun = false;

        try
        {
            var document = await _documents.GetByIdWithOcrResultAsync(documentId, cancellationToken)
                ?? throw new InvalidOperationException($"Document {documentId} was not found.");
            var json = document.OcrResult?.StructuredDocumentJson;
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException($"Document {documentId} has no structured OCR result.");

            var source = JsonSerializer.Deserialize<StructuredDocumentDto>(json)
                ?? throw new InvalidOperationException($"Structured OCR JSON for document {documentId} is invalid.");
            if (!string.Equals(source.DocumentId, documentId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidOperationException($"Structured OCR document id does not match DocumentId {documentId}.");
            if (string.IsNullOrWhiteSpace(document.Title))
                throw new InvalidOperationException($"Document {documentId} has no valid title.");

            var options = _options;
            var normalized = _normalizer.Normalize(source, document.Title);
            var sourceContractValidation = StructuredDocumentContractValidator.Validate(source, options);
            if (!sourceContractValidation.IsValid)
            {
                var details = JsonSerializer.Serialize(new
                {
                    code = "structured_document_validation_failed",
                    documentId,
                    errors = sourceContractValidation.Errors
                });
                throw new InvalidOperationException(details);
            }
            var configurationJson = ChunkingRunIdentity.SerializeConfiguration(options);
            identity = new RunIdentity(
                documentId,
                normalized.SourceContentHash,
                normalized.SourceSchemaVersion,
                options.ChunkerVersion,
                configurationJson);
            var run = await FindRunAsync(identity, cancellationToken);

            if (run?.Status == ChunkingRunStatuses.Completed)
            {
                if (run.IsActive)
                    return;

                reactivatingCompletedRun = true;
                await ActivateCompletedRunAsync(run, documentId, cancellationToken);
                reactivatingCompletedRun = false;
                _logger.LogInformation(
                    "Reactivated completed ChunkingRunId {ChunkingRunId} for DocumentId {DocumentId}.",
                    run.Id,
                    documentId);
                return;
            }

            if (run is null)
            {
                run = new ChunkingRun
                {
                    Id = ChunkingRunIdentity.CreateId(
                        documentId,
                        identity.SourceContentHash,
                        identity.SourceSchemaVersion,
                        identity.ChunkerVersion,
                        identity.ConfigurationJson),
                    DocumentId = documentId,
                    DepartmentId = document.DepartmentId,
                    SourceContentHash = identity.SourceContentHash,
                    SourceSchemaVersion = identity.SourceSchemaVersion,
                    ChunkerVersion = identity.ChunkerVersion,
                    ConfigurationJson = identity.ConfigurationJson,
                    Status = ChunkingRunStatuses.Queued,
                    UpdatedAt = DateTime.UtcNow
                };
                await _unitOfWork.ChunkRepo.AddAsync(run, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            run.Status = ChunkingRunStatuses.Processing;
            run.IsActive = false;
            run.StartedAt ??= DateTime.UtcNow;
            run.CompletedAt = null;
            run.ErrorMessage = null;
            run.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.ChunkRepo.UpdateAsync(run, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var headings = _headingDetector.Detect(normalized);
            var sectioned = _sectionBuilder.Build(normalized, headings);
            var parents = _parentBuilder.Build(sectioned, options, _tokenCounter);
            var children = _childBuilder.Build(parents, normalized.Title, options, _tokenCounter);
            var result = new ChunkingResult(
                documentId, normalized.SourceSchemaVersion, normalized.SourceContentHash,
                options.ChunkerVersion, parents, children, sectioned.Warnings);
            var sourceInventory = new ChunkingSourceInventory(
                documentId,
                source.Pages.SelectMany(page => page.Components).Select(component => component.ComponentId),
                normalized.Blocks.Select(block => new ChunkingSourceBlock(
                    block.BlockId,
                    block.Text,
                    _tokenCounter.Count(block.Text),
                    block.ComponentIds)),
                normalized.Notices);
            var validation = _validator.Validate(
                result,
                sourceInventory,
                options,
                _tokenCounter);
            validation = new ChunkingValidationResult(
                validation.Errors.Concat(sourceContractValidation.Errors),
                validation.Warnings.Concat(sourceContractValidation.Warnings),
                sourceCoverage: validation.SourceCoverage,
                sourceFidelity: sourceContractValidation.SourceFidelity,
                parentCoverage: validation.ParentCoverage,
                childCoverage: validation.ChildCoverage);
            if (!validation.IsValid)
            {
                var details = JsonSerializer.Serialize(new
                {
                    code = "chunking_validation_failed",
                    documentId,
                    errors = validation.Errors
                });
                throw new InvalidOperationException(details);
            }

            run.ValidationWarningsJson = JsonSerializer.Serialize(validation.Warnings);
            run.NormalizationNoticesJson = JsonSerializer.Serialize(normalized.Notices);
            run.SourceCoverage = validation.SourceCoverage;

            await _unitOfWork.BeginTransactionAsync();

            // Remove any graph left by an earlier failed attempt before adding
            // deterministic entities with the same IDs. This save is protected
            // by the transaction, so rollback restores the previous graph.
            run.ParentChunks.Clear();
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _unitOfWork.ClearTrackedChanges();
            run = await FindRunAsync(identity, cancellationToken)
                ?? throw new InvalidOperationException($"Chunking run for document {documentId} disappeared.");

            var parentEntities = result.Parents.Select(parent => new ParentChunk
            {
                Id = parent.Id, ChunkingRunId = run.Id, DocumentId = documentId,
                DepartmentId = document.DepartmentId, Ordinal = parent.Ordinal, Title = parent.Title,
                SectionPath = parent.SectionPath.ToList(), Content = parent.Content,
                TokenCount = parent.TokenCount, PageFrom = parent.PageFrom, PageTo = parent.PageTo,
                ComponentIds = parent.ComponentIds.ToList(), ContentHash = parent.ContentHash,
                IsAtomic = parent.IsAtomic
            }).ToList();
            foreach (var child in result.Children)
            {
                var parent = parentEntities.Single(p => p.Id == child.ParentChunkId);
                parent.ChildChunks.Add(new ChildChunk
                {
                    Id = child.Id, ParentChunkId = parent.Id, ChunkingRunId = run.Id,
                    DocumentId = documentId, DepartmentId = document.DepartmentId, Ordinal = child.Ordinal,
                    SectionPath = child.SectionPath.ToList(), RawContent = child.RawContent,
                    ContextualizedContent = child.ContextualizedContent, TokenCount = child.TokenCount,
                    PageFrom = child.PageFrom, PageTo = child.PageTo, ComponentIds = child.ComponentIds.ToList(),
                    ContentHash = child.ContentHash, IsAtomic = child.IsAtomic
                });
            }
            foreach (var parent in parentEntities) run.ParentChunks.Add(parent);
            run.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var previous = await _unitOfWork.ChunkRepo.GetActiveRunAsync(documentId, cancellationToken);
            if (previous is not null && previous.Id != run.Id)
            {
                previous.IsActive = false;
                previous.UpdatedAt = DateTime.UtcNow;
                await _unitOfWork.ChunkRepo.UpdateAsync(previous, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            run.Status = ChunkingRunStatuses.Completed;
            run.IsActive = true;
            run.CompletedAt = DateTime.UtcNow;
            run.ErrorMessage = null;
            run.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.ChunkRepo.UpdateAsync(run, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await _unitOfWork.CommitTransactionAsync();
            _logger.LogInformation("Chunking completed for DocumentId {DocumentId}, ChunkingRunId {ChunkingRunId}, version {ChunkerVersion}.", documentId, run.Id, options.ChunkerVersion);
        }
        catch (OperationCanceledException exception)
        {
            await RecoverFailedRunAsync(
                identity,
                "Chunking execution was cancelled.",
                exception,
                reactivatingCompletedRun);
            _logger.LogWarning(exception, "Chunking was cancelled for DocumentId {DocumentId}.", documentId);
            throw;
        }
        catch (Exception ex)
        {
            if (await RecoverFailedRunAsync(
                    identity,
                    GetSafeRootCauseMessage(ex),
                    ex,
                    reactivatingCompletedRun))
            {
                _logger.LogWarning(
                    ex,
                    "Chunking commit outcome was ambiguous, but the completed active run was confirmed for DocumentId {DocumentId}.",
                    documentId);
                return;
            }

            var failedRunId = identity is null
                ? null
                : ChunkingRunIdentity.CreateId(
                    identity.DocumentId,
                    identity.SourceContentHash,
                    identity.SourceSchemaVersion,
                    identity.ChunkerVersion,
                    identity.ConfigurationJson);
            _logger.LogError(ex, "Chunking failed for DocumentId {DocumentId}, ChunkingRunId {ChunkingRunId}.", documentId, failedRunId);
            throw;
        }
    }

    private static string GetSafeRootCauseMessage(Exception exception)
    {
        var rootCause = exception.GetBaseException();
        return string.IsNullOrWhiteSpace(rootCause.Message)
            ? exception.Message
            : rootCause.Message;
    }

    private Task<ChunkingRun?> FindRunAsync(
        RunIdentity identity,
        CancellationToken cancellationToken) =>
        _unitOfWork.ChunkRepo.GetByVersionAsync(
            identity.DocumentId,
            identity.SourceContentHash,
            identity.SourceSchemaVersion,
            identity.ChunkerVersion,
            identity.ConfigurationJson,
            cancellationToken);

    private async Task<bool> RecoverFailedRunAsync(
        RunIdentity? identity,
        string errorMessage,
        Exception originalException,
        bool preserveCompletedRun = false)
    {
        try
        {
            await _unitOfWork.RollbackTransactionAsync();
        }
        catch (Exception rollbackException)
        {
            _logger.LogError(
                rollbackException,
                "Rollback failed while recovering chunking for DocumentId {DocumentId}.",
                identity?.DocumentId);
        }

        _unitOfWork.ClearTrackedChanges();
        if (identity is null)
            return false;

        try
        {
            var persisted = await FindRunAsync(identity, CancellationToken.None);
            if (persisted is null)
                return false;

            // A client-side commit error can happen after PostgreSQL committed.
            // Confirming the durable state makes the operation idempotent.
            if (persisted.Status == ChunkingRunStatuses.Completed && persisted.IsActive)
                return true;
            if (preserveCompletedRun && persisted.Status == ChunkingRunStatuses.Completed)
                return false;

            persisted.Status = ChunkingRunStatuses.Failed;
            persisted.IsActive = false;
            persisted.CompletedAt = null;
            persisted.ErrorMessage = errorMessage;
            persisted.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.ChunkRepo.UpdateAsync(persisted, CancellationToken.None);
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception recoveryException)
        {
            _logger.LogError(
                recoveryException,
                "Failed to persist terminal chunking state for DocumentId {DocumentId}. Original error: {OriginalError}",
                identity.DocumentId,
                originalException.Message);
        }

        return false;
    }

    private async Task ActivateCompletedRunAsync(
        ChunkingRun run,
        int documentId,
        CancellationToken cancellationToken)
    {
        await _unitOfWork.BeginTransactionAsync();
        var previous = await _unitOfWork.ChunkRepo.GetActiveRunAsync(documentId, cancellationToken);
        if (previous is not null && previous.Id != run.Id)
        {
            previous.IsActive = false;
            previous.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.ChunkRepo.UpdateAsync(previous, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        run.IsActive = true;
        run.UpdatedAt = DateTime.UtcNow;
        await _unitOfWork.ChunkRepo.UpdateAsync(run, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _unitOfWork.CommitTransactionAsync();
    }

    private sealed record RunIdentity(
        int DocumentId,
        string SourceContentHash,
        string SourceSchemaVersion,
        string ChunkerVersion,
        string ConfigurationJson);
}
