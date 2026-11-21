using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace truckkuntower.Migrations
{
    /// <inheritdoc />
    public partial class SaveMatchReplays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplayJson",
                table: "Waves",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplayMapJson",
                table: "Matches",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayJson",
                table: "Waves");

            migrationBuilder.DropColumn(
                name: "ReplayMapJson",
                table: "Matches");
        }
    }
}
