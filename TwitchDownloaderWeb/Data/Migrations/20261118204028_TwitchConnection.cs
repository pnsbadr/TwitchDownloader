using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class TwitchConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TwitchConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TwitchUserId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TwitchLogin = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConnectedByStaffId = table.Column<int>(type: "INTEGER", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TwitchConnections", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TwitchConnections");
        }
    }
}
