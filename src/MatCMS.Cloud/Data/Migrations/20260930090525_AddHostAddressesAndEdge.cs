using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostAddressesAndEdge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Nodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoDomainBase",
                table: "Nodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoDomainEnabled",
                table: "Nodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HostDomain",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostProvider",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HostPublishedAt",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostRouteError",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostRouteId",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProxyVia",
                table: "Instances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Address",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoDomainBase",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoDomainEnabled",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "HostDomain",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "HostProvider",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "HostPublishedAt",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "HostRouteError",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "HostRouteId",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ProxyVia",
                table: "Instances");
        }
    }
}
