using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class ErrorReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorReports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Area = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ErrorType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: true),
                    TeamName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StaffId = table.Column<int>(type: "INTEGER", nullable: true),
                    StaffUsername = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Browser = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Os = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    VodId = table.Column<long>(type: "INTEGER", nullable: true),
                    EntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExportId = table.Column<int>(type: "INTEGER", nullable: true),
                    SegmentDownloadId = table.Column<int>(type: "INTEGER", nullable: true),
                    Context = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_CreatedAtUtc",
                table: "ErrorReports",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_Fingerprint",
                table: "ErrorReports",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_TeamId",
                table: "ErrorReports",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_UserId",
                table: "ErrorReports",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorReports");
        }
    }
}
