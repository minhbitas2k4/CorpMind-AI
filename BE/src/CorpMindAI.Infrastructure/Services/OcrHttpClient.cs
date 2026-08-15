using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using CorpMindAI.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Infrastructure.Services
{
    public class OcrHttpClient : IOcrService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OcrHttpClient> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public OcrHttpClient(HttpClient httpClient, ILogger<OcrHttpClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<OcrServiceResponse> ProcessOcrAsync(
            int documentId,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new
            {
                document_id = documentId.ToString(),
                file_path = filePath,
            };

            _logger.LogInformation(
                "Gọi OCR service: documentId={DocumentId}",
                documentId);

            try
            {
                var response = await _httpClient.PostAsJsonAsync("/ocr", requestBody, JsonOptions, cancellationToken);

                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "OCR service trả về HTTP {StatusCode}: {Response}",
                        response.StatusCode,
                        responseContent);

                    return new OcrServiceResponse
                    {
                        Success = false,
                        ErrorMessage = $"OCR service returned HTTP {response.StatusCode}: {responseContent}",
                    };
                }

                using var doc = JsonDocument.Parse(responseContent);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusProp) &&
                    statusProp.GetString() == "error")
                {
                    var errorMsg = root.TryGetProperty("error_message", out var msgProp)
                        ? msgProp.GetString()
                        : "Unknown error from OCR service";

                    return new OcrServiceResponse
                    {
                        Success = false,
                        ErrorMessage = errorMsg,
                    };
                }

                var result = new OcrServiceResponse
                {
                    Success = true,
                    TotalPages = root.TryGetProperty("total_pages", out var pages) ? pages.GetInt32() : 1,
                    PageAverageConfidence = root.TryGetProperty("page_average_confidence", out var conf) ? conf.GetDouble() : 0,
                    OverallLevel = root.TryGetProperty("overall_level", out var level) ? level.GetString() ?? "" : "",
                    SchemaVersion = root.TryGetProperty("schema_version", out var schemaVersion)
                        ? schemaVersion.GetString()
                        : null,
                };

                if (root.TryGetProperty("components", out var components))
                {
                    result.ComponentsJson = components.GetRawText();
                }

                if (root.TryGetProperty("validation_errors", out var validationErrors))
                {
                    result.ValidationErrorsJson = validationErrors.GetRawText();
                }

                if (result.SchemaVersion is not null &&
                    root.TryGetProperty("document_id", out var responseDocumentId) &&
                    root.TryGetProperty("pages", out var structuredPages))
                {
                    // Persist the versioned document envelope, not merely the pages array.
                    // JsonElement serialization preserves every nested field supplied by Python.
                    result.StructuredDocumentJson = JsonSerializer.Serialize(new
                    {
                        schema_version = result.SchemaVersion,
                        document_id = responseDocumentId.GetString() ?? documentId.ToString(),
                        total_pages = result.TotalPages,
                        pages = structuredPages,
                    });
                }

                if (root.TryGetProperty("reconstruction_artifact", out var artifact) &&
                    artifact.ValueKind == JsonValueKind.Object &&
                    artifact.TryGetProperty("artifact_id", out var artifactId))
                {
                    result.ReconstructionArtifact = new ReconstructionArtifactResponse
                    {
                        ArtifactId = artifactId.GetString() ?? string.Empty,
                        DocumentId = artifact.TryGetProperty("document_id", out var artifactDocumentId)
                            ? artifactDocumentId.GetString() ?? string.Empty
                            : string.Empty,
                        Size = artifact.TryGetProperty("size", out var artifactSize)
                            ? artifactSize.GetInt64()
                            : 0,
                        RenderSeconds = artifact.TryGetProperty("render_seconds", out var renderSeconds)
                            ? renderSeconds.GetDouble()
                            : 0,
                    };
                }

                _logger.LogInformation(
                    "OCR service hoàn thành: {TotalPages} trang, confidence={Confidence:F4}, level={Level}",
                    result.TotalPages,
                    result.PageAverageConfidence,
                    result.OverallLevel);

                return result;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Không thể kết nối đến OCR service");
                return new OcrServiceResponse
                {
                    Success = false,
                    ErrorMessage = $"Không thể kết nối đến OCR service: {ex.Message}",
                };
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "OCR service timeout");
                return new OcrServiceResponse
                {
                    Success = false,
                    ErrorMessage = "OCR service timeout - file quá lớn hoặc service không phản hồi.",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi không mong đợi khi gọi OCR service");
                return new OcrServiceResponse
                {
                    Success = false,
                    ErrorMessage = $"Lỗi không mong đợi: {ex.Message}",
                };
            }
        }

        public async Task<long> DownloadReconstructionAsync(
            int documentId,
            string artifactId,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            var path = BuildArtifactPath(documentId, artifactId);
            using var response = await _httpClient.GetAsync(
                path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Reconstruction artifact returned HTTP {response.StatusCode}.",
                    null,
                    response.StatusCode);
            if (response.Content.Headers.ContentType?.MediaType != "application/pdf")
                throw new InvalidDataException("Reconstruction artifact response is not application/pdf.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var signature = new byte[5];
            var read = 0;
            while (read < signature.Length)
            {
                var count = await source.ReadAsync(signature.AsMemory(read), cancellationToken);
                if (count == 0)
                    break;
                read += count;
            }
            if (read != signature.Length || Encoding.ASCII.GetString(signature) != "%PDF-")
                throw new InvalidDataException("Reconstruction artifact is empty or has an invalid PDF signature.");

            await destination.WriteAsync(signature, cancellationToken);
            await source.CopyToAsync(destination, cancellationToken);
            return destination.CanSeek ? destination.Length : read;
        }

        public async Task CleanupReconstructionAsync(
            int documentId,
            string artifactId,
            CancellationToken cancellationToken = default)
        {
            using var response = await _httpClient.DeleteAsync(
                BuildArtifactPath(documentId, artifactId), cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private static string BuildArtifactPath(int documentId, string artifactId) =>
            $"/internal/ocr-artifacts/{documentId}/{Uri.EscapeDataString(artifactId)}";
    }
}
