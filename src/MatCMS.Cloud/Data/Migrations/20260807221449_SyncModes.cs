using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <summary>
    /// Replaces the per-payload <c>Overwrite*</c> booleans with a three-valued mode (keep/add/once).
    /// <para>Hand-written on purpose: EF sees four bools disappear and five ints appear and guesses a
    /// rename, which would cross-wire the columns AND invert their meaning — the old <c>true</c>
    /// ("overwrite") is 1, but <c>SyncMode.Keep</c> is 0. The explicit UPDATE below is what keeps
    /// existing profiles behaving exactly as they did.</para>
    /// </summary>
    public partial class SyncModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var column in new[] { "SettingsMode", "PluginsMode", "ComponentsMode", "TemplatesMode", "UsersMode" })
            {
                migrationBuilder.AddColumn<int>(
                    name: column,
                    table: "Profiles",
                    type: "INTEGER",
                    nullable: false,
                    defaultValue: 0);
            }

            // Overwrite = true → Keep (0), false → Add (1). Nobody had "once" before, so nothing maps
            // to 2. Users were always add-only and stay that way.
            migrationBuilder.Sql("""
                UPDATE Profiles SET
                    SettingsMode   = CASE WHEN OverwriteSettings   = 1 THEN 0 ELSE 1 END,
                    PluginsMode    = CASE WHEN OverwritePlugins    = 1 THEN 0 ELSE 1 END,
                    ComponentsMode = CASE WHEN OverwriteComponents = 1 THEN 0 ELSE 1 END,
                    TemplatesMode  = CASE WHEN OverwriteTemplates  = 1 THEN 0 ELSE 1 END,
                    UsersMode      = 1;
                """);

            foreach (var column in new[] { "OverwriteSettings", "OverwritePlugins", "OverwriteComponents", "OverwriteTemplates" })
                migrationBuilder.DropColumn(name: column, table: "Profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var column in new[] { "OverwriteSettings", "OverwritePlugins", "OverwriteComponents", "OverwriteTemplates" })
            {
                migrationBuilder.AddColumn<bool>(
                    name: column,
                    table: "Profiles",
                    type: "INTEGER",
                    nullable: false,
                    defaultValue: true);
            }

            // "once" has no equivalent in the old world; it collapses to "do not overwrite", which is
            // the safer of the two directions to land on.
            migrationBuilder.Sql("""
                UPDATE Profiles SET
                    OverwriteSettings   = CASE WHEN SettingsMode   = 0 THEN 1 ELSE 0 END,
                    OverwritePlugins    = CASE WHEN PluginsMode    = 0 THEN 1 ELSE 0 END,
                    OverwriteComponents = CASE WHEN ComponentsMode = 0 THEN 1 ELSE 0 END,
                    OverwriteTemplates  = CASE WHEN TemplatesMode  = 0 THEN 1 ELSE 0 END;
                """);

            foreach (var column in new[] { "SettingsMode", "PluginsMode", "ComponentsMode", "TemplatesMode", "UsersMode" })
                migrationBuilder.DropColumn(name: column, table: "Profiles");
        }
    }
}
