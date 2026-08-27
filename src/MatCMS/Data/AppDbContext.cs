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
    public DbSet<Menu> Menus => Set<Menu>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<Form> Forms => Set<Form>();
    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();
    public DbSet<Media> Media => Set<Media>();
    public DbSet<Component> Components => Set<Component>();
    public DbSet<MailTemplate> MailTemplates => Set<MailTemplate>();
    public DbSet<Plugin> Plugins => Set<Plugin>();
    public DbSet<Post> Posts => Set<Post>();

    // Public-site visitor accounts and their role vocabulary (the "guest area" login).
    public DbSet<SiteMember> SiteMembers => Set<SiteMember>();
    public DbSet<SiteRole> SiteRoles => Set<SiteRole>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Post>().HasIndex(p => new { p.Slug, p.Locale }).IsUnique();
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        // A slug is unique per locale (the same slug may exist once per content locale).
        b.Entity<Page>().HasIndex(p => new { p.Slug, p.Locale }).IsUnique();
        b.Entity<Page>().HasIndex(p => p.TranslationGroup);
        b.Entity<MenuItem>().HasIndex(m => new { m.Menu, m.Locale });
        b.Entity<SiteSetting>().HasIndex(s => s.Key).IsUnique();
        b.Entity<Form>().HasIndex(f => f.Slug).IsUnique();
        b.Entity<Component>().HasIndex(c => c.Type).IsUnique();
        b.Entity<MailTemplate>().HasIndex(m => m.Key).IsUnique();
        b.Entity<Menu>().HasIndex(m => m.Key).IsUnique();
        b.Entity<SiteMember>().HasIndex(m => m.Username).IsUnique();
        b.Entity<SiteRole>().HasIndex(r => r.Name).IsUnique();

        b.Entity<ContentBlock>()
            .HasOne(cb => cb.Page)
            .WithMany(p => p.Blocks)
            .HasForeignKey(cb => cb.PageId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-reference for nested blocks (container → children).
        b.Entity<ContentBlock>()
            .HasOne(cb => cb.Parent)
            .WithMany(cb => cb.Children)
            .HasForeignKey(cb => cb.ParentId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<FormSubmission>()
            .HasOne(fs => fs.Form)
            .WithMany(f => f.Submissions)
            .HasForeignKey(fs => fs.FormId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
