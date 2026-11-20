using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClipColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "LibraryEntries",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Color",
                table: "LibraryEntries");
        }
    }
}
