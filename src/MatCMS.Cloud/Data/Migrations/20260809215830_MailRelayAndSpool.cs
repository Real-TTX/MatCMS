using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class MailRelayAndSpool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written: EF scaffolded a DROP followed by an ADD with an empty default, which
            // would have thrown the setting away. An empty value reads as "own", so every profile
            // that rolled out the CLOUD's SMTP would have quietly started rolling out its own
            // (mostly empty) values to live sites instead. Add, carry over, then drop.
            migrationBuilder.AddColumn<string>(
                name: "MailSource",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "own");

            migrationBuilder.Sql("UPDATE Profiles SET MailSource = 'global' WHERE UseGlobalSmtp = 1;");

            migrationBuilder.DropColumn(
                name: "UseGlobalSmtp",
                table: "Profiles");

            migrationBuilder.CreateTable(
                name: "SpooledMails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Recipients = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    ReplyTo = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpooledMails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpooledMails_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpooledMails_InstanceId",
                table: "SpooledMails",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpooledMails");

            migrationBuilder.DropColumn(
                name: "MailSource",
                table: "Profiles");

            migrationBuilder.AddColumn<bool>(
                name: "UseGlobalSmtp",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
