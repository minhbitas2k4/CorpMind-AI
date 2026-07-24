namespace CorpMindAI.Application.DTOs.Document
{
    // DTO trả về trạng thái OCR hiện tại của document.
    public class OcrStatusResponseDto
    {
        public int DocumentId { get; set; }

        public string OcrStatus { get; set; } = string.Empty;

        public string? ErrorMessage { get; set; }

        public OcrResponseDto? Result { get; set; }
    }
}
