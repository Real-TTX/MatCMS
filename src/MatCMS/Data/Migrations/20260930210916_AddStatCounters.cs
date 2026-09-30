using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStatCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatCounters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Count = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatCounters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatCounters_Day_Kind_Key",
                table: "StatCounters",
                columns: new[] { "Day", "Kind", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatCounters");
        }
    }
}
