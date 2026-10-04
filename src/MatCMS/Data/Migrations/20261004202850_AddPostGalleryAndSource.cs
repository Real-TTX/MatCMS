using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPostGalleryAndSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GalleryJson",
                table: "Posts",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "GalleryLayout",
                table: "Posts",
                type: "TEXT",
                nullable: false,
                defaultValue: "carousel");

            migrationBuilder.AddColumn<string>(
                name: "SourceName",
                table: "Posts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "Posts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GalleryJson",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "GalleryLayout",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SourceName",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "Posts");
        }
    }
}
