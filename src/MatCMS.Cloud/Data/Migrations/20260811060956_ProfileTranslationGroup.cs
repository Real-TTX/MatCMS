using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileTranslationGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SyncTranslation",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncTranslation",
                table: "Profiles");
        }
    }
}
