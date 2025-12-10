using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeTubeServer.Data.Migrations;

public partial class InitialCreateWithWebSubNotification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Channels",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ChannelId = table.Column<string>(nullable: false),
                UploadsPlaylistId = table.Column<string>(nullable: true),
                TopicUrl = table.Column<string>(nullable: false),
                HubSecret = table.Column<string>(nullable: true),
                LeaseExpiresAt = table.Column<DateTimeOffset>(nullable: true),
                LastSeenPublishedAt = table.Column<DateTimeOffset>(nullable: true),
                LastWebSubNotification = table.Column<DateTimeOffset>(nullable: true),
                ChannelName = table.Column<string>(nullable: true),
                ChannelThumbnailUrl = table.Column<string>(nullable: true),
                MetadataLastUpdated = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Channels", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                AppUserId = table.Column<string>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Videos",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                VideoId = table.Column<string>(nullable: false),
                ChannelId = table.Column<int>(nullable: false),
                PublishedAt = table.Column<DateTimeOffset>(nullable: false),
                Title = table.Column<string>(nullable: true),
                Description = table.Column<string>(nullable: true),
                ThumbnailUrl = table.Column<string>(nullable: true),
                Duration = table.Column<TimeSpan>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Videos", x => x.Id);
                table.ForeignKey(
                    name: "FK_Videos_Channels_ChannelId",
                    column: x => x.ChannelId,
                    principalTable: "Channels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserChannels",
            columns: table => new
            {
                UserId = table.Column<int>(nullable: false),
                ChannelId = table.Column<int>(nullable: false),
                UserLastSeenPublishedAt = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserChannels", x => new { x.UserId, x.ChannelId });
                table.ForeignKey(
                    name: "FK_UserChannels_Channels_ChannelId",
                    column: x => x.ChannelId,
                    principalTable: "Channels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_UserChannels_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Channels_ChannelId",
            table: "Channels",
            column: "ChannelId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Channels_LeaseExpiresAt",
            table: "Channels",
            column: "LeaseExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_Channels_TopicUrl",
            table: "Channels",
            column: "TopicUrl",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserChannels_User_Channel",
            table: "UserChannels",
            columns: new[] { "UserId", "ChannelId" });

        migrationBuilder.CreateIndex(
            name: "IX_UserChannels_ChannelId",
            table: "UserChannels",
            column: "ChannelId");

        migrationBuilder.CreateIndex(
            name: "IX_Users_AppUserId",
            table: "Users",
            column: "AppUserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Videos_Channel_Published",
            table: "Videos",
            columns: new[] { "ChannelId", "PublishedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Videos_VideoId",
            table: "Videos",
            column: "VideoId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UserChannels");

        migrationBuilder.DropTable(
            name: "Videos");

        migrationBuilder.DropTable(
            name: "Users");

        migrationBuilder.DropTable(
            name: "Channels");
    }
}
