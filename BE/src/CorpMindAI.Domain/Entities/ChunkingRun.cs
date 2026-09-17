using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CorpMindAI.Domain.Entities;

[Table("chunking_runs")]
public class ChunkingRun
{
    [Key]
    [Column("id")]
    [MaxLength(128)]
    public string Id { get; set; } = null!;

    [Column("document_id")]
    public int DocumentId { get; set; }

    [Column("department_id")]
    public int DepartmentId { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("source_content_hash")]
    public string SourceContentHash { get; set; } = null!;

    [Required]
    [MaxLength(32)]
    [Column("source_schema_version")]
    public string SourceSchemaVersion { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    [Column("chunker_version")]
    public string ChunkerVersion { get; set; } = null!;

    [Required]
    [Column("configuration_json", TypeName = "jsonb")]
    public string ConfigurationJson { get; set; } = "{}";

    [Required]
    [MaxLength(20)]
    [Column("status")]
    public string Status { get; set; } = ChunkingRunStatuses.NotStarted;

    [Column("started_at")]
    public DateTime? StartedAt { get; set; }

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Required]
    [Column("validation_warnings_json", TypeName = "jsonb")]
    public string ValidationWarningsJson { get; set; } = "[]";

    [Required]
    [Column("normalization_notices_json", TypeName = "jsonb")]
    public string NormalizationNoticesJson { get; set; } = "[]";

    [Column("source_coverage")]
    public double? SourceCoverage { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(DocumentId))]
    public virtual Document Document { get; set; } = null!;

    public virtual ICollection<ParentChunk> ParentChunks { get; set; } = new List<ParentChunk>();
}
