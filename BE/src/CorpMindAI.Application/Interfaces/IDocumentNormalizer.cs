using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.DTOs.Document;

namespace CorpMindAI.Application.Interfaces;

public interface IDocumentNormalizer
{
    NormalizedDocument Normalize(StructuredDocumentDto source, string? documentTitle = null);
}
