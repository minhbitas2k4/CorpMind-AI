using System.Text.Json;
using System.Text.Json.Serialization;

namespace CorpMindAI.Application.DTOs.Document
{
    public class OcrResponseDto
    {
        public int DocumentId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int TotalPages { get; set; }
        public double PageAverageConfidence { get; set; }
        public string OverallLevel { get; set; } = string.Empty;
        public List<OcrComponentDto> Components { get; set; } = new();
        public List<OcrValidationErrorDto> ValidationErrors { get; set; } = new();
        public StructuredDocumentDto? StructuredDocument { get; set; }
        public string? ReconstructedStorageKey { get; set; }
        public string? ReconstructionStatus { get; set; }
        public DateTime? ReconstructedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class OcrComponentDto
    {
        public string ComponentType { get; set; } = string.Empty;
        public string RawText { get; set; } = string.Empty;
        public double AverageConfidence { get; set; }
        public string Level { get; set; } = string.Empty;
        public List<double> Bbox { get; set; } = new();
    }

    public class OcrValidationErrorDto
    {
        public string ComponentType { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public static class OcrContractDeserializer
    {
        public static List<OcrComponentDto> DeserializeComponents(string json) =>
            (JsonSerializer.Deserialize<List<PythonOcrComponentDto>>(json) ?? new())
            .Select(component => new OcrComponentDto
            {
                ComponentType = component.ComponentType,
                RawText = component.RawText,
                AverageConfidence = component.AverageConfidence,
                Level = component.Level,
                Bbox = component.Bbox,
            }).ToList();

        public static List<OcrValidationErrorDto> DeserializeValidationErrors(string json) =>
            (JsonSerializer.Deserialize<List<PythonOcrValidationErrorDto>>(json) ?? new())
            .Select(error => new OcrValidationErrorDto
            {
                ComponentType = error.ComponentType,
                Text = error.Text,
                Confidence = error.Confidence,
                Reason = error.Reason,
            }).ToList();

        private class PythonOcrComponentDto
        {
            [JsonPropertyName("component_type")]
            public string ComponentType { get; set; } = string.Empty;
            [JsonPropertyName("raw_text")]
            public string RawText { get; set; } = string.Empty;
            [JsonPropertyName("average_confidence")]
            public double AverageConfidence { get; set; }
            [JsonPropertyName("level")]
            public string Level { get; set; } = string.Empty;
            [JsonPropertyName("bbox")]
            public List<double> Bbox { get; set; } = new();
        }

        private class PythonOcrValidationErrorDto
        {
            [JsonPropertyName("component_type")]
            public string ComponentType { get; set; } = string.Empty;
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;
            [JsonPropertyName("confidence")]
            public double Confidence { get; set; }
            [JsonPropertyName("reason")]
            public string Reason { get; set; } = string.Empty;
        }
    }

    public class StructuredDocumentDto
    {
        [JsonPropertyName("schema_version")]
        public string SchemaVersion { get; set; } = string.Empty;
        [JsonPropertyName("document_id")]
        public string DocumentId { get; set; } = string.Empty;
        [JsonPropertyName("total_pages")]
        public int TotalPages { get; set; }
        [JsonPropertyName("pages")]
        public List<StructuredPageDto> Pages { get; set; } = new();
    }

    public class StructuredPageDto
    {
        [JsonPropertyName("page_number")]
        public int PageNumber { get; set; }
        [JsonPropertyName("width")]
        public int Width { get; set; }
        [JsonPropertyName("height")]
        public int Height { get; set; }
        [JsonPropertyName("render_dpi")]
        public int RenderDpi { get; set; }
        [JsonPropertyName("coordinate_system")]
        public CoordinateSystemDto CoordinateSystem { get; set; } = new();
        [JsonPropertyName("components")]
        public List<StructuredComponentDto> Components { get; set; } = new();
    }

    public class CoordinateSystemDto
    {
        [JsonPropertyName("origin")]
        public string Origin { get; set; } = string.Empty;
        [JsonPropertyName("bbox_format")]
        public string BboxFormat { get; set; } = string.Empty;
        [JsonPropertyName("unit")]
        public string Unit { get; set; } = string.Empty;
    }

    public class StructuredComponentDto
    {
        [JsonPropertyName("component_id")]
        public string ComponentId { get; set; } = string.Empty;
        [JsonPropertyName("document_id")]
        public string DocumentId { get; set; } = string.Empty;
        [JsonPropertyName("page_number")]
        public int PageNumber { get; set; }
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        [JsonPropertyName("bbox")]
        public List<double> Bbox { get; set; } = new();
        [JsonPropertyName("normalized_bbox")]
        public List<double> NormalizedBbox { get; set; } = new();
        [JsonPropertyName("reading_order")]
        public int ReadingOrder { get; set; }
        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
        [JsonPropertyName("lines")]
        public List<StructuredLineDto> Lines { get; set; } = new();
        [JsonPropertyName("metadata")]
        public ComponentMetadataDto Metadata { get; set; } = new();
        [JsonPropertyName("rows")]
        public JsonElement? Rows { get; set; }
        [JsonPropertyName("cells")]
        public JsonElement? Cells { get; set; }
        [JsonPropertyName("asset")]
        public AssetMetadataDto? Asset { get; set; }
        [JsonPropertyName("caption")]
        public FigureCaptionDto? Caption { get; set; }
    }

    public class StructuredLineDto
    {
        [JsonPropertyName("line_id")]
        public string LineId { get; set; } = string.Empty;
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
        [JsonPropertyName("bbox")]
        public List<List<double>> Bbox { get; set; } = new();
        [JsonPropertyName("normalized_bbox")]
        public List<List<double>> NormalizedBbox { get; set; } = new();
    }

    public class ComponentMetadataDto
    {
        [JsonPropertyName("source_layout_type")]
        public string SourceLayoutType { get; set; } = string.Empty;
        [JsonPropertyName("synthetic_region")]
        public bool SyntheticRegion { get; set; }
        [JsonPropertyName("asset_errors")]
        public List<string> AssetErrors { get; set; } = new();
    }

    public class AssetMetadataDto
    {
        [JsonPropertyName("asset_id")]
        public string AssetId { get; set; } = string.Empty;
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
        [JsonPropertyName("width")]
        public int Width { get; set; }
        [JsonPropertyName("height")]
        public int Height { get; set; }
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
    }

    public class FigureCaptionDto
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
        [JsonPropertyName("lines")]
        public List<StructuredLineDto> Lines { get; set; } = new();
    }
}
