using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceProxy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProxyDomain",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProxyError",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProxyProvider",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProxyPublishedAt",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProxyRouteId",
                table: "Instances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProxyDomain",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ProxyError",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ProxyProvider",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ProxyPublishedAt",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ProxyRouteId",
                table: "Instances");
        }
    }
}
