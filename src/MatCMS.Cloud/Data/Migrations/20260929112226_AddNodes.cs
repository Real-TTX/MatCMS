using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NodeId",
                table: "Instances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AgentVersion = table.Column<string>(type: "TEXT", nullable: true),
                    HostName = table.Column<string>(type: "TEXT", nullable: true),
                    DockerVersion = table.Column<string>(type: "TEXT", nullable: true),
                    DockerError = table.Column<string>(type: "TEXT", nullable: true),
                    InventoryJson = table.Column<string>(type: "TEXT", nullable: true),
                    InventoryAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProxyKind = table.Column<string>(type: "TEXT", nullable: false),
                    MatcadUrl = table.Column<string>(type: "TEXT", nullable: true),
                    MatcadTokenEnc = table.Column<string>(type: "TEXT", nullable: true),
                    CaddyAdminUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CaddyServer = table.Column<string>(type: "TEXT", nullable: true),
                    ProxyUpstream = table.Column<string>(type: "TEXT", nullable: false),
                    ProxyNetwork = table.Column<string>(type: "TEXT", nullable: true),
                    ProxyUpstreamHost = table.Column<string>(type: "TEXT", nullable: true),
                    PortFrom = table.Column<int>(type: "INTEGER", nullable: false),
                    PortTo = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NodeJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeJobs_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instances_NodeId",
                table: "Instances",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_NodeJobs_NodeId_State",
                table: "NodeJobs",
                columns: new[] { "NodeId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Name",
                table: "Nodes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_PublicId",
                table: "Nodes",
                column: "PublicId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Instances_Nodes_NodeId",
                table: "Instances",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Instances_Nodes_NodeId",
                table: "Instances");

            migrationBuilder.DropTable(
                name: "NodeJobs");

            migrationBuilder.DropTable(
                name: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Instances_NodeId",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "Instances");
        }
    }
}
