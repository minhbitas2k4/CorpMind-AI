using System.Net.Http.Json;
using System.Text.Json;
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
                "Gọi OCR service: documentId={DocumentId}, filePath={FilePath}",
                documentId,
                filePath);

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
                };

                if (root.TryGetProperty("components", out var components))
                {
                    result.ComponentsJson = components.GetRawText();
                }

                if (root.TryGetProperty("validation_errors", out var validationErrors))
                {
                    result.ValidationErrorsJson = validationErrors.GetRawText();
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
    }
}
