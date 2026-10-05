using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResyncRequested : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ResyncRequestedAt",
                table: "Instances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResyncRequestedAt",
                table: "Instances");
        }
    }
}
