using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimelineRowsBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing markers go on their creator's first video row on the VOD, existing VOD/clip entries on their adder's (or, for entries
            // made before accounts, the team owner's) first clip row. Members get the other kind of row when they next open the VOD.
            migrationBuilder.Sql("""
                INSERT INTO TimelineRows (TeamId, VodId, UserId, Kind, Position, Name, CreatedAtUtc)
                SELECT DISTINCT TeamId, VodId, CreatedByUserId, 'Video', 0, NULL, strftime('%Y-%m-%d %H:%M:%S', 'now')
                FROM VodMarkers WHERE CreatedByUserId > 0;

                UPDATE VodMarkers SET RowId = (
                    SELECT r.Id FROM TimelineRows r
                    WHERE r.TeamId = VodMarkers.TeamId AND r.VodId = VodMarkers.VodId AND r.UserId = VodMarkers.CreatedByUserId AND r.Kind = 'Video')
                WHERE CreatedByUserId > 0;

                INSERT INTO TimelineRows (TeamId, VodId, UserId, Kind, Position, Name, CreatedAtUtc)
                SELECT DISTINCT o.TeamId, o.VodId, o.Owner, 'Clip',
                    CASE WHEN EXISTS (SELECT 1 FROM TimelineRows v WHERE v.TeamId = o.TeamId AND v.VodId = o.VodId AND v.UserId = o.Owner) THEN 1 ELSE 0 END,
                    NULL, strftime('%Y-%m-%d %H:%M:%S', 'now')
                FROM (
                    SELECT e.TeamId, e.VodId,
                        COALESCE(CAST(e.AddedByUserId AS INTEGER), (SELECT t.OwnerUserId FROM Teams t WHERE t.Id = e.TeamId)) AS Owner
                    FROM LibraryEntries e WHERE e.VodId > 0
                ) o
                WHERE o.Owner IS NOT NULL AND o.Owner > 0;

                UPDATE LibraryEntries SET RowId = (
                    SELECT r.Id FROM TimelineRows r
                    WHERE r.TeamId = LibraryEntries.TeamId AND r.VodId = LibraryEntries.VodId AND r.Kind = 'Clip'
                      AND r.UserId = COALESCE(CAST(LibraryEntries.AddedByUserId AS INTEGER),
                                              (SELECT t.OwnerUserId FROM Teams t WHERE t.Id = LibraryEntries.TeamId)))
                WHERE VodId > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rows are dropped with their table by the TimelineRows migration's Down
        }
    }
}
