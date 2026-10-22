using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaEditing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "Media",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalUrl",
                table: "Media",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "Media",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Height",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "OriginalUrl",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "Media");
        }
    }
}
