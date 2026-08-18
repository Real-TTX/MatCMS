using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupBeforeRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackupRequestError",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupRequestId",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "BackupRequestedAt",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BackupWaitNotified",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingRemovalAt",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingRemovalContainerId",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingRemovalError",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingRemovalMode",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestId",
                table: "CloudBackups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ArchivedBackups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceName = table.Column<string>(type: "TEXT", nullable: false),
                    InstancePublicId = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedBackups", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchivedBackups");

            migrationBuilder.DropColumn(
                name: "BackupRequestError",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "BackupRequestId",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "BackupRequestedAt",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "BackupWaitNotified",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "PendingRemovalAt",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "PendingRemovalContainerId",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "PendingRemovalError",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "PendingRemovalMode",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "CloudBackups");
        }
    }
}
