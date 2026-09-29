using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstanceMigrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromNodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    ToNodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    FromName = table.Column<string>(type: "TEXT", nullable: false),
                    ToName = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Step = table.Column<string>(type: "TEXT", nullable: false),
                    Log = table.Column<string>(type: "TEXT", nullable: false),
                    RemoveSource = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceContainerId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetContainerId = table.Column<string>(type: "TEXT", nullable: true),
                    TransferId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceStopped = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetStarted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceMigrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceMigrations_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstanceMigrations_InstanceId_State",
                table: "InstanceMigrations",
                columns: new[] { "InstanceId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_InstanceMigrations_TransferId",
                table: "InstanceMigrations",
                column: "TransferId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceMigrations");
        }
    }
}
