using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Sections;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class HeadingDetectorTests
{
    private readonly HeadingDetector _detector = new();

    [Fact]
    public void Title_block_is_detected_with_level_one_when_no_pattern_exists()
    {
        var document = Document(Block("title", 1, 0, "Corporate Policy", "title"));

        var heading = Assert.Single(_detector.Detect(document));

        Assert.Equal("title", heading.BlockId);
        Assert.Equal("Corporate Policy", heading.Text);
        Assert.Equal(1, heading.Level);
        Assert.Equal(0.8, heading.Confidence);
    }

    [Fact]
    public void English_named_heading_patterns_are_detected()
    {
        var document = Document(
            Block("chapter", 1, 0, "Chapter I Corporate Policy"),
            Block("part", 1, 1, "Part A Governance"),
            Block("section", 1, 2, "Section 1 Access Control"),
            Block("article", 1, 3, "Article 1 Passwords"),
            Block("appendix", 1, 4, "Appendix A Glossary"));

        var headings = _detector.Detect(document);

        Assert.Equal(
            new[] { "chapter", "part", "section", "article", "appendix" },
            headings.Select(heading => heading.BlockId));
        Assert.Equal(new[] { 1, 1, 2, 3, 1 }, headings.Select(heading => heading.Level));
    }

    [Fact]
    public void Numeric_roman_and_letter_headings_have_deterministic_levels()
    {
        var document = Document(
            Block("n1", 1, 0, "1. Overview"),
            Block("n2", 1, 1, "1.1 Scope"),
            Block("n3", 1, 2, "1.1.1 Exceptions"),
            Block("roman", 1, 3, "I. Introduction"),
            Block("letter", 1, 4, "A. Definitions"));

        var headings = _detector.Detect(document);

        Assert.Equal(new[] { 1, 2, 3, 1, 2 }, headings.Select(heading => heading.Level));
    }

    [Fact]
    public void Explicit_patterns_are_case_insensitive_for_English_documents()
    {
        var document = Document(Block("section", 1, 0, "SECTION 1 ACCESS CONTROL"));

        var heading = Assert.Single(_detector.Detect(document));

        Assert.Equal(2, heading.Level);
    }

    [Fact]
    public void Decimal_date_page_number_contract_number_and_sentence_are_not_headings()
    {
        var document = Document(
            Block("decimal", 1, 0, "3.14"),
            Block("date", 1, 1, "2026-09-09"),
            Block("page", 1, 2, "Page 1"),
            Block("contract", 1, 3, "ABC-2026-001"),
            Block("sentence", 1, 4, "THIS IS A BODY SENTENCE."),
            Block("reference", 1, 5, "See Section 1.2 for details."));

        Assert.Empty(_detector.Detect(document));
    }

    [Fact]
    public void Conservative_uppercase_candidate_can_be_detected_but_body_sentence_is_not()
    {
        var document = Document(
            Block("candidate", 1, 0, "INTERNAL CONTROL POLICY"),
            Block("body", 1, 1, "THIS IS A BODY SENTENCE."));

        var headings = _detector.Detect(document);

        Assert.Equal(new[] { "candidate" }, headings.Select(heading => heading.BlockId));
    }

    [Fact]
    public void Numbered_list_block_is_not_detected_as_a_heading()
    {
        var list = string.Join(
            " ",
            Enumerable.Range(1, 12).Select(index =>
                $"{index}. Record the control evidence, assign an owner, and document the review outcome."));
        var document = Document(Block("list", 1, 0, list, "list", atomic: true));

        Assert.Empty(_detector.Detect(document));
    }

    [Fact]
    public void Table_block_starting_with_a_numeric_marker_is_not_detected_as_a_heading()
    {
        var document = Document(Block(
            "table",
            1,
            0,
            "1. Control | Owner | Status\n2. Access review | Security | Complete",
            "table",
            atomic: true));

        Assert.Empty(_detector.Detect(document));
    }

    [Fact]
    public void Long_numbered_body_is_not_detected_as_a_heading()
    {
        var body = "1. " + string.Join(
            " ",
            Enumerable.Repeat(
                "This operational paragraph explains why evidence must be retained, reviewed, approved, and traceable across the enterprise.",
                8));
        var document = Document(Block("body", 1, 0, body));

        Assert.True(body.Length > 255);
        Assert.Empty(_detector.Detect(document));
    }

    [Fact]
    public void Multiple_numbered_markers_in_a_text_block_are_not_detected_as_a_heading()
    {
        var document = Document(Block(
            "body",
            1,
            0,
            "1. Record evidence. 2. Assign an owner. 3. Review the result."));

        Assert.Empty(_detector.Detect(document));
    }

    [Fact]
    public void Numbered_body_sentence_is_not_detected_as_a_heading()
    {
        var document = Document(Block(
            "body",
            1,
            0,
            "1. The incident response process must be documented and reviewed after every event."));

        Assert.Empty(_detector.Detect(document));
    }

    [Theory]
    [InlineData("WARNING: Do not execute recovery actions without approval.")]
    [InlineData("CAUTION: This step can overwrite the production recovery point.")]
    [InlineData("NOTE: Record the evidence before closing the incident.")]
    [InlineData("IMPORTANT: Escalate unresolved exceptions to Corporate Assurance.")]
    [InlineData("TIP: Use the controlled repository copy for every decision.")]
    public void Sentence_shaped_callouts_are_not_detected_even_when_marked_as_titles(string text)
    {
        var document = Document(Block("callout", 1, 0, text, "title"));

        Assert.Empty(_detector.Detect(document));
    }

    [Fact]
    public void Sentence_shaped_numeric_footnote_is_not_detected_as_a_heading()
    {
        var document = Document(Block(
            "footnote",
            8,
            0,
            "1 Contract value means the total amount committed under the signed agreement.",
            "text"));

        Assert.Empty(_detector.Detect(document));
    }

    [Fact]
    public void Short_numeric_heading_without_trailing_dot_remains_valid()
    {
        var document = Document(Block("heading", 1, 0, "1 Scope", "title"));

        var heading = Assert.Single(_detector.Detect(document));

        Assert.Equal("1 Scope", heading.Text);
        Assert.Equal(1, heading.Level);
    }

    [Fact]
    public void Short_standalone_text_label_is_detected_as_a_heading()
    {
        var document = Document(
            Block("control", 1, 0, "Document Control"),
            Block("exception", 2, 0, "Temporary Exception"));

        var headings = _detector.Detect(document);

        Assert.Equal(new[] { "control", "exception" }, headings.Select(heading => heading.BlockId));
        Assert.All(headings, heading => Assert.Equal(1, heading.Level));
    }

    [Fact]
    public void Detection_is_sorted_and_preserves_block_ids_and_confidence()
    {
        var document = Document(
            Block("second", 2, 1, "2. Second", confidence: 0.91),
            Block("first", 1, 2, "1. First", confidence: 0.73),
            Block("tie-b", 1, 1, "B. Tie", confidence: 0.82),
            Block("tie-a", 1, 1, "A. Tie", confidence: 0.81));

        var headings = _detector.Detect(document);

        Assert.Equal(new[] { "tie-a", "tie-b", "first", "second" }, headings.Select(heading => heading.BlockId));
        Assert.Equal(0.81, headings[0].Confidence);
        Assert.Equal(0.91, headings[3].Confidence);
    }

    [Fact]
    public void Duplicate_block_ids_are_rejected()
    {
        var document = Document(
            Block("duplicate", 1, 0, "1. One"),
            Block("duplicate", 1, 1, "2. Two"));

        Assert.Throws<ArgumentException>(() => _detector.Detect(document));
    }

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
        string type = "text",
        double confidence = 0.8,
        bool atomic = false) => new(
        documentId: 42,
        blockId: id,
        pageNumber: page,
        readingOrder: order,
        blockType: type,
        text: text,
        confidence: confidence,
        componentIds: new[] { id },
        sourceLayoutType: type,
        isAtomic: atomic);
}
