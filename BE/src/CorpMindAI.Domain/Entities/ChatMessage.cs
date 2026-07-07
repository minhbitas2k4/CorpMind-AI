using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CorpMindAI.Domain.Entities
{
    [Table("chat_messages")]
    public class ChatMessage
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("session_id")]
        public int SessionId { get; set; }

        [ForeignKey(nameof(SessionId))]
        public virtual ChatSession ChatSession { get; set; } = null!;

        [Required]
        [MaxLength(20)]
        [Column("role")]
        public string Role { get; set; } = null!; 

        [Required]
        [Column("content")]
        public string Content { get; set; } = null!;

        [Column("prompt_tokens")]
        public int PromptTokens { get; set; } = 0;

        [Column("completion_tokens")]
        public int CompletionTokens { get; set; } = 0;

        [Column("total_tokens")]
        public int TotalTokens { get; set; } = 0;

        [MaxLength(50)]
        [Column("model_used")]
        public string? ModelUsed { get; set; }

        [Column("cost_usd", TypeName = "numeric(10, 6)")]
        public decimal CostUsd { get; set; } = 0.000000m;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
