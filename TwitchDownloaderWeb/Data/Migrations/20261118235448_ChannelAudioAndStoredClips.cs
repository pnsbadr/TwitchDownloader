using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchDownloaderWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChannelAudioAndStoredClips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StoreClips",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowSaveAudio",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Audio saving moves from the team to the app Owner, per channel (badrfly 2026-11-18). Nothing changes on the day: a channel is
            // saved automatically where its team had it on, and Save audio now stays allowed where it worked. Stored clips start off for
            // every team (clips added from now on are references; stored files stay); the Owner turns them on per team.
            migrationBuilder.Sql("""
                UPDATE "Channels" SET
                    "WatchMutedAudio" = CASE WHEN "WatchMutedAudio" = 1
                        AND EXISTS (SELECT 1 FROM "Teams" t WHERE t."Id" = "Channels"."TeamId" AND t."AutoSaveAudio" = 1) THEN 1 ELSE 0 END,
                    "AllowSaveAudio" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoreClips",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "AllowSaveAudio",
                table: "Channels");
        }
    }
}
