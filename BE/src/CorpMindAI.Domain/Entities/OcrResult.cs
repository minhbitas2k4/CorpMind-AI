using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CorpMindAI.Domain.Entities
{
    [Table("ocr_results")]
    public class OcrResult
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("document_id")]
        public int DocumentId { get; set; }

        [ForeignKey(nameof(DocumentId))]
        public virtual Document Document { get; set; } = null!;

        [Column("total_pages")]
        public int TotalPages { get; set; }

        [Column("page_average_confidence")]
        public double PageAverageConfidence { get; set; }

        [MaxLength(20)]
        [Column("overall_level")]
        public string OverallLevel { get; set; } = string.Empty;

        [Column("components_json", TypeName = "text")]
        public string ComponentsJson { get; set; } = "[]";

        [Column("validation_errors_json", TypeName = "text")]
        public string ValidationErrorsJson { get; set; } = "[]";

        [MaxLength(512)]
        [Column("raw_text_path")]
        public string? RawTextPath { get; set; }

        [Column("error_message", TypeName = "text")]
        public string? ErrorMessage { get; set; }

        [Required]
        [MaxLength(20)]
        [Column("status")]
        public string Status { get; set; } = "processing";

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
