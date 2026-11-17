using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectParents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EntryId",
                table: "Projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VideoId",
                table: "Projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VodId",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_EntryId",
                table: "Projects",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TeamId_VodId",
                table: "Projects",
                columns: new[] { "TeamId", "VodId" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_VideoId",
                table: "Projects",
                column: "VideoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_LibraryEntries_EntryId",
                table: "Projects",
                column: "EntryId",
                principalTable: "LibraryEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Videos_VideoId",
                table: "Projects",
                column: "VideoId",
                principalTable: "Videos",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_LibraryEntries_EntryId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Videos_VideoId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_EntryId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_TeamId_VodId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_VideoId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EntryId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "VideoId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "VodId",
                table: "Projects");
        }
    }
}
