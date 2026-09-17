using CorpMindAI.Application.Chunking.Models;

namespace CorpMindAI.Application.Interfaces;

public interface ISectionBuilder
{
    SectionedDocument Build(
        NormalizedDocument document,
        IReadOnlyList<DetectedHeading> headings);
}
