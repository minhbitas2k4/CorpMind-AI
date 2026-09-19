using CorpMindAI.Application.Services;
using Xunit;

namespace CorpMindAI.Tests.Unit.Search;

[Trait("TestType", "Unit")]
public sealed class EmbeddingChunkSearchabilityTests
{
    [Fact]
    public void IsSearchable_returns_false_when_content_is_only_a_section_heading()
    {
        var result = EmbeddingChunkSearchability.IsSearchable(
            " Document Control ",
            new[] { "Handbook", "Document Control" });

        Assert.False(result);
    }

    [Fact]
    public void IsSearchable_returns_true_when_content_adds_policy_text_to_a_heading()
    {
        var result = EmbeddingChunkSearchability.IsSearchable(
            "Document Control\nEffective date: September 14, 2026.",
            new[] { "Document Control" });

        Assert.True(result);
    }

    [Fact]
    public void IsSearchable_returns_false_for_empty_content()
    {
        Assert.False(EmbeddingChunkSearchability.IsSearchable("  ", new[] { "Policy" }));
    }
}
