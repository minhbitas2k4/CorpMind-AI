using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CorpMindAI.Domain.Entities
{
    [Table("documents")]
    public class Document
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [MaxLength(255)]
        [Column("title")]
        public string Title { get; set; } = null!;

        [Required]
        [MaxLength(512)]
        [Column("file_path")]
        public string FilePath { get; set; } = null!;

        [MaxLength(50)]
        [Column("file_type")]
        public string? FileType { get; set; }

        [Column("file_size")]
        public int? FileSize { get; set; }

        [Column("uploaded_by")]
        public int? UploadedById { get; set; }

        [ForeignKey(nameof(UploadedById))]
        public virtual User? UploadedBy { get; set; }

        [Column("department_id")]
        public int DepartmentId { get; set; }

        [ForeignKey(nameof(DepartmentId))]
        public virtual Department Department { get; set; } = null!;

        [Required]
        [MaxLength(20)]
        [Column("status")]
        public string Status { get; set; } = "pending";

        [Column("approved_by")]
        public int? ApprovedById { get; set; }

        [ForeignKey(nameof(ApprovedById))]
        public virtual User? ApprovedBy { get; set; }

        [Column("curriculum_id")]
        public int? CurriculumId { get; set; }

        [ForeignKey(nameof(CurriculumId))]
        public virtual Curriculum? Curriculum { get; set; }

        [Column("is_ocr_required")]
        public bool IsOcrRequired { get; set; } = false;

        [Required]
        [MaxLength(20)]
        [Column("ocr_status")]
        public string OcrStatus { get; set; } = "not_started";

        [MaxLength(512)]
        [Column("raw_text_path")]
        public string? RawTextPath { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Phục vụ mối quan hệ tự tham chiếu kép với bảng xung đột
        public virtual ICollection<AiKnowledgeConflict> SourceConflicts { get; set; } = new List<AiKnowledgeConflict>();
        public virtual ICollection<AiKnowledgeConflict> ConflictingConflicts { get; set; } = new List<AiKnowledgeConflict>();
    }
}
