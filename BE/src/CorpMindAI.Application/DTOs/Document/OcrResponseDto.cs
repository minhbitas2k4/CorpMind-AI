namespace CorpMindAI.Application.DTOs.Document
{
    public class OcrResponseDto
    {
        public int DocumentId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int TotalPages { get; set; }
        public double PageAverageConfidence { get; set; }
        public string OverallLevel { get; set; } = string.Empty;
        public List<OcrComponentDto> Components { get; set; } = new();
        public List<OcrValidationErrorDto> ValidationErrors { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }

    public class OcrComponentDto
    {
        public string ComponentType { get; set; } = string.Empty;
        public string RawText { get; set; } = string.Empty;
        public double AverageConfidence { get; set; }
        public string Level { get; set; } = string.Empty;
        public List<double> Bbox { get; set; } = new();
    }

    public class OcrValidationErrorDto
    {
        public string ComponentType { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
