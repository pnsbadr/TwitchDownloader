using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class SegmentDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CombineSegmentDownloads",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SegmentExports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Quality = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Combined = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CombinedFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    CombinedFileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentExports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SegmentExports_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SegmentExportItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExportId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    MarkerId = table.Column<int>(type: "INTEGER", nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StartSeconds = table.Column<double>(type: "REAL", nullable: false),
                    EndSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    AudioRestoredSeconds = table.Column<double>(type: "REAL", nullable: true),
                    AudioMutedSeconds = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentExportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SegmentExportItems_SegmentExports_ExportId",
                        column: x => x.ExportId,
                        principalTable: "SegmentExports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SegmentExportItems_ExportId_Position",
                table: "SegmentExportItems",
                columns: new[] { "ExportId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SegmentExports_Status",
                table: "SegmentExports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SegmentExports_TeamId_VodId",
                table: "SegmentExports",
                columns: new[] { "TeamId", "VodId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SegmentExportItems");

            migrationBuilder.DropTable(
                name: "SegmentExports");

            migrationBuilder.DropColumn(
                name: "CombineSegmentDownloads",
                table: "Teams");
        }
    }
}
