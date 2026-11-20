using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExportSeparate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Separate",
                table: "ExportItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Separate",
                table: "ExportItems");
        }
    }
}
