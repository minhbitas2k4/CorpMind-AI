using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CorpMindAI.Domain.Entities;

namespace CorpMindAI.Infrastructure.Data.Entities;

[Table("chunking_outbox_messages")]
public sealed class ChunkingOutboxMessage
{
    [Key, MaxLength(96), Column("id")]
    public string Id { get; set; } = null!;

    [Column("document_id")]
    public int DocumentId { get; set; }

    [ForeignKey(nameof(DocumentId))]
    public Document Document { get; set; } = null!;

    [Required, MaxLength(64), Column("ocr_payload_hash")]
    public string OcrPayloadHash { get; set; } = null!;

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("next_attempt_at")]
    public DateTime NextAttemptAt { get; set; }

    [Column("dispatched_at")]
    public DateTime? DispatchedAt { get; set; }

    [MaxLength(100), Column("hangfire_job_id")]
    public string? HangfireJobId { get; set; }

    [Column("last_error", TypeName = "text")]
    public string? LastError { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
