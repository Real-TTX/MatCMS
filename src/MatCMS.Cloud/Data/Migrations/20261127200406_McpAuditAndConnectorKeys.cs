using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class McpAuditAndConnectorKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestedBy",
                table: "ContentOps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OAuthClientId",
                table: "ApiKeys",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedBy",
                table: "ContentOps");

            migrationBuilder.DropColumn(
                name: "OAuthClientId",
                table: "ApiKeys");
        }
    }
}
