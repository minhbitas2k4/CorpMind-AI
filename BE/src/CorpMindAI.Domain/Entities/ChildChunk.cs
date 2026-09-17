using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CorpMindAI.Domain.Entities;

[Table("child_chunks")]
public class ChildChunk
{
    [Key]
    [Column("id")]
    [MaxLength(256)]
    public string Id { get; set; } = null!;

    [Required]
    [MaxLength(256)]
    [Column("parent_chunk_id")]
    public string ParentChunkId { get; set; } = null!;

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

    [Required]
    [Column("section_path", TypeName = "jsonb")]
    public List<string> SectionPath { get; set; } = new();

    [Required]
    [Column("raw_content")]
    public string RawContent { get; set; } = null!;

    [Required]
    [Column("contextualized_content")]
    public string ContextualizedContent { get; set; } = null!;

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

    [ForeignKey(nameof(ParentChunkId))]
    public virtual ParentChunk ParentChunk { get; set; } = null!;

    public virtual ChunkingRun ChunkingRun { get; set; } = null!;
}
