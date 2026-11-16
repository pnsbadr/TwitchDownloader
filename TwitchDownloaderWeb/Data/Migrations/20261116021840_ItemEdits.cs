using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class ItemEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileCuts",
                table: "LibraryEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ItemApplies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    MarkerId = table.Column<int>(type: "INTEGER", nullable: true),
                    VideoId = table.Column<int>(type: "INTEGER", nullable: true),
                    EditId = table.Column<int>(type: "INTEGER", nullable: true),
                    EditTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CutsBefore = table.Column<string>(type: "TEXT", nullable: true),
                    CutsAfter = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemApplies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemApplies_LibraryEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "LibraryEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemApplies_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemApplies_Videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "Videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemApplies_VodMarkers_MarkerId",
                        column: x => x.MarkerId,
                        principalTable: "VodMarkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemEdits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    MarkerId = table.Column<int>(type: "INTEGER", nullable: true),
                    VideoId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Cuts = table.Column<string>(type: "TEXT", nullable: true),
                    Parts = table.Column<string>(type: "TEXT", nullable: true),
                    Layout = table.Column<string>(type: "TEXT", maxLength: 100000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemEdits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemEdits_LibraryEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "LibraryEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemEdits_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemEdits_Videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "Videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemEdits_VodMarkers_MarkerId",
                        column: x => x.MarkerId,
                        principalTable: "VodMarkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemApplies_EntryId",
                table: "ItemApplies",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemApplies_MarkerId",
                table: "ItemApplies",
                column: "MarkerId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemApplies_TeamId",
                table: "ItemApplies",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemApplies_VideoId",
                table: "ItemApplies",
                column: "VideoId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemEdits_EntryId",
                table: "ItemEdits",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemEdits_MarkerId",
                table: "ItemEdits",
                column: "MarkerId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemEdits_TeamId_VodId",
                table: "ItemEdits",
                columns: new[] { "TeamId", "VodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemEdits_VideoId",
                table: "ItemEdits",
                column: "VideoId");

            // Files made before this hold their range without its cut-out parts; newer files hold the whole range
            migrationBuilder.Sql("UPDATE LibraryEntries SET FileCuts = Cuts WHERE Cuts IS NOT NULL AND (FilePath IS NOT NULL OR GeneratedFilePath IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemApplies");

            migrationBuilder.DropTable(
                name: "ItemEdits");

            migrationBuilder.DropColumn(
                name: "FileCuts",
                table: "LibraryEntries");
        }
    }
}
