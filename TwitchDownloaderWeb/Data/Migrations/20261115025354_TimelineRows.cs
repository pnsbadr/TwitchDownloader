using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimelineRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RowId",
                table: "VodMarkers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RowId",
                table: "LibraryEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemberVods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstOpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberVods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemberVods_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TimelineRows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimelineRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimelineRows_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VodMarkers_RowId",
                table: "VodMarkers",
                column: "RowId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_RowId",
                table: "LibraryEntries",
                column: "RowId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberVods_TeamId_UserId_VodId",
                table: "MemberVods",
                columns: new[] { "TeamId", "UserId", "VodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimelineRows_TeamId_VodId_UserId",
                table: "TimelineRows",
                columns: new[] { "TeamId", "VodId", "UserId" });

            migrationBuilder.AddForeignKey(
                name: "FK_LibraryEntries_TimelineRows_RowId",
                table: "LibraryEntries",
                column: "RowId",
                principalTable: "TimelineRows",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_VodMarkers_TimelineRows_RowId",
                table: "VodMarkers",
                column: "RowId",
                principalTable: "TimelineRows",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LibraryEntries_TimelineRows_RowId",
                table: "LibraryEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_VodMarkers_TimelineRows_RowId",
                table: "VodMarkers");

            migrationBuilder.DropTable(
                name: "MemberVods");

            migrationBuilder.DropTable(
                name: "TimelineRows");

            migrationBuilder.DropIndex(
                name: "IX_VodMarkers_RowId",
                table: "VodMarkers");

            migrationBuilder.DropIndex(
                name: "IX_LibraryEntries_RowId",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "RowId",
                table: "VodMarkers");

            migrationBuilder.DropColumn(
                name: "RowId",
                table: "LibraryEntries");
        }
    }
}
