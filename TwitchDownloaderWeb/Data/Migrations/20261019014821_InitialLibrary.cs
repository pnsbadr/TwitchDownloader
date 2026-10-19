using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelName = table.Column<string>(type: "TEXT", nullable: true),
                    Quality = table.Column<string>(type: "TEXT", nullable: true),
                    TrimMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StartTime = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    EndTime = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    ClipLengthSeconds = table.Column<double>(type: "REAL", nullable: false),
                    VodLengthSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamDateUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ThumbnailUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    GenerationStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    GeneratedFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedFileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    GeneratedExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AddedByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EntryId",
                table: "AuditEvents",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TimestampUtc",
                table: "AuditEvents",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_AddedAtUtc",
                table: "LibraryEntries",
                column: "AddedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_Status",
                table: "LibraryEntries",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_StreamDateUtc",
                table: "LibraryEntries",
                column: "StreamDateUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "LibraryEntries");
        }
    }
}
