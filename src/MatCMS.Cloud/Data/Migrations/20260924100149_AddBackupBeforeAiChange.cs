using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupBeforeAiChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BackupBeforeAiChange",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupBeforeAiChange",
                table: "Profiles");
        }
    }
}
