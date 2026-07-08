namespace CorpMindAI.Application.DTOs.Document
{
    public class DocumentUploadResultDto
    {
        public int Id { get; set; }

        public string OriginalFileName { get; set; } = string.Empty;

        public long FileSize { get; set; }

        public DateTime UploadedAt { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
