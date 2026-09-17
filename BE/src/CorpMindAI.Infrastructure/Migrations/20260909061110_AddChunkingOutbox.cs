using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CorpMindAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkingOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chunking_outbox_messages",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    ocr_payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    hangfire_job_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunking_outbox_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_chunking_outbox_messages_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chunking_outbox_messages_dispatched_at_next_attempt_at",
                table: "chunking_outbox_messages",
                columns: new[] { "dispatched_at", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_chunking_outbox_messages_document_id_ocr_payload_hash",
                table: "chunking_outbox_messages",
                columns: new[] { "document_id", "ocr_payload_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunking_outbox_messages");
        }
    }
}
