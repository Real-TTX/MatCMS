using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Data.Migrations
{
    /// <inheritdoc />
    public partial class PluginOwnedPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerPlugin",
                table: "Pages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PluginRole",
                table: "Pages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pages_OwnerPlugin_PluginRole_Locale",
                table: "Pages",
                columns: new[] { "OwnerPlugin", "PluginRole", "Locale" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pages_OwnerPlugin_PluginRole_Locale",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "OwnerPlugin",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "PluginRole",
                table: "Pages");
        }
    }
}
