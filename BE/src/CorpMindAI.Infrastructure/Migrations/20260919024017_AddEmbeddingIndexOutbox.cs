using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CorpMindAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingIndexOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "embedding_index_outbox_messages",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    chunking_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    hangfire_job_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_embedding_index_outbox_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_embedding_index_outbox_messages_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_embedding_index_outbox_messages_completed_at_dispatched_at_~",
                table: "embedding_index_outbox_messages",
                columns: new[] { "completed_at", "dispatched_at", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_embedding_index_outbox_messages_document_id_chunking_run_id",
                table: "embedding_index_outbox_messages",
                columns: new[] { "document_id", "chunking_run_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedding_index_outbox_messages");
        }
    }
}
