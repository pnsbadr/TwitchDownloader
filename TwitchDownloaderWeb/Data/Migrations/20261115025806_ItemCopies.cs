using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class ItemCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CopiedFromEntryId",
                table: "VodMarkers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CopiedFromMarkerId",
                table: "VodMarkers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CopiedFromEntryId",
                table: "LibraryEntries",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CopiedFromEntryId",
                table: "VodMarkers");

            migrationBuilder.DropColumn(
                name: "CopiedFromMarkerId",
                table: "VodMarkers");

            migrationBuilder.DropColumn(
                name: "CopiedFromEntryId",
                table: "LibraryEntries");
        }
    }
}
