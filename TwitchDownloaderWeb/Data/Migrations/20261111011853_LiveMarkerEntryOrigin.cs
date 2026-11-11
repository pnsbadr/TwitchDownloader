using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class LiveMarkerEntryOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MadeFromMarkers",
                table: "LibraryEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Entries the live markers feature made before this column existed: the ones following now, and the ones it added
            // (its audit text) that were re-cut by hand since
            migrationBuilder.Sql(
                "UPDATE LibraryEntries SET MadeFromMarkers = 1 WHERE FollowsMarkers = 1 OR Id IN " +
                "(SELECT EntryId FROM AuditEvents WHERE Action = 'entry.added' AND Details LIKE '%from markers on the live stream%')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MadeFromMarkers",
                table: "LibraryEntries");
        }
    }
}
