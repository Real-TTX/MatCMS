using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFullLogFetch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LogFetchRequestId",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LogFetchRequestedAt",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Exception",
                table: "InstanceLogs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogFetchRequestId",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "LogFetchRequestedAt",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "Exception",
                table: "InstanceLogs");
        }
    }
}
