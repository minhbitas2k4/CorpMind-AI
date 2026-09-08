using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CorpMindAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTokenVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "token_version",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "token_version",
                table: "users");
        }
    }
}
