using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CorpMindAI.Domain.Entities
{
    [Table("ai_knowledge_conflicts")]
    public class AiKnowledgeConflict
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("source_document_id")]
        public int SourceDocumentId { get; set; }
        public virtual Document SourceDocument { get; set; } = null!;

        [Column("conflicting_document_id")]
        public int ConflictingDocumentId { get; set; }
        public virtual Document ConflictingDocument { get; set; } = null!;

        [Required]
        [Column("conflict_description")]
        public string ConflictDescription { get; set; } = null!;

        [Required]
        [MaxLength(20)]
        [Column("status")]
        public string Status { get; set; } = "unresolved";

        [Column("resolved_by")]
        public int? ResolvedById { get; set; }

        [ForeignKey(nameof(ResolvedById))]
        public virtual User? ResolvedBy { get; set; }

        [Column("resolution_note")]
        public string? ResolutionNote { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("resolved_at")]
        public DateTime? ResolvedAt { get; set; }
    }
}
