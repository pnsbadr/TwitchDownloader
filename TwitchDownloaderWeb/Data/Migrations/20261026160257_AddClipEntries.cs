using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClipSlug",
                table: "LibraryEntries",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "LibraryEntries",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Vod"); // existing entries are VOD cuts
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClipSlug",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "LibraryEntries");
        }
    }
}
