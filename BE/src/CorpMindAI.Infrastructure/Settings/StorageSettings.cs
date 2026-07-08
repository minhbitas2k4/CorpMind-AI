namespace CorpMindAI.Infrastructure.Settings
{
    /// <summary>
    /// Cài đặt cho Local File Storage.
    /// Đọc từ appsettings.json section "StorageSettings".
    /// </summary>
    public class StorageSettings
    {
        /// <summary>
        /// Thư mục gốc để lưu file upload.
        /// Không hard-code, đọc từ appsettings.json.
        /// </summary>
        public string UploadPath { get; set; } = "uploads";
    }
}
