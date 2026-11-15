using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExportBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExportBatches_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExportItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Quality = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    StartSeconds = table.Column<double>(type: "REAL", nullable: false),
                    EndSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Cuts = table.Column<string>(type: "TEXT", nullable: true),
                    TrimMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    MarkerId = table.Column<int>(type: "INTEGER", nullable: true),
                    VideoId = table.Column<int>(type: "INTEGER", nullable: true),
                    RenderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Parts = table.Column<string>(type: "TEXT", nullable: true),
                    Joined = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    PartsDone = table.Column<int>(type: "INTEGER", nullable: false),
                    PartsTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    AudioRestoredSeconds = table.Column<double>(type: "REAL", nullable: true),
                    AudioMutedSeconds = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExportItems_ExportBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ExportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExportBatches_Status",
                table: "ExportBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ExportBatches_TeamId_CreatedByUserId",
                table: "ExportBatches",
                columns: new[] { "TeamId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExportItems_BatchId_Position",
                table: "ExportItems",
                columns: new[] { "BatchId", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportItems");

            migrationBuilder.DropTable(
                name: "ExportBatches");
        }
    }
}
