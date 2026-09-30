using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Cpus",
                table: "Nodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemTotal",
                table: "Nodes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cpus",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MemTotal",
                table: "Nodes");
        }
    }
}
