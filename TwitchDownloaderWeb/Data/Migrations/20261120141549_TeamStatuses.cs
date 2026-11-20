using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class TeamStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TeamStatusId",
                table: "Videos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusesMadeAtUtc",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeamStatusId",
                table: "LibraryEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TeamStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    SetWhen = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    HideFromLists = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamStatuses_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamTags_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EntryTags",
                columns: table => new
                {
                    EntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryTags", x => new { x.EntryId, x.TagId });
                    table.ForeignKey(
                        name: "FK_EntryTags_LibraryEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "LibraryEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EntryTags_TeamTags_TagId",
                        column: x => x.TagId,
                        principalTable: "TeamTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VideoTags",
                columns: table => new
                {
                    VideoId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoTags", x => new { x.VideoId, x.TagId });
                    table.ForeignKey(
                        name: "FK_VideoTags_TeamTags_TagId",
                        column: x => x.TagId,
                        principalTable: "TeamTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VideoTags_Videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "Videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Videos_TeamStatusId",
                table: "Videos",
                column: "TeamStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_TeamStatusId",
                table: "LibraryEntries",
                column: "TeamStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_EntryTags_TagId",
                table: "EntryTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamStatuses_TeamId",
                table: "TeamStatuses",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamTags_TeamId_Name",
                table: "TeamTags",
                columns: new[] { "TeamId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoTags_TagId",
                table: "VideoTags",
                column: "TagId");

            migrationBuilder.AddForeignKey(
                name: "FK_LibraryEntries_TeamStatuses_TeamStatusId",
                table: "LibraryEntries",
                column: "TeamStatusId",
                principalTable: "TeamStatuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Videos_TeamStatuses_TeamStatusId",
                table: "Videos",
                column: "TeamStatusId",
                principalTable: "TeamStatuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LibraryEntries_TeamStatuses_TeamStatusId",
                table: "LibraryEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_Videos_TeamStatuses_TeamStatusId",
                table: "Videos");

            migrationBuilder.DropTable(
                name: "EntryTags");

            migrationBuilder.DropTable(
                name: "TeamStatuses");

            migrationBuilder.DropTable(
                name: "VideoTags");

            migrationBuilder.DropTable(
                name: "TeamTags");

            migrationBuilder.DropIndex(
                name: "IX_Videos_TeamStatusId",
                table: "Videos");

            migrationBuilder.DropIndex(
                name: "IX_LibraryEntries_TeamStatusId",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "TeamStatusId",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "StatusesMadeAtUtc",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "TeamStatusId",
                table: "LibraryEntries");
        }
    }
}
