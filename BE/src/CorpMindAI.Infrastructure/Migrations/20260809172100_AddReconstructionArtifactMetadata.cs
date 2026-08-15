using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CorpMindAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReconstructionArtifactMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "reconstructed_at",
                table: "ocr_results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reconstructed_storage_key",
                table: "ocr_results",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reconstruction_status",
                table: "ocr_results",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reconstructed_at",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "reconstructed_storage_key",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "reconstruction_status",
                table: "ocr_results");
        }
    }
}
