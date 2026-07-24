namespace CorpMindAI.Application.Interfaces
{
    public interface IOcrService
    {
        Task<OcrServiceResponse> ProcessOcrAsync(
            int documentId,
            string filePath,
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
    }
}
