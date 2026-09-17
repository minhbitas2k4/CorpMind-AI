using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Validation;
using CorpMindAI.Application.Interfaces;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class ChunkingResultValidatorTests
{
    private readonly ChunkingResultValidator _validator = new();

    [Fact]
    public void Valid_result_is_accepted_without_mutating_input()
    {
        var result = CreateResult();
        var before = result.Parents[0].Content;

        var validation = _validator.Validate(result);

        Assert.True(validation.IsValid);
        Assert.Equal(before, result.Parents[0].Content);
    }

    [Fact]
    public void Complete_source_inventory_is_fully_covered()
    {
        var validation = _validator.Validate(CreateResult(), CreateInventory(), 1.0);

        Assert.True(validation.IsValid);
        Assert.Contains(validation.Warnings, warning =>
            warning.Contains("source_coverage.value: 1.000000", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_parent_content_fails_configured_token_and_lexical_coverage()
    {
        var parent = new ParentChunkResult(
            "parent-1", 42, 0, "Policy", new[] { "Policy" }, "Parent", 1, 1, 1,
            new[] { "component-1" }, "parent-hash");
        var child = CreateChild(parent.Id);
        var result = new ChunkingResult(
            42, "1.0", "source", "1.0", new[] { parent }, new[] { child });

        var validation = _validator.Validate(result, CreateInventory(), 1.0);

        Assert.Contains(validation.Errors, error => error.Code == "source_coverage.below_threshold");
    }

    [Fact]
    public void Configured_coverage_threshold_is_honored()
    {
        var parent = new ParentChunkResult(
            "parent-1", 42, 0, "Policy", new[] { "Policy" }, "Parent", 1, 1, 1,
            new[] { "component-1" }, "parent-hash");
        var result = new ChunkingResult(
            42, "1.0", "source", "1.0", new[] { parent },
            new[] { CreateChild(parent.Id, rawContent: "Parent") });

        var validation = _validator.Validate(result, CreateInventory(), 0.5);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void Every_original_component_must_be_retained_or_have_a_notice()
    {
        var inventory = new ChunkingSourceInventory(
            42,
            new[] { "component-1", "lost-component" },
            new[] { new ChunkingSourceBlock("block-1", "Parent content", 2, new[] { "component-1" }) },
            Array.Empty<NormalizationNotice>());

        var validation = _validator.Validate(CreateResult(), inventory, 1.0);

        Assert.Contains(validation.Errors, error => error.Code == "source.component_unaccounted");
    }

    [Fact]
    public void Intentionally_excluded_component_is_accounted_for_by_notice()
    {
        var inventory = new ChunkingSourceInventory(
            42,
            new[] { "component-1", "page-number" },
            new[] { new ChunkingSourceBlock("block-1", "Parent content", 2, new[] { "component-1" }) },
            new[] { new NormalizationNotice("page-number", 1, "Page number", "1") });

        var validation = _validator.Validate(CreateResult(), inventory, 1.0);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void Retained_component_missing_from_every_parent_is_rejected()
    {
        var inventory = new ChunkingSourceInventory(
            42,
            new[] { "component-1", "component-2" },
            new[]
            {
                new ChunkingSourceBlock("block-1", "Parent content", 2, new[] { "component-1" }),
                new ChunkingSourceBlock("block-2", "Missing", 1, new[] { "component-2" })
            },
            Array.Empty<NormalizationNotice>());

        var validation = _validator.Validate(CreateResult(), inventory, 0.5);

        Assert.Contains(validation.Errors, error => error.Code == "source.component_uncovered");
    }

    [Fact]
    public void Parent_cannot_reference_an_unknown_or_excluded_component()
    {
        var parent = new ParentChunkResult(
            "parent-1", 42, 0, "Policy", new[] { "Policy" }, "Parent content", 2, 1, 1,
            new[] { "excluded-component" }, "parent-hash");
        var result = new ChunkingResult(
            42, "1.0", "source", "1.0", new[] { parent },
            new[] { CreateChild(parent.Id) });
        var inventory = new ChunkingSourceInventory(
            42,
            new[] { "component-1", "excluded-component" },
            new[] { new ChunkingSourceBlock("block-1", "Parent content", 2, new[] { "component-1" }) },
            new[] { new NormalizationNotice("excluded-component", 1, "Page number", "1") });

        var validation = _validator.Validate(result, inventory, 1.0);

        Assert.Contains(validation.Errors, error => error.Code == "parent.component_excluded");
        Assert.Contains(validation.Errors, error => error.Code == "source.component_uncovered");
    }

    [Fact]
    public void Null_result_is_rejected_with_stable_error_code()
    {
        var validation = _validator.Validate(null);

        Assert.False(validation.IsValid);
        Assert.Equal("result.required", validation.Errors[0].Code);
    }

    [Fact]
    public void Orphan_child_is_rejected_by_the_result_contract_before_validation()
    {
        var parent = CreateParent();
        var orphan = CreateChild("missing-parent");
        Assert.Throws<ArgumentException>(() => new ChunkingResult(
            42, "1.0", "source", "1.0", new[] { parent }, new[] { orphan }));
    }

    [Fact]
    public void Child_outside_parent_page_range_is_rejected()
    {
        var parent = CreateParent();
        var child = CreateChild(parent.Id, pageFrom: 2, pageTo: 2);
        var result = new ChunkingResult(42, "1.0", "source", "1.0", new[] { parent }, new[] { child });

        var validation = _validator.Validate(result);

        Assert.Contains(validation.Errors, error => error.Code == "child.page_outside_parent");
    }

    [Fact]
    public void Child_content_outside_parent_is_rejected()
    {
        var parent = CreateParent();
        var child = new ChildChunkResult(
            "child-foreign", parent.Id, 42, 0, parent.SectionPath, "Invented content",
            "Document: D\nSection: Policy\nContent: Invented content", 2, 1, 1,
            parent.ComponentIds, "child-hash");
        var result = new ChunkingResult(
            42, "1.0", "source", "1.0", new[] { parent }, new[] { child });

        var validation = _validator.Validate(result);

        Assert.Contains(validation.Errors, error => error.Code == "child.content_outside_parent");
        Assert.Contains(validation.Errors, error => error.Code == "child.coverage_incomplete");
    }

    [Fact]
    public void Parent_without_children_is_rejected()
    {
        var result = new ChunkingResult(
            42, "1.0", "source", "1.0", new[] { CreateParent() }, Array.Empty<ChildChunkResult>());

        var validation = _validator.Validate(result);

        Assert.Contains(validation.Errors, error => error.Code == "parent.child_required");
    }

    [Fact]
    public void Empty_retained_source_is_rejected_before_embedding()
    {
        var result = new ChunkingResult(
            42, "1.0", "source", "1.0", Array.Empty<ParentChunkResult>(), Array.Empty<ChildChunkResult>());
        var inventory = new ChunkingSourceInventory(
            42,
            new[] { "empty-component" },
            Array.Empty<ChunkingSourceBlock>(),
            new[] { new NormalizationNotice("empty-component", 1, "Empty component", string.Empty) });

        var validation = _validator.Validate(result, inventory, 1.0);

        Assert.Contains(validation.Errors, error => error.Code == "source.no_searchable_content");
    }

    [Fact]
    public void Duplicate_child_ids_and_ordinals_are_rejected_by_the_result_contract()
    {
        var parent = CreateParent();
        var first = CreateChild(parent.Id);
        var second = CreateChild(parent.Id);
        Assert.Throws<ArgumentException>(() => new ChunkingResult(
            42, "1.0", "source", "1.0", new[] { parent }, new[] { first, second }));
    }

    [Fact]
    public void Strict_validation_recomputes_tokens_hashes_and_hard_limits()
    {
        var parent = new ParentChunkResult(
            "parent-strict", 42, 0, "Policy", new[] { "Policy" }, "one two three", 2,
            1, 1, new[] { "component-1" }, Hash("incorrect"));
        var contextualized = "Document: D\nSection: Policy\nContent: one two three";
        var child = new ChildChunkResult(
            "child-strict", parent.Id, 42, 0, parent.SectionPath, "one two three",
            contextualized, 1, 1, 1, parent.ComponentIds, Hash("incorrect"));
        var result = new ChunkingResult(42, "1.0", "source", "3.0", new[] { parent }, new[] { child });
        var inventory = new ChunkingSourceInventory(
            42,
            new[] { "component-1" },
            new[] { new ChunkingSourceBlock("block-1", "one two three", 1, new[] { "component-1" }) },
            Array.Empty<NormalizationNotice>());
        var options = new ChunkingOptions
        {
            ParentTargetTokens = 2,
            ParentMaxTokens = 2,
            ParentMinTokens = 1,
            ChildTargetTokens = 7,
            ChildMaxTokens = 7,
            ChildOverlapTokens = 0
        };

        var validation = _validator.Validate(result, inventory, options, new WordTokenCounter());

        Assert.Contains(validation.Errors, error => error.Code == "token_count.mismatch");
        Assert.Contains(validation.Errors, error => error.Code == "content_hash.mismatch");
        Assert.Contains(validation.Errors, error => error.Code == "token_count.hard_limit_exceeded");
        Assert.Contains(validation.Errors, error => error.Code == "source.token_count_mismatch");
    }

    [Fact]
    public void Repeated_boilerplate_hash_is_a_warning_not_a_validation_error()
    {
        var parents = new[]
        {
            StrictParent("parent-a", 0, "component-a"),
            StrictParent("parent-b", 1, "component-b")
        };
        var children = new[]
        {
            StrictChild("child-a", parents[0]),
            StrictChild("child-b", parents[1])
        };
        var result = new ChunkingResult(42, "1.0", "source", "3.0", parents, children);
        var inventory = new ChunkingSourceInventory(
            42,
            new[] { "component-a", "component-b" },
            new[]
            {
                new ChunkingSourceBlock("block-a", "Repeated text", 2, new[] { "component-a" }),
                new ChunkingSourceBlock("block-b", "Repeated text", 2, new[] { "component-b" })
            },
            Array.Empty<NormalizationNotice>());

        var validation = _validator.Validate(result, inventory, new ChunkingOptions(), new WordTokenCounter());

        Assert.True(validation.IsValid);
        Assert.Contains(validation.Warnings, warning => warning.StartsWith("parent.hash_duplicate", StringComparison.Ordinal));
        Assert.Contains(validation.Warnings, warning => warning.StartsWith("child.hash_duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void Parent_title_at_persistence_limit_is_valid()
    {
        var title = new string('T', 255);
        var parent = new ParentChunkResult(
            "parent-1", 42, 0, title, new[] { title }, "Parent content", 2, 1, 1,
            new[] { "component-1" }, "parent-hash");
        var validation = _validator.Validate(
            new ChunkingResult(42, "1.0", "source", "4.0", new[] { parent },
                new[] { CreateChild(parent.Id, sectionPath: new[] { title }) }));

        Assert.True(validation.IsValid);
        Assert.DoesNotContain(validation.Errors, error => error.Code == "PARENT_TITLE_TOO_LONG");
    }

    [Fact]
    public void Parent_title_over_persistence_limit_is_rejected_before_persistence_without_truncation()
    {
        var title = new string('T', 256);
        var parent = new ParentChunkResult(
            "parent-1", 42, 0, title, new[] { title }, "Parent content", 2, 1, 1,
            new[] { "component-1" }, "parent-hash");
        var validation = _validator.Validate(
            new ChunkingResult(42, "1.0", "source", "4.0", new[] { parent },
                new[] { CreateChild(parent.Id, sectionPath: new[] { title }) }));

        Assert.Contains(validation.Errors, error => error.Code == "PARENT_TITLE_TOO_LONG");
        Assert.Equal(256, parent.Title!.Length);
    }

    private static ChunkingResult CreateResult() =>
        new(42, "1.0", "source", "1.0", new[] { CreateParent() }, new[] { CreateChild("parent-1") });

    private static ChunkingSourceInventory CreateInventory() => new(
        42,
        new[] { "component-1" },
        new[] { new ChunkingSourceBlock("block-1", "Parent content", 2, new[] { "component-1" }) },
        Array.Empty<NormalizationNotice>());

    private static ParentChunkResult CreateParent() => new(
        "parent-1", 42, 0, "Policy", new[] { "Policy" }, "Parent content", 2, 1, 1,
        new[] { "component-1" }, "parent-hash");

    private static ChildChunkResult CreateChild(
        string parentId,
        int pageFrom = 1,
        int pageTo = 1,
        IReadOnlyList<string>? sectionPath = null,
        string rawContent = "Parent content") => new(
        "child-1", parentId, 42, 0, sectionPath ?? new[] { "Policy" }, rawContent, $"Document: D\nSection: Policy\nContent: {rawContent}",
        2, pageFrom, pageTo, new[] { "component-1" }, "child-hash");

    private static ParentChunkResult StrictParent(string id, int ordinal, string componentId) => new(
        id, 42, ordinal, "Policy", new[] { "Policy" }, "Repeated text", 2, 1, 1,
        new[] { componentId }, Hash("Repeated text"));

    private static ChildChunkResult StrictChild(string id, ParentChunkResult parent)
    {
        const string contextualized = "Document: D\nSection: Policy\nContent: Repeated text";
        return new ChildChunkResult(
            id, parent.Id, 42, 0, parent.SectionPath, "Repeated text", contextualized,
            7, 1, 1, parent.ComponentIds, Hash("Repeated text"));
    }

    private static string Hash(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed class WordTokenCounter : ITokenCounter
    {
        public int Count(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
