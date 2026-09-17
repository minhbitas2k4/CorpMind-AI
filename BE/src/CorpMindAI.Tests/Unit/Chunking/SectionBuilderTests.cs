using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Sections;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class SectionBuilderTests
{
    private readonly HeadingDetector _detector = new();
    private readonly SectionBuilder _builder = new();

    [Fact]
    public void Nested_headings_create_section_paths_for_headings_and_body_blocks()
    {
        var document = Document(
            Block("chapter", 1, 0, "Chapter I Corporate Policy", "title"),
            Block("chapter-body", 1, 1, "Policy introduction"),
            Block("section", 1, 2, "Section 1 Access Control"),
            Block("section-body", 2, 0, "Access requirements"),
            Block("article", 2, 1, "Article 1 Passwords"),
            Block("article-body", 2, 2, "Password requirements"));

        var result = Build(document);

        Assert.Equal(
            new[] { "Chapter I Corporate Policy" },
            result.GetSectionPath("chapter"));
        Assert.Equal(
            new[] { "Chapter I Corporate Policy" },
            result.GetSectionPath("chapter-body"));
        Assert.Equal(
            new[] { "Chapter I Corporate Policy", "Section 1 Access Control" },
            result.GetSectionPath("section-body"));
        Assert.Equal(
            new[] { "Chapter I Corporate Policy", "Section 1 Access Control", "Article 1 Passwords" },
            result.GetSectionPath("article-body"));
    }

    [Fact]
    public void Same_level_heading_closes_the_previous_section()
    {
        var document = Document(
            Block("one", 1, 0, "Section 1 One"),
            Block("one-body", 1, 1, "One body"),
            Block("two", 1, 2, "Section 2 Two"),
            Block("two-body", 1, 3, "Two body"));

        var result = Build(document);

        Assert.Equal(new[] { "Section 1 One" }, result.GetSectionPath("one-body"));
        Assert.Equal(new[] { "Section 2 Two" }, result.GetSectionPath("two-body"));
    }

    [Fact]
    public void Lower_level_heading_closes_nested_sections()
    {
        var document = Document(
            Block("chapter", 1, 0, "Chapter I Policy"),
            Block("section", 1, 1, "Section 1 Access"),
            Block("article", 1, 2, "Article 1 Passwords"),
            Block("section-two", 1, 3, "Section 2 Auditing"),
            Block("body", 1, 4, "Auditing body"));

        var result = Build(document);

        Assert.Equal(
            new[] { "Chapter I Policy", "Section 2 Auditing" },
            result.GetSectionPath("body"));
    }

    [Fact]
    public void Blocks_before_the_first_heading_have_an_empty_section_path()
    {
        var document = Document(
            Block("intro", 1, 0, "Document introduction"),
            Block("heading", 1, 1, "1. Scope"),
            Block("body", 1, 2, "Scope body"));

        var result = Build(document);

        Assert.Empty(result.GetSectionPath("intro"));
        Assert.Equal(new[] { "1. Scope" }, result.GetSectionPath("body"));
    }

    [Fact]
    public void Document_without_headings_gets_empty_assignments()
    {
        var document = Document(
            Block("one", 1, 0, "Plain body"),
            Block("two", 2, 0, "More plain body"));

        var result = Build(document);

        Assert.Empty(result.Headings);
        Assert.All(result.Assignments, assignment =>
        {
            Assert.False(assignment.IsHeading);
            Assert.Empty(assignment.SectionPath);
        });
    }

    [Fact]
    public void Heading_at_page_end_continues_to_the_next_page()
    {
        var document = Document(
            Block("heading", 1, 0, "Section 1 Access"),
            Block("body-next-page", 2, 0, "Access body"));

        var result = Build(document);

        Assert.Equal(new[] { "Section 1 Access" }, result.GetSectionPath("body-next-page"));
    }

    [Fact]
    public void Numbered_list_inherits_current_section_without_opening_a_new_section()
    {
        var list = string.Join(
            " ",
            Enumerable.Range(1, 6).Select(index =>
                $"{index}. Record the incident evidence, assign an owner, and document the review outcome."));
        var document = Document(
            Block("heading", 1, 0, "5. Incident Response Checklist", "title"),
            Block("list", 1, 1, list, "list"),
            Block("body", 2, 0, "The checklist must be reviewed after every incident."));

        var result = Build(document);

        Assert.Single(result.Headings);
        Assert.Equal("heading", result.Headings[0].BlockId);
        Assert.Equal(
            new[] { "5. Incident Response Checklist" },
            result.GetSectionPath("list"));
        Assert.Equal(
            new[] { "5. Incident Response Checklist" },
            result.GetSectionPath("body"));
    }

    [Fact]
    public void Numeric_hierarchy_is_offset_below_named_root_and_callouts_stay_content()
    {
        var document = Document(
            Block("chapter-one", 2, 0, "Chapter I - Information Security Governance", "title"),
            Block("scope", 2, 1, "1. Scope and Applicability", "title"),
            Block("identity", 2, 2, "1.1 Identity and Access Management", "title"),
            Block("joiner", 2, 3, "1.1.1 Joiner, Mover, and Leaver Controls", "title"),
            Block("joiner-body", 2, 4, "Joiner controls require approval."),
            Block("chapter-two", 3, 0, "Chapter II - Incident Response", "title"),
            Block("incident", 4, 0, "2. Incident Response Checklist", "title"),
            Block("incident-body", 4, 1, "The checklist is reviewed after every incident."),
            Block("roman", 7, 0, "IV. Recovery Exercise Procedure", "title"),
            Block("execution", 7, 1, "B. Execution", "title"),
            Block("warning", 7, 2, "WARNING: Do not execute recovery actions without approval.", "title"),
            Block("closure", 7, 3, "C. Closure", "title"),
            Block("closure-body", 7, 4, "Closure evidence is retained."),
            Block("appendix", 10, 0, "Appendix A - Legacy Recovery Authorization", "title"),
            Block("appendix-body", 10, 1, "Authorization reference: LRA-8821"));

        var result = Build(document);

        Assert.Equal(
            new[] { "Chapter I - Information Security Governance", "1. Scope and Applicability" },
            result.GetSectionPath("scope"));
        Assert.Equal(
            new[]
            {
                "Chapter I - Information Security Governance",
                "1. Scope and Applicability",
                "1.1 Identity and Access Management"
            },
            result.GetSectionPath("identity"));
        Assert.Equal(
            new[]
            {
                "Chapter I - Information Security Governance",
                "1. Scope and Applicability",
                "1.1 Identity and Access Management",
                "1.1.1 Joiner, Mover, and Leaver Controls"
            },
            result.GetSectionPath("joiner-body"));
        Assert.Equal(
            new[] { "Chapter II - Incident Response", "2. Incident Response Checklist" },
            result.GetSectionPath("incident-body"));
        Assert.Equal(
            new[] { "IV. Recovery Exercise Procedure", "B. Execution" },
            result.GetSectionPath("warning"));
        Assert.False(result.Assignments.Single(assignment => assignment.BlockId == "warning").IsHeading);
        Assert.Equal(
            new[] { "IV. Recovery Exercise Procedure", "C. Closure" },
            result.GetSectionPath("closure-body"));
        Assert.Equal(
            new[] { "Appendix A - Legacy Recovery Authorization" },
            result.GetSectionPath("appendix-body"));
    }

    [Fact]
    public void Unnumbered_labels_under_named_roots_are_nested_and_long_titles_use_a_stable_suffix()
    {
        var document = Document(
            Block(
                "title",
                1,
                0,
                "Northstar Dynamics Corporate Governance, Information Security, Data Retention, Vendor Management, and Business Continuity Handbook - 2026 Controlled Internal Edition",
                "title"),
            Block("control", 1, 1, "Document Control"),
            Block("chapter", 2, 0, "Chapter III - Business Continuity and Recovery", "title"),
            Block("matrix", 2, 1, "Business Service Recovery Matrix"),
            Block("exception", 3, 0, "Temporary Exception"));

        var result = Build(document);

        Assert.Equal(
            new[] { "Handbook - 2026 Controlled Internal Edition" },
            result.GetSectionPath("title"));
        Assert.Equal(new[] { "Document Control" }, result.GetSectionPath("control"));
        Assert.Equal(
            new[] { "Chapter III - Business Continuity and Recovery", "Business Service Recovery Matrix" },
            result.GetSectionPath("matrix"));
        Assert.Equal(
            new[] { "Chapter III - Business Continuity and Recovery", "Temporary Exception" },
            result.GetSectionPath("exception"));
    }

    [Fact]
    public void Skipped_heading_levels_are_normalized_and_reported()
    {
        var document = Document(
            Block("heading", 1, 0, "1.1 Scope"),
            Block("body", 1, 1, "Scope body"));

        var result = Build(document);

        Assert.Equal(1, result.Headings[0].Level);
        Assert.Contains(result.Warnings, warning => warning.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Sectioned_document_preserves_original_blocks_without_creating_chunks()
    {
        var document = Document(Block("heading", 1, 0, "1. Scope", "title"));

        var result = Build(document);

        Assert.Same(document, result.Document);
        Assert.Single(result.Assignments);
        Assert.DoesNotContain(result.Document.Blocks, block => block.BlockType is "parent" or "child");
    }

    [Fact]
    public void Headings_from_another_document_are_rejected()
    {
        var document = Document(Block("heading", 1, 0, "1. Scope"));
        var foreignHeading = new DetectedHeading(
            documentId: 99,
            blockId: "heading",
            text: "1. Scope",
            level: 1,
            sectionPath: new[] { "1. Scope" },
            confidence: 0.8);

        Assert.Throws<ArgumentException>(() => _builder.Build(document, new[] { foreignHeading }));
    }

    private SectionedDocument Build(NormalizedDocument document) =>
        _builder.Build(document, _detector.Detect(document));

    private static NormalizedDocument Document(params NormalizedBlock[] blocks) => new(
        documentId: 42,
        sourceSchemaVersion: "1.0",
        sourceContentHash: "source-hash",
        blocks: blocks);

    private static NormalizedBlock Block(
        string id,
        int page,
        int order,
        string text,
        string type = "text") => new(
        documentId: 42,
        blockId: id,
        pageNumber: page,
        readingOrder: order,
        blockType: type,
        text: text,
        confidence: 0.8,
        componentIds: new[] { id },
        sourceLayoutType: type);
}
