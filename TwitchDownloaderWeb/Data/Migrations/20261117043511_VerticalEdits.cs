using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class VerticalEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Vertical",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Edits that already have boxes or keyframes on a piece become vertical edits; the rest are horizontal (badrfly 2026-11-17)
            migrationBuilder.Sql("UPDATE Projects SET Vertical = 1 WHERE Content LIKE '%\"layout\":{%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Vertical",
                table: "Projects");
        }
    }
}
