using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeTubeServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWebSubReliabilityTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastWebSubNotification",
                table: "Channels",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastWebSubNotification",
                table: "Channels");
        }
    }
}
