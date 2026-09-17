using CorpMindAI.Application.Chunking.Models;

namespace CorpMindAI.Application.Interfaces;

public interface IHeadingDetector
{
    IReadOnlyList<DetectedHeading> Detect(NormalizedDocument document);
}
