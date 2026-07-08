namespace CorpMindAI.Domain.Enums
{
    /// <summary>
    /// Trạng thái của tài liệu trong hệ thống.
    /// Thiết kế mở rộng: để có thể thêm Processing, Completed khi tích hợp OCR.
    /// </summary>
    public enum DocumentStatus
    {
        Uploaded = 0
    }
}
