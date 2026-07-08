namespace CorpMindAI.Application.Settings
{
    /// <summary>
    /// Cài đặt cho chức năng upload tài liệu.
    /// Đọc từ appsettings.json section "UploadSettings".
    /// Không hard-code bất kỳ giá trị nào trong code.
    /// </summary>
    public class UploadSettings
    {
        /// <summary>
        /// Kích thước file tối đa tính bằng byte.
        /// Mặc định: 20MB = 20 * 1024 * 1024.
        /// Cấu hình trong appsettings: "MaxFileSizeBytes": 20971520
        /// </summary>
        public long MaxFileSizeBytes { get; set; } = 20 * 1024 * 1024;

        /// <summary>
        /// Số file tối đa được upload trong một request.
        /// Mặc định: 10 files.
        /// </summary>
        public int MaxFilesPerRequest { get; set; } = 10;

        /// <summary>
        /// Danh sách MIME type được chấp nhận.
        /// Mặc định: chỉ PDF.
        /// </summary>
        public string[] AllowedContentTypes { get; set; } = { "application/pdf" };

        /// <summary>
        /// Danh sách extension được chấp nhận (lowercase, có dấu chấm).
        /// Mặc định: ".pdf"
        /// </summary>
        public string[] AllowedExtensions { get; set; } = { ".pdf" };
    }
}
