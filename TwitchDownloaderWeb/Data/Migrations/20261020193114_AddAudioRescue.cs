using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioRescue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AudioRestoredSeconds",
                table: "LibraryEntries",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AudioRescues",
                columns: table => new
                {
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelLogin = table.Column<string>(type: "TEXT", maxLength: 25, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StreamStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    SourceQuality = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextCheckAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GrabCompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PruneAfterUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SegmentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SavedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MutedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnrecoverableCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StoredBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioRescues", x => x.VodId);
                });

            migrationBuilder.CreateTable(
                name: "RescuedAudioSegments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OffsetSeconds = table.Column<double>(type: "REAL", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    WasMuted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Unrecoverable = table.Column<bool>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstAudioPts = table.Column<long>(type: "INTEGER", nullable: true),
                    SavedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RescuedAudioSegments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudioRescues_Status_NextCheckAtUtc",
                table: "AudioRescues",
                columns: new[] { "Status", "NextCheckAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RescuedAudioSegments_VodId_Key",
                table: "RescuedAudioSegments",
                columns: new[] { "VodId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioRescues");

            migrationBuilder.DropTable(
                name: "RescuedAudioSegments");

            migrationBuilder.DropColumn(
                name: "AudioRestoredSeconds",
                table: "LibraryEntries");
        }
    }
}
