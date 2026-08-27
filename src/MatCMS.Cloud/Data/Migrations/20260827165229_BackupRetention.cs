using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "BackupQuotaGb",
                table: "Profiles",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupKeepDaily",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupKeepMonthly",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupKeepWeekly",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupMaxCount",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupKeepDaily",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "BackupKeepMonthly",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "BackupKeepWeekly",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "BackupMaxCount",
                table: "Profiles");

            migrationBuilder.AlterColumn<int>(
                name: "BackupQuotaGb",
                table: "Profiles",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);
        }
    }
}
