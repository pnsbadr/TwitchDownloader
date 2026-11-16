using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntryAndAuditIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_TeamId_VodId",
                table: "LibraryEntries",
                columns: new[] { "TeamId", "VodId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TeamId_TimestampUtc",
                table: "AuditEvents",
                columns: new[] { "TeamId", "TimestampUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LibraryEntries_TeamId_VodId",
                table: "LibraryEntries");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_TeamId_TimestampUtc",
                table: "AuditEvents");
        }
    }
}
