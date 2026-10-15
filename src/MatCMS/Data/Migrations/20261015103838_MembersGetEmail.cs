using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Data.Migrations
{
    /// <inheritdoc />
    public partial class MembersGetEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmTokenExpiresAt",
                table: "SiteMembers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmTokenHash",
                table: "SiteMembers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "SiteMembers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailConfirmedAt",
                table: "SiteMembers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResetTokenExpiresAt",
                table: "SiteMembers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResetTokenHash",
                table: "SiteMembers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SiteMembers_Email",
                table: "SiteMembers",
                column: "Email",
                unique: true,
                filter: "\"Email\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SiteMembers_Email",
                table: "SiteMembers");

            migrationBuilder.DropColumn(
                name: "ConfirmTokenExpiresAt",
                table: "SiteMembers");

            migrationBuilder.DropColumn(
                name: "ConfirmTokenHash",
                table: "SiteMembers");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "SiteMembers");

            migrationBuilder.DropColumn(
                name: "EmailConfirmedAt",
                table: "SiteMembers");

            migrationBuilder.DropColumn(
                name: "ResetTokenExpiresAt",
                table: "SiteMembers");

            migrationBuilder.DropColumn(
                name: "ResetTokenHash",
                table: "SiteMembers");
        }
    }
}
