using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CorpMindAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddParentChildChunking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chunking_runs",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    department_id = table.Column<int>(type: "integer", nullable: false),
                    source_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_schema_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    chunker_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunking_runs", x => x.id);
                    table.UniqueConstraint("AK_chunking_runs_id_document_id", x => new { x.id, x.document_id });
                    table.ForeignKey(
                        name: "FK_chunking_runs_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "parent_chunks",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    chunking_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    department_id = table.Column<int>(type: "integer", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    section_path = table.Column<string>(type: "jsonb", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    page_from = table.Column<int>(type: "integer", nullable: false),
                    page_to = table.Column<int>(type: "integer", nullable: false),
                    component_ids = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_atomic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parent_chunks", x => x.id);
                    table.UniqueConstraint("AK_parent_chunks_id_chunking_run_id_document_id", x => new { x.id, x.chunking_run_id, x.document_id });
                    table.ForeignKey(
                        name: "FK_parent_chunks_chunking_runs_chunking_run_id_document_id",
                        columns: x => new { x.chunking_run_id, x.document_id },
                        principalTable: "chunking_runs",
                        principalColumns: new[] { "id", "document_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "child_chunks",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    parent_chunk_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    chunking_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    department_id = table.Column<int>(type: "integer", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    section_path = table.Column<string>(type: "jsonb", nullable: false),
                    raw_content = table.Column<string>(type: "text", nullable: false),
                    contextualized_content = table.Column<string>(type: "text", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    page_from = table.Column<int>(type: "integer", nullable: false),
                    page_to = table.Column<int>(type: "integer", nullable: false),
                    component_ids = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_atomic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_child_chunks", x => x.id);
                    table.ForeignKey(
                        name: "FK_child_chunks_chunking_runs_chunking_run_id_document_id",
                        columns: x => new { x.chunking_run_id, x.document_id },
                        principalTable: "chunking_runs",
                        principalColumns: new[] { "id", "document_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_child_chunks_parent_chunks_parent_chunk_id_chunking_run_id_~",
                        columns: x => new { x.parent_chunk_id, x.chunking_run_id, x.document_id },
                        principalTable: "parent_chunks",
                        principalColumns: new[] { "id", "chunking_run_id", "document_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_child_chunks_chunking_run_id",
                table: "child_chunks",
                column: "chunking_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_child_chunks_chunking_run_id_document_id",
                table: "child_chunks",
                columns: new[] { "chunking_run_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_child_chunks_document_id",
                table: "child_chunks",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_child_chunks_parent_chunk_id",
                table: "child_chunks",
                column: "parent_chunk_id");

            migrationBuilder.CreateIndex(
                name: "IX_child_chunks_parent_chunk_id_chunking_run_id_document_id",
                table: "child_chunks",
                columns: new[] { "parent_chunk_id", "chunking_run_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_child_chunks_parent_chunk_id_ordinal",
                table: "child_chunks",
                columns: new[] { "parent_chunk_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chunking_runs_document_id",
                table: "chunking_runs",
                column: "document_id",
                unique: true,
                filter: "\"is_active\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_parent_chunks_chunking_run_id",
                table: "parent_chunks",
                column: "chunking_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_parent_chunks_chunking_run_id_document_id",
                table: "parent_chunks",
                columns: new[] { "chunking_run_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_parent_chunks_chunking_run_id_ordinal",
                table: "parent_chunks",
                columns: new[] { "chunking_run_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parent_chunks_document_id",
                table: "parent_chunks",
                column: "document_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "child_chunks");

            migrationBuilder.DropTable(
                name: "parent_chunks");

            migrationBuilder.DropTable(
                name: "chunking_runs");
        }
    }
}
