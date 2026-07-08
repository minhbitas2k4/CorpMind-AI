using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CorpMindAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentStorageFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "file_size",
                table: "documents",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "file_path",
                table: "documents",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<string>(
                name: "original_file_name",
                table: "documents",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "storage_key",
                table: "documents",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "original_file_name",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "storage_key",
                table: "documents");

            migrationBuilder.AlterColumn<int>(
                name: "file_size",
                table: "documents",
                type: "integer",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "file_path",
                table: "documents",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);
        }
    }
}
