using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class CutOutParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cuts",
                table: "VodMarkers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cuts",
                table: "VideoSegments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cuts",
                table: "SegmentExportItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cuts",
                table: "LibraryEntries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cuts",
                table: "VodMarkers");

            migrationBuilder.DropColumn(
                name: "Cuts",
                table: "VideoSegments");

            migrationBuilder.DropColumn(
                name: "Cuts",
                table: "SegmentExportItems");

            migrationBuilder.DropColumn(
                name: "Cuts",
                table: "LibraryEntries");
        }
    }
}
