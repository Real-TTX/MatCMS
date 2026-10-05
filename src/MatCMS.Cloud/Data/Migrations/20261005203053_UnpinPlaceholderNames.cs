using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnpinPlaceholderNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time repair. Saving the instance form used to pin whatever name was in the field, so
            // instances got their PLACEHOLDER pinned ("MatCMS", "Neue Instanz") when someone only saved
            // the domain — and never picked up their real site name. Unpinning a placeholder is safe: a
            // site that really is called "MatCMS" reports exactly that and keeps it.
            migrationBuilder.Sql(
                "UPDATE Instances SET NamePinned = 0 WHERE NamePinned = 1 AND TRIM(Name) IN ('MatCMS', 'Neue Instanz');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
