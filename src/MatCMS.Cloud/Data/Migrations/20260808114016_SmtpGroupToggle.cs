using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <summary>Adds the per-group rollout switch for SMTP and turns it on where SMTP was already
    /// being rolled out, so no profile changes behaviour on upgrade.</summary>
    public partial class SmtpGroupToggle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SyncSmtp",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // A new switch that defaults to OFF would silently stop the rollout for every profile
            // that is doing it today. Switch it ON wherever SMTP was in fact being rolled out —
            // either from the global settings or from the profile own smtp.* rows.
            migrationBuilder.Sql("""
                UPDATE Profiles SET SyncSmtp = 1
                WHERE UseGlobalSmtp = 1
                   OR Id IN (SELECT ProfileId FROM ProfileSettings WHERE Key LIKE 'smtp.%');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncSmtp",
                table: "Profiles");
        }
    }
}
