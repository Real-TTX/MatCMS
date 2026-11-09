using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileInheritanceAndSenderName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FromName",
                table: "SpooledMails",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHtml",
                table: "SpooledMails",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MailFromSiteName",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ParentId",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MailFromName",
                table: "Instances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FromName",
                table: "SpooledMails");

            migrationBuilder.DropColumn(
                name: "IsHtml",
                table: "SpooledMails");

            migrationBuilder.DropColumn(
                name: "MailFromSiteName",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MailFromName",
                table: "Instances");
        }
    }
}
