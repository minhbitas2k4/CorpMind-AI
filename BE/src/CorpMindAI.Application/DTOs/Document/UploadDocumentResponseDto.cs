namespace CorpMindAI.Application.DTOs.Document
{
    public class UploadDocumentResponseDto
    {
        public int SuccessCount { get; set; }

        public int FailedCount { get; set; }

        public List<DocumentUploadResultDto> SuccessfulFiles { get; set; } = new();

        public List<FailedFileDto> FailedFiles { get; set; } = new();
    }

    public class FailedFileDto
    {
        public string FileName { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;
    }
}
