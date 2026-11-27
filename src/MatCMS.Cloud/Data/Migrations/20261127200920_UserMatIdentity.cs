using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserMatIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalSubject",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentitySignedOutAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalSubject",
                table: "Users",
                column: "ExternalSubject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_ExternalSubject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ExternalSubject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IdentitySignedOutAt",
                table: "Users");
        }
    }
}
