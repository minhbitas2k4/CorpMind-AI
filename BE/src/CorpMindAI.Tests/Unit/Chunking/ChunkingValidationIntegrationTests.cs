using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Validation;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class ChunkingValidationIntegrationTests
{
    [Fact]
    public void Validation_is_deterministic_for_the_same_result()
    {
        var result = new ChunkingResult(
            42, "1.0", "source", "1.0",
            new[] { new ParentChunkResult("p", 42, 0, "Policy", new[] { "Policy" }, "Parent", 1, 1, 1, new[] { "c" }, "ph") },
            new[] { new ChildChunkResult("c1", "p", 42, 0, new[] { "Policy" }, "Child", "Context: Child", 1, 1, 1, new[] { "c" }, "ch") });
        var validator = new ChunkingResultValidator();

        var first = validator.Validate(result);
        var second = validator.Validate(result);

        Assert.Equal(first.IsValid, second.IsValid);
        Assert.Equal(first.Errors, second.Errors);
        Assert.Equal(first.Warnings, second.Warnings);
    }
}
