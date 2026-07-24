using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<ContentBlock> ContentBlocks => Set<ContentBlock>();
    public DbSet<ContactSubmission> ContactSubmissions => Set<ContactSubmission>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<Page>().HasIndex(p => p.Slug).IsUnique();
        b.Entity<SiteSetting>().HasIndex(s => s.Key).IsUnique();

        b.Entity<ContentBlock>()
            .HasOne(cb => cb.Page)
            .WithMany(p => p.Blocks)
            .HasForeignKey(cb => cb.PageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
