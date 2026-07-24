using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CorpMindAI.Infrastructure.Storage
{
    public class LocalFileStorage : IFileStorage
    {
        private readonly StorageSettings _storageSettings;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<LocalFileStorage> _logger;

        // Thư mục con logic để tổ chức file — portable sang MinIO/S3
        private const string LogicalPrefix = "documents";

        public LocalFileStorage(
            IOptions<StorageSettings> storageSettings,
            IWebHostEnvironment environment,
            ILogger<LocalFileStorage> logger)
        {
            _storageSettings = storageSettings.Value;
            _environment = environment;
            _logger = logger;
        }

        private string GetPhysicalBasePath()
        {
            if (Path.IsPathRooted(_storageSettings.UploadPath))
                return _storageSettings.UploadPath;

            return Path.Combine(_environment.ContentRootPath, _storageSettings.UploadPath);
        }

        private static string BuildStorageKey(string originalFileName)
        {
            var now = DateTime.UtcNow;
            var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
            var uniqueId = Guid.NewGuid().ToString("N"); 
            return $"{LogicalPrefix}/{now:yyyy}/{now:MM}/{uniqueId}{extension}";
        }

        private string StorageKeyToPhysicalPath(string storageKey)
        {
            var relativePath = storageKey.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(GetPhysicalBasePath(), relativePath);
        }

        public string GetPhysicalPath(string storageKey)
        {
            return StorageKeyToPhysicalPath(storageKey);
        }

        public async Task<string> UploadAsync(
            Stream fileStream,
            string originalFileName,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            var storageKey = BuildStorageKey(originalFileName);
            var physicalPath = StorageKeyToPhysicalPath(storageKey);

            // Đảm bảo thư mục tồn tại — tạo tự động nếu chưa có
            var directory = Path.GetDirectoryName(physicalPath)!;
            Directory.CreateDirectory(directory);

            _logger.LogDebug(
                "Đang lưu file '{OriginalName}' → '{PhysicalPath}' (storageKey: '{StorageKey}')",
                originalFileName, physicalPath, storageKey);

            // CopyToAsync: streaming — không đọc toàn bộ file vào RAM
            await using var fileWriteStream = new FileStream(
                physicalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920, 
                useAsync: true);

            await fileStream.CopyToAsync(fileWriteStream, cancellationToken);

            _logger.LogDebug("Lưu file thành công: storageKey='{StorageKey}'", storageKey);

            // Trả về storage key (đường dẫn logic) — KHÔNG trả về đường dẫn vật lý
            return storageKey;
        }

        public Task<Stream> DownloadAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            var physicalPath = StorageKeyToPhysicalPath(storageKey);

            if (!File.Exists(physicalPath))
                throw new FileNotFoundException($"Không tìm thấy file với storageKey: {storageKey}");

            Stream stream = new FileStream(
                physicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            return Task.FromResult(stream);
        }

        public Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            var physicalPath = StorageKeyToPhysicalPath(storageKey);

            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
                _logger.LogInformation("Xóa file thành công: storageKey='{StorageKey}'", storageKey);
            }
            else
            {
                _logger.LogWarning(
                    "Yêu cầu xóa file nhưng không tìm thấy: storageKey='{StorageKey}'",
                    storageKey);
            }

            return Task.CompletedTask;
        }
    }
}
