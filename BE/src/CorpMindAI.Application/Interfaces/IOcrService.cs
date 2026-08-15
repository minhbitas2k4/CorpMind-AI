namespace CorpMindAI.Application.Interfaces
{
    public interface IOcrService
    {
        Task<OcrServiceResponse> ProcessOcrAsync(
            int documentId,
            string filePath,
            CancellationToken cancellationToken = default);

        Task<long> DownloadReconstructionAsync(
            int documentId,
            string artifactId,
            Stream destination,
            CancellationToken cancellationToken = default);

        Task CleanupReconstructionAsync(
            int documentId,
            string artifactId,
            CancellationToken cancellationToken = default);
    }

    public class OcrServiceResponse
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int TotalPages { get; set; }
        public double PageAverageConfidence { get; set; }
        public string OverallLevel { get; set; } = string.Empty;
        public string ComponentsJson { get; set; } = "[]";
        public string ValidationErrorsJson { get; set; } = "[]";
        public string? SchemaVersion { get; set; }
        public string? StructuredDocumentJson { get; set; }
        public ReconstructionArtifactResponse? ReconstructionArtifact { get; set; }
    }

    public class ReconstructionArtifactResponse
    {
        public string ArtifactId { get; set; } = string.Empty;
        public string DocumentId { get; set; } = string.Empty;
        public long Size { get; set; }
        public double RenderSeconds { get; set; }
    }
}
