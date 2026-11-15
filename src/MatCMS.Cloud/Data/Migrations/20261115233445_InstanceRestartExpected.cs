using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstanceRestartExpected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RestartExpectedUntil",
                table: "Instances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestartExpectedUntil",
                table: "Instances");
        }
    }
}
