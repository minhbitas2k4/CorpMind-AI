using System.Security.Cryptography;
using System.Text;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Interfaces;

namespace CorpMindAI.Application.Chunking.Validation;

// <summary>Pure, deterministic validation of an in-memory chunking result.</summary>
public sealed class ChunkingResultValidator : IChunkingResultValidator
{
    private const int MaxPersistedParentTitleCharacters = 255;

    public ChunkingValidationResult Validate(ChunkingResult? result) =>
        ValidateCore(result, null, 1.0, null, null);

    public ChunkingValidationResult Validate(
        ChunkingResult? result,
        ChunkingSourceInventory sourceInventory,
        double minimumSourceCoverage)
    {
        if (sourceInventory is null)
            return Invalid(new("source_inventory.required", "ChunkingSourceInventory is required."));
        if (double.IsNaN(minimumSourceCoverage) ||
            double.IsInfinity(minimumSourceCoverage) ||
            minimumSourceCoverage is <= 0 or > 1)
        {
            return Invalid(new(
                "source_coverage.threshold_invalid",
                "Minimum source coverage must be greater than zero and no greater than one."));
        }

        return ValidateCore(result, sourceInventory, minimumSourceCoverage, null, null);
    }

    public ChunkingValidationResult Validate(
        ChunkingResult? result,
        ChunkingSourceInventory sourceInventory,
        ChunkingOptions options,
        ITokenCounter tokenCounter)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        options.Validate();
        return ValidateCore(
            result,
            sourceInventory,
            options.MinimumSourceCoverage,
            options,
            tokenCounter);
    }

    private static ChunkingValidationResult ValidateCore(
        ChunkingResult? result,
        ChunkingSourceInventory? sourceInventory,
        double minimumSourceCoverage,
        ChunkingOptions? options,
        ITokenCounter? tokenCounter)
    {
        if (result is null)
            return Invalid(new("result.required", "ChunkingResult is required."));

        var errors = new List<ChunkingValidationError>();
        var warnings = new List<string>();
        var documentId = result.DocumentId;
        if (documentId <= 0)
            Add(errors, "document.invalid", "DocumentId must be positive.", documentId);

        var parents = result.Parents;
        var children = result.Children;
        ValidateIdsAndOrdinals(parents, children, documentId, errors);

        var parentById = parents
            .Where(parent => !string.IsNullOrWhiteSpace(parent.Id))
            .GroupBy(parent => parent.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var childrenByParent = children
            .Where(child => !string.IsNullOrWhiteSpace(child.ParentChunkId))
            .GroupBy(child => child.ParentChunkId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var parent in parents)
        {
            if (string.IsNullOrWhiteSpace(parent.Id))
                Add(errors, "parent.id_required", "Parent ID is required.", documentId);
            if (parent.DocumentId != documentId)
                Add(errors, "parent.document_mismatch", "Parent DocumentId does not match the result.", documentId, parent.Id);
            if (parent.Title is { Length: > MaxPersistedParentTitleCharacters })
            {
                Add(
                    errors,
                    "PARENT_TITLE_TOO_LONG",
                    $"Parent title length {parent.Title.Length} exceeds the maximum of {MaxPersistedParentTitleCharacters} characters.",
                    documentId,
                    parent.Id);
            }
            ValidateCommon(parent.Content, parent.TokenCount, parent.PageFrom, parent.PageTo,
                parent.ComponentIds, parent.SectionPath, parent.ContentHash, documentId,
                errors, parent.Id, null);
            if (options is not null && tokenCounter is not null)
            {
                ValidateDerivedValues(
                    parent.Content,
                    parent.TokenCount,
                    parent.ContentHash,
                    options.ParentMaxTokens,
                    parent.IsAtomic,
                    tokenCounter,
                    documentId,
                    errors,
                    parent.Id,
                    null);
            }
        }

        foreach (var child in children)
        {
            if (string.IsNullOrWhiteSpace(child.Id))
                Add(errors, "child.id_required", "Child ID is required.", documentId, null, child.Id);
            if (child.DocumentId != documentId)
                Add(errors, "child.document_mismatch", "Child DocumentId does not match the result.", documentId, null, child.Id);
            ValidateCommon(child.RawContent, child.TokenCount, child.PageFrom, child.PageTo,
                child.ComponentIds, child.SectionPath, child.ContentHash, documentId,
                errors, null, child.Id);
            if (string.IsNullOrWhiteSpace(child.ContextualizedContent))
                Add(errors, "child.contextualized_content.required", "ContextualizedContent is required.", documentId, null, child.Id);
            if (options is not null && tokenCounter is not null)
            {
                ValidateDerivedValues(
                    child.ContextualizedContent,
                    child.TokenCount,
                    child.ContentHash,
                    options.ChildMaxTokens,
                    child.IsAtomic,
                    tokenCounter,
                    documentId,
                    errors,
                    null,
                    child.Id,
                    hashContent: child.RawContent);
            }

            if (string.IsNullOrWhiteSpace(child.ParentChunkId))
            {
                Add(errors, "child.parent_required", "Child ParentChunkId is required.", documentId, null, child.Id);
                continue;
            }

            if (!parentById.TryGetValue(child.ParentChunkId, out var parent))
            {
                Add(errors, "child.orphan", "Child does not reference a parent in the result.", documentId, child.ParentChunkId, child.Id);
                continue;
            }

            if (child.PageFrom < parent.PageFrom || child.PageTo > parent.PageTo)
                Add(errors, "child.page_outside_parent", "Child page range must be within its parent.", documentId, parent.Id, child.Id);
            if (!child.SectionPath.SequenceEqual(parent.SectionPath, StringComparer.Ordinal))
                Add(errors, "child.section_mismatch", "Child SectionPath must match its parent.", documentId, parent.Id, child.Id);
            var parentComponents = parent.ComponentIds.ToHashSet(StringComparer.Ordinal);
            if (child.ComponentIds.Any(componentId => !parentComponents.Contains(componentId)))
                Add(errors, "child.component_outside_parent", "Child ComponentIds must be contained by its parent.", documentId, parent.Id, child.Id);

            if (!ContainsNormalizedContent(parent.Content, child.RawContent))
            {
                Add(
                    errors,
                    "child.content_outside_parent",
                    "Child RawContent must be traceable to its parent content.",
                    documentId,
                    parent.Id,
                    child.Id);
            }
        }

        foreach (var parent in parents)
        {
            if (!childrenByParent.TryGetValue(parent.Id, out var parentChildren) || parentChildren.Length == 0)
            {
                Add(
                    errors,
                    "parent.child_required",
                    "Every parent must contain at least one child chunk.",
                    documentId,
                    parent.Id);
                continue;
            }

            var parentTokens = Tokenize(NormalizeWhitespace(parent.Content));
            var childTokens = parentChildren
                .SelectMany(child => Tokenize(NormalizeWhitespace(child.RawContent)))
                .ToArray();
            var parentChildCoverage = CalculateMultisetCoverage(parentTokens, childTokens);
            if (parentChildCoverage + 1e-12 < 1.0)
            {
                Add(
                    errors,
                    "child.coverage_incomplete",
                    $"Child chunks cover {parentChildCoverage:F6} of the parent content.",
                    documentId,
                    parent.Id);
            }
        }

        var parentTokensForChildCoverage = parents
            .SelectMany(parent => Tokenize(NormalizeWhitespace(parent.Content)))
            .ToArray();
        var childTokensForCoverage = children
            .SelectMany(child => Tokenize(NormalizeWhitespace(child.RawContent)))
            .ToArray();
        var childCoverage = CalculateMultisetCoverage(
            parentTokensForChildCoverage,
            childTokensForCoverage);
        warnings.Add(
            $"parent_to_child_coverage.value: {childCoverage:F6}; threshold: 1.000000; " +
            $"parent_tokens: {parentTokensForChildCoverage.Length}; child_tokens: {childTokensForCoverage.Length}.");
        if (childCoverage + 1e-12 < 1.0)
        {
            Add(
                errors,
                "parent_to_child_coverage.below_threshold",
                $"Parent-to-child coverage {childCoverage:F6} is below the required threshold 1.000000.",
                documentId);
        }

        AddDuplicateHashWarnings(parents.Select(parent => parent.ContentHash), warnings, "parent");
        AddDuplicateHashWarnings(children.Select(child => child.ContentHash), warnings, "child");

        double? sourceCoverage = null;
        if (sourceInventory is null)
        {
            warnings.Add("source_coverage.not_evaluable: No normalized source inventory was supplied.");
        }
        else
        {
            sourceCoverage = ValidateSourceCoverage(
                result,
                sourceInventory,
                minimumSourceCoverage,
                errors,
                warnings,
                tokenCounter);
        }

        if (sourceCoverage.HasValue)
        {
            warnings.Add(
                $"source_to_parent_coverage.value: {sourceCoverage.Value:F6}; " +
                $"threshold: {minimumSourceCoverage:F6}.");
        }

        return new ChunkingValidationResult(
            errors,
            warnings,
            sourceCoverage: sourceCoverage,
            parentCoverage: sourceCoverage,
            childCoverage: childCoverage);
    }

    private static double? ValidateSourceCoverage(
        ChunkingResult result,
        ChunkingSourceInventory inventory,
        double minimumCoverage,
        ICollection<ChunkingValidationError> errors,
        ICollection<string> warnings,
        ITokenCounter? tokenCounter)
    {
        var documentId = result.DocumentId;
        if (inventory.DocumentId != documentId)
        {
            Add(errors, "source_inventory.document_mismatch",
                "Source inventory DocumentId does not match the chunking result.", documentId);
            return null;
        }

        if (inventory.RetainedBlocks.Count == 0)
        {
            Add(
                errors,
                "source.no_searchable_content",
                "No searchable source blocks remain after normalization.",
                documentId);
        }

        AddDuplicateValueErrors(
            inventory.SourceComponentIds,
            "source.component_duplicate",
            "source component",
            documentId,
            errors);

        if (tokenCounter is not null)
        {
            foreach (var block in inventory.RetainedBlocks)
            {
                var actualTokenCount = tokenCounter.Count(block.Content);
                if (actualTokenCount != block.TokenCount)
                {
                    Add(errors, "source.token_count_mismatch",
                        $"Normalized block '{block.BlockId}' token count is {block.TokenCount}, expected {actualTokenCount}.",
                        documentId);
                }
            }
        }
        AddDuplicateValueErrors(
            inventory.RetainedBlocks.Select(block => block.BlockId),
            "source.block_duplicate",
            "normalized block",
            documentId,
            errors);
        AddDuplicateValueErrors(
            inventory.NormalizationNotices.Select(notice => notice.ComponentId),
            "normalization_notice.duplicate",
            "normalization notice component",
            documentId,
            errors);

        var sourceComponents = inventory.SourceComponentIds
            .Where(componentId => !string.IsNullOrWhiteSpace(componentId))
            .ToHashSet(StringComparer.Ordinal);
        if (sourceComponents.Count != inventory.SourceComponentIds.Count)
            Add(errors, "source.component_id_invalid", "Source component IDs must be non-empty.", documentId);

        var retainedComponents = inventory.RetainedBlocks
            .SelectMany(block => block.ComponentIds)
            .ToHashSet(StringComparer.Ordinal);
        var excludedComponents = inventory.NormalizationNotices
            .Select(notice => notice.ComponentId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var componentId in retainedComponents.Where(id => !sourceComponents.Contains(id)))
            Add(errors, "source.block_component_unknown",
                $"Normalized block references unknown source component '{componentId}'.", documentId);
        foreach (var componentId in excludedComponents.Where(id => !sourceComponents.Contains(id)))
            Add(errors, "normalization_notice.component_unknown",
                $"Normalization notice references unknown source component '{componentId}'.", documentId);
        foreach (var componentId in retainedComponents.Intersect(excludedComponents, StringComparer.Ordinal))
            Add(errors, "source.component_retained_and_excluded",
                $"Source component '{componentId}' is both retained and excluded.", documentId);
        foreach (var componentId in sourceComponents
                     .Except(retainedComponents, StringComparer.Ordinal)
                     .Except(excludedComponents, StringComparer.Ordinal))
        {
            Add(errors, "source.component_unaccounted",
                $"Source component '{componentId}' was neither retained nor recorded by a normalization notice.",
                documentId);
        }

        var parentComponents = result.Parents
            .SelectMany(parent => parent.ComponentIds)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var componentId in parentComponents.Where(id => !retainedComponents.Contains(id)))
        {
            var code = excludedComponents.Contains(componentId)
                ? "parent.component_excluded"
                : "parent.component_unknown";
            Add(errors, code,
                $"Parent chunk references component '{componentId}' that is not retained source content.",
                documentId);
        }
        foreach (var componentId in retainedComponents.Except(parentComponents, StringComparer.Ordinal))
            Add(errors, "source.component_uncovered",
                $"Retained source component '{componentId}' does not appear in any parent chunk.", documentId);

        var sourceTokenCount = inventory.TotalRetainedTokens;
        var parentTokenCount = result.Parents.Sum(parent => (long)Math.Max(0, parent.TokenCount));
        var tokenCoverage = sourceTokenCount == 0
            ? 1.0
            : Math.Min(1.0, (double)parentTokenCount / sourceTokenCount);
        var sourceLexicalUnits = inventory.RetainedBlocks
            .SelectMany(block => Tokenize(block.Content))
            .ToArray();
        var parentLexicalUnits = result.Parents
            .OrderBy(parent => parent.Ordinal)
            .SelectMany(parent => Tokenize(parent.Content))
            .ToArray();
        var lexicalCoverage = CalculateMultisetCoverage(sourceLexicalUnits, parentLexicalUnits);
        var actualCoverage = Math.Min(tokenCoverage, lexicalCoverage);

        warnings.Add(
            $"source_coverage.value: {actualCoverage:F6}; threshold: {minimumCoverage:F6}; " +
            $"retained_tokens: {sourceTokenCount}; parent_tokens: {parentTokenCount}.");
        if (actualCoverage + 1e-12 < minimumCoverage)
        {
            Add(errors, "source_coverage.below_threshold",
                $"Source coverage {actualCoverage:F6} is below the configured threshold {minimumCoverage:F6}.",
                documentId);
        }

        return actualCoverage;
    }

    private static void ValidateDerivedValues(
        string tokenizedContent,
        int tokenCount,
        string contentHash,
        int hardLimit,
        bool isAtomic,
        ITokenCounter tokenCounter,
        int documentId,
        ICollection<ChunkingValidationError> errors,
        string? parentId,
        string? childId,
        string? hashContent = null)
    {
        var actualTokenCount = tokenCounter.Count(tokenizedContent);
        if (actualTokenCount < 0)
            Add(errors, "token_count.counter_invalid", "Token counter returned a negative count.", documentId, parentId, childId);
        else if (actualTokenCount != tokenCount)
            Add(errors, "token_count.mismatch", $"Token count is {tokenCount}, expected {actualTokenCount}.", documentId, parentId, childId);

        if (!isAtomic && actualTokenCount > hardLimit)
            Add(errors, "token_count.hard_limit_exceeded", $"Token count {actualTokenCount} exceeds hard limit {hardLimit}.", documentId, parentId, childId);

        var actualHash = ComputeHash(hashContent ?? tokenizedContent);
        if (!string.Equals(actualHash, contentHash, StringComparison.Ordinal))
            Add(errors, "content_hash.mismatch", "ContentHash does not match chunk content.", documentId, parentId, childId);
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static IReadOnlyList<string> Tokenize(string content) =>
        content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool ContainsNormalizedContent(string parentContent, string childContent)
    {
        var normalizedParent = NormalizeWhitespace(parentContent);
        var normalizedChild = NormalizeWhitespace(childContent);
        return normalizedChild.Length > 0 &&
               normalizedParent.Contains(normalizedChild, StringComparison.Ordinal);
    }

    private static string NormalizeWhitespace(string content) =>
        string.Join(
            " ",
            content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static double CalculateMultisetCoverage(
        IReadOnlyList<string> source,
        IReadOnlyList<string> chunks)
    {
        if (source.Count == 0)
            return 1.0;

        var available = chunks
            .GroupBy(token => token, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var covered = 0;
        foreach (var token in source)
        {
            if (!available.TryGetValue(token, out var count) || count == 0)
                continue;

            covered++;
            available[token] = count - 1;
        }

        return (double)covered / source.Count;
    }

    private static void AddDuplicateValueErrors(
        IEnumerable<string> values,
        string code,
        string valueKind,
        int documentId,
        ICollection<ChunkingValidationError> errors)
    {
        foreach (var duplicate in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .GroupBy(value => value, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            Add(errors, code, $"Duplicate {valueKind} ID '{duplicate.Key}'.", documentId);
        }
    }

    private static void ValidateIdsAndOrdinals(
        IReadOnlyList<ParentChunkResult> parents,
        IReadOnlyList<ChildChunkResult> children,
        int documentId,
        ICollection<ChunkingValidationError> errors)
    {
        AddDuplicateIdErrors(parents.Select(parent => parent.Id), documentId, errors, "parent");
        AddDuplicateIdErrors(children.Select(child => child.Id), documentId, errors, "child");
        AddDuplicateOrdinalErrors(parents.Select(parent => (parent.Ordinal, ParentId: (string?)null)), documentId, errors, null);
        foreach (var group in children.GroupBy(child => child.ParentChunkId, StringComparer.Ordinal))
            AddDuplicateOrdinalErrors(group.Select(child => (child.Ordinal, ParentId: (string?)group.Key)), documentId, errors, group.Key);
    }

    private static void ValidateCommon(
        string content,
        int tokenCount,
        int pageFrom,
        int pageTo,
        IEnumerable<string> componentIds,
        IEnumerable<string> sectionPath,
        string contentHash,
        int documentId,
        ICollection<ChunkingValidationError> errors,
        string? parentId,
        string? childId)
    {
        if (string.IsNullOrWhiteSpace(content)) Add(errors, "content.required", "Chunk content is required.", documentId, parentId, childId);
        if (tokenCount < 0) Add(errors, "token_count.invalid", "Token count must not be negative.", documentId, parentId, childId);
        if (pageFrom <= 0 || pageTo <= 0 || pageFrom > pageTo)
            Add(errors, "page_range.invalid", "Page range must be positive and ordered.", documentId, parentId, childId);
        if (componentIds is null || !componentIds.Any() || componentIds.Any(string.IsNullOrWhiteSpace))
            Add(errors, "component_ids.invalid", "ComponentIds must contain non-empty values.", documentId, parentId, childId);
        if (sectionPath is null || sectionPath.Any(string.IsNullOrWhiteSpace))
            Add(errors, "section_path.invalid", "SectionPath must not contain empty values.", documentId, parentId, childId);
        if (string.IsNullOrWhiteSpace(contentHash)) Add(errors, "content_hash.required", "ContentHash is required.", documentId, parentId, childId);
    }

    private static void AddDuplicateIdErrors(IEnumerable<string> ids, int documentId, ICollection<ChunkingValidationError> errors, string kind)
    {
        foreach (var group in ids.Where(id => !string.IsNullOrWhiteSpace(id)).GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Add(errors, $"{kind}.id_duplicate", $"Duplicate {kind} ID '{group.Key}'.", documentId,
                kind == "parent" ? group.Key : null, kind == "child" ? group.Key : null);
    }

    private static void AddDuplicateOrdinalErrors(IEnumerable<(int Ordinal, string? ParentId)> values, int documentId, ICollection<ChunkingValidationError> errors, string? parentId)
    {
        foreach (var group in values.GroupBy(value => value.Ordinal).Where(group => group.Count() > 1))
            Add(errors, "ordinal.duplicate", $"Duplicate ordinal '{group.Key}'.", documentId, parentId, null);
    }

    private static void AddDuplicateHashWarnings(
        IEnumerable<string> values,
        ICollection<string> warnings,
        string kind)
    {
        foreach (var group in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .GroupBy(value => value, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            warnings.Add($"{kind}.hash_duplicate: Content hash '{group.Key}' occurs {group.Count()} times.");
        }
    }

    private static void Add(ICollection<ChunkingValidationError> errors, string code, string message, int? documentId = null, string? parentId = null, string? childId = null) =>
        errors.Add(new ChunkingValidationError(code, message, documentId, parentId, childId));

    private static ChunkingValidationResult Invalid(ChunkingValidationError error) => new(new[] { error });
}
