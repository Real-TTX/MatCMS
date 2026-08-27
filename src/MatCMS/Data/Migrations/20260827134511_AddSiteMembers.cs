using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Access",
                table: "Pages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RequiredRole",
                table: "Pages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SiteMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    RolesCsv = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteRoles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteMembers_Username",
                table: "SiteMembers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SiteRoles_Name",
                table: "SiteRoles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteMembers");

            migrationBuilder.DropTable(
                name: "SiteRoles");

            migrationBuilder.DropColumn(
                name: "Access",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "RequiredRole",
                table: "Pages");
        }
    }
}
