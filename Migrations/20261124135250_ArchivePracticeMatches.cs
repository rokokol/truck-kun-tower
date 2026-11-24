using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace truckkuntower.Migrations
{
    /// <inheritdoc />
    public partial class ArchivePracticeMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Practice",
                table: "Matches",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Practice",
                table: "Matches");
        }
    }
}
