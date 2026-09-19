using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CorpMindAI.Domain.Entities;

namespace CorpMindAI.Infrastructure.Data.Entities;

[Table("embedding_index_outbox_messages")]
public sealed class EmbeddingIndexOutboxMessage
{
    [Key, MaxLength(160), Column("id")]
    public string Id { get; set; } = null!;

    [Column("document_id")]
    public int DocumentId { get; set; }

    [ForeignKey(nameof(DocumentId))]
    public Document Document { get; set; } = null!;

    [Required, MaxLength(128), Column("chunking_run_id")]
    public string ChunkingRunId { get; set; } = null!;

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("next_attempt_at")]
    public DateTime NextAttemptAt { get; set; }

    [Column("dispatched_at")]
    public DateTime? DispatchedAt { get; set; }

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [MaxLength(100), Column("hangfire_job_id")]
    public string? HangfireJobId { get; set; }

    [Column("last_error", TypeName = "text")]
    public string? LastError { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
