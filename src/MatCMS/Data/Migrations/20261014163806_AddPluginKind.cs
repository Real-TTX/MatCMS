using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Api",
                table: "Plugins",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Entry",
                table: "Plugins",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Plugins",
                type: "TEXT",
                nullable: false,
                defaultValue: "script");

            migrationBuilder.AddColumn<string>(
                name: "Sha256",
                table: "Plugins",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Api",
                table: "Plugins");

            migrationBuilder.DropColumn(
                name: "Entry",
                table: "Plugins");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Plugins");

            migrationBuilder.DropColumn(
                name: "Sha256",
                table: "Plugins");
        }
    }
}
