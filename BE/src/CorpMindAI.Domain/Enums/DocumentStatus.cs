namespace CorpMindAI.Domain.Enums
{
    /// <summary>
    /// Trạng thái của tài liệu trong hệ thống.
    /// </summary>
    public enum DocumentStatus
    {
        Uploaded = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3,
        Pending = 4
    }
}
