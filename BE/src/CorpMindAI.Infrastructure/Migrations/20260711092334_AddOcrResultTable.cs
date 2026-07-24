using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CorpMindAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrResultTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ocr_results",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    total_pages = table.Column<int>(type: "integer", nullable: false),
                    page_average_confidence = table.Column<double>(type: "double precision", nullable: false),
                    overall_level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    components_json = table.Column<string>(type: "text", nullable: false),
                    validation_errors_json = table.Column<string>(type: "text", nullable: false),
                    raw_text_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ocr_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_ocr_results_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ocr_results_document_id",
                table: "ocr_results",
                column: "document_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ocr_results");
        }
    }
}
