using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CorpMindAI.Domain.Entities;

[Table("parent_chunks")]
public class ParentChunk
{
    [Key]
    [Column("id")]
    [MaxLength(256)]
    public string Id { get; set; } = null!;

    [Required]
    [MaxLength(128)]
    [Column("chunking_run_id")]
    public string ChunkingRunId { get; set; } = null!;

    [Column("document_id")]
    public int DocumentId { get; set; }

    [Column("department_id")]
    public int DepartmentId { get; set; }

    [Column("ordinal")]
    public int Ordinal { get; set; }

    [MaxLength(255)]
    [Column("title")]
    public string? Title { get; set; }

    [Required]
    [Column("section_path", TypeName = "jsonb")]
    public List<string> SectionPath { get; set; } = new();

    [Required]
    [Column("content")]
    public string Content { get; set; } = null!;

    [Column("token_count")]
    public int TokenCount { get; set; }

    [Column("page_from")]
    public int PageFrom { get; set; }

    [Column("page_to")]
    public int PageTo { get; set; }

    [Required]
    [Column("component_ids", TypeName = "jsonb")]
    public List<string> ComponentIds { get; set; } = new();

    [Required]
    [MaxLength(64)]
    [Column("content_hash")]
    public string ContentHash { get; set; } = null!;

    [Column("is_atomic")]
    public bool IsAtomic { get; set; }

    [ForeignKey(nameof(ChunkingRunId))]
    public virtual ChunkingRun ChunkingRun { get; set; } = null!;

    public virtual ICollection<ChildChunk> ChildChunks { get; set; } = new List<ChildChunk>();
}
