using CorpMindAI.Application.Chunking.Models;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public class ChunkingContractTests
{
    [Fact]
    public void Options_have_the_planned_defaults()
    {
        var options = new ChunkingOptions();

        Assert.Equal(1200, options.ParentTargetTokens);
        Assert.Equal(2000, options.ParentMaxTokens);
        Assert.Equal(200, options.ParentMinTokens);
        Assert.Equal(350, options.ChildTargetTokens);
        Assert.Equal(500, options.ChildMaxTokens);
        Assert.Equal(60, options.ChildOverlapTokens);
        Assert.Equal(1.0, options.MinimumSourceCoverage);
        Assert.Equal("cl100k_base", options.TokenizerEncoding);
        Assert.Equal("6.0", options.ChunkerVersion);
        options.Validate();
    }

    [Fact]
    public void Options_reject_invalid_ordering_and_overlap()
    {
        Assert.Throws<ArgumentException>(() => new ChunkingOptions
        {
            ParentMinTokens = 1300,
            ParentTargetTokens = 1200,
        }.Validate());

        Assert.Throws<ArgumentException>(() => new ChunkingOptions
        {
            ChildOverlapTokens = 350,
            ChildTargetTokens = 350,
        }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkingOptions
        {
            MinimumSourceCoverage = 0,
        }.Validate());
    }

    [Fact]
    public void Options_reject_an_unsupported_tokenizer_encoding()
    {
        Assert.Throws<ArgumentException>(() => new ChunkingOptions
        {
            TokenizerEncoding = "another-encoding",
        }.Validate());
    }

    [Fact]
    public void Legacy_v5_options_remain_valid_and_have_a_distinct_run_identity()
    {
        var v5 = new ChunkingOptions { ChunkerVersion = "5.0" };
        var v6 = new ChunkingOptions { ChunkerVersion = "6.0" };

        v5.Validate();
        v6.Validate();

        var v5Id = ChunkingRunIdentity.CreateId(
            42,
            "source-hash",
            "1.0",
            v5.ChunkerVersion,
            ChunkingRunIdentity.SerializeConfiguration(v5));
        var v6Id = ChunkingRunIdentity.CreateId(
            42,
            "source-hash",
            "1.1",
            v6.ChunkerVersion,
            ChunkingRunIdentity.SerializeConfiguration(v6));

        Assert.NotEqual(v5Id, v6Id);
    }

    [Fact]
    public void Child_requires_a_parent_id()
    {
        Assert.Throws<ArgumentException>(() => CreateChild(parentChunkId: " "));
    }

    [Fact]
    public void Chunking_result_rejects_an_orphan_child()
    {
        var parent = CreateParent();
        var child = CreateChild(parentChunkId: "missing-parent");

        Assert.Throws<ArgumentException>(() => new ChunkingResult(
            documentId: 42,
            sourceSchemaVersion: "1.0",
            sourceContentHash: "source-hash",
            chunkerVersion: "1.0",
            parents: new[] { parent },
            children: new[] { child }));
    }

    [Fact]
    public void Chunking_result_preserves_traceability_metadata()
    {
        var parent = CreateParent();
        var child = CreateChild(parent.Id);
        var result = new ChunkingResult(
            documentId: 42,
            sourceSchemaVersion: "1.0",
            sourceContentHash: "source-hash",
            chunkerVersion: "1.0",
            parents: new[] { parent },
            children: new[] { child });

        Assert.Equal(42, result.DocumentId);
        Assert.Equal(parent.Id, result.Children[0].ParentChunkId);
        Assert.Equal(new[] { "Policy" }, result.Children[0].SectionPath);
        Assert.Equal(new[] { "doc-42-p1-c0001" }, result.Children[0].ComponentIds);
        Assert.Equal(1, result.Children[0].PageFrom);
        Assert.Equal(1, result.Children[0].PageTo);
    }

    private static ParentChunkResult CreateParent() => new(
        id: "parent-001",
        documentId: 42,
        ordinal: 0,
        title: "Policy",
        sectionPath: new[] { "Policy" },
        content: "A parent context.",
        tokenCount: 3,
        pageFrom: 1,
        pageTo: 1,
        componentIds: new[] { "doc-42-p1-c0000" },
        contentHash: "parent-hash");

    private static ChildChunkResult CreateChild(string parentChunkId = "parent-001") => new(
        id: "child-001",
        parentChunkId: parentChunkId,
        documentId: 42,
        ordinal: 0,
        sectionPath: new[] { "Policy" },
        rawContent: "A child passage.",
        contextualizedContent: "Policy\nA child passage.",
        tokenCount: 3,
        pageFrom: 1,
        pageTo: 1,
        componentIds: new[] { "doc-42-p1-c0001" },
        contentHash: "child-hash");
}
