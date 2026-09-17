using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CorpMindAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkingValidationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalization_notices_json",
                table: "chunking_runs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<double>(
                name: "source_coverage",
                table: "chunking_runs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_warnings_json",
                table: "chunking_runs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "normalization_notices_json",
                table: "chunking_runs");

            migrationBuilder.DropColumn(
                name: "source_coverage",
                table: "chunking_runs");

            migrationBuilder.DropColumn(
                name: "validation_warnings_json",
                table: "chunking_runs");
        }
    }
}
