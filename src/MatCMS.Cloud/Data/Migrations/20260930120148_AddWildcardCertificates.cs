using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWildcardCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DnsCredentialsEnc",
                table: "Nodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DnsProvider",
                table: "Nodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WildcardEnabled",
                table: "Nodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WildcardError",
                table: "Nodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WildcardRouteId",
                table: "Nodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DnsCredentialsEnc",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "DnsProvider",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "WildcardEnabled",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "WildcardError",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "WildcardRouteId",
                table: "Nodes");
        }
    }
}
