using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Data.Migrations
{
    /// <summary>
    /// The blog became a plugin and its <c>Post</c> entity left the model — but the TABLE did not leave: the blog plugin adopts the
    /// <c>Posts</c> table as it is, so every post a site ever wrote is still there, read and written through the plugin's data API.
    /// <para><b>Up and Down are empty on purpose.</b> <c>dotnet ef migrations add</c> wrote a <c>DropTable("Posts")</c> here, because
    /// the entity is gone from the model; left in, this migration would delete every blog post of every site on its first start. Only
    /// the model snapshot changes. (The table itself is still created by the first migration and, for databases older than the
    /// migrations, by the baseline in <c>DbSeeder</c>.) If the model is ever regenerated, check that no <c>DropTable("Posts")</c> comes back.</para>
    /// </summary>
    public partial class BlogLeavesTheModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
