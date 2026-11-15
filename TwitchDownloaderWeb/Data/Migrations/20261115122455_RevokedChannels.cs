using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class RevokedChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RevokedChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Login = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TwitchUserId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevokedChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevokedChannels_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HiddenVods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    VodId = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedChannelId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiddenVods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HiddenVods_RevokedChannels_RevokedChannelId",
                        column: x => x.RevokedChannelId,
                        principalTable: "RevokedChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HiddenVods_RevokedChannelId",
                table: "HiddenVods",
                column: "RevokedChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_HiddenVods_TeamId_VodId",
                table: "HiddenVods",
                columns: new[] { "TeamId", "VodId" });

            migrationBuilder.CreateIndex(
                name: "IX_RevokedChannels_TeamId",
                table: "RevokedChannels",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HiddenVods");

            migrationBuilder.DropTable(
                name: "RevokedChannels");
        }
    }
}
