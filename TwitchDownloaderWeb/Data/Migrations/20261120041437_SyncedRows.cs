using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncedRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Videos_TimelineRows_RowId",
                table: "Videos");

            migrationBuilder.AlterColumn<int>(
                name: "RowId",
                table: "Videos",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<long>(
                name: "ShownOnVodId",
                table: "TimelineRows",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SyncBaseSeconds",
                table: "TimelineRows",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimelineRows_TeamId_ShownOnVodId",
                table: "TimelineRows",
                columns: new[] { "TeamId", "ShownOnVodId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Videos_TimelineRows_RowId",
                table: "Videos",
                column: "RowId",
                principalTable: "TimelineRows",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Videos_TimelineRows_RowId",
                table: "Videos");

            migrationBuilder.DropIndex(
                name: "IX_TimelineRows_TeamId_ShownOnVodId",
                table: "TimelineRows");

            migrationBuilder.DropColumn(
                name: "ShownOnVodId",
                table: "TimelineRows");

            migrationBuilder.DropColumn(
                name: "SyncBaseSeconds",
                table: "TimelineRows");

            migrationBuilder.AlterColumn<int>(
                name: "RowId",
                table: "Videos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Videos_TimelineRows_RowId",
                table: "Videos",
                column: "RowId",
                principalTable: "TimelineRows",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
