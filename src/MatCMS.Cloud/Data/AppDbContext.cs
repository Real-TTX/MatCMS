using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Instance> Instances => Set<Instance>();
    public DbSet<InstanceEvent> InstanceEvents => Set<InstanceEvent>();
    public DbSet<CloudSetting> CloudSettings => Set<CloudSetting>();

    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ProfileSetting> ProfileSettings => Set<ProfileSetting>();
    public DbSet<ProfileUser> ProfileUsers => Set<ProfileUser>();
    public DbSet<ProfilePlugin> ProfilePlugins => Set<ProfilePlugin>();
    public DbSet<ProfileComponent> ProfileComponents => Set<ProfileComponent>();
    public DbSet<ProfileTemplate> ProfileTemplates => Set<ProfileTemplate>();

    // The global store plus the per-profile selections out of it.
    public DbSet<StorePlugin> StorePlugins => Set<StorePlugin>();
    public DbSet<StoreTemplate> StoreTemplates => Set<StoreTemplate>();
    public DbSet<StoreComponent> StoreComponents => Set<StoreComponent>();
    public DbSet<StoreUser> StoreUsers => Set<StoreUser>();
    public DbSet<StoreSetting> StoreSettings => Set<StoreSetting>();
    public DbSet<ProfileStorePlugin> ProfileStorePlugins => Set<ProfileStorePlugin>();
    public DbSet<ProfileStoreTemplate> ProfileStoreTemplates => Set<ProfileStoreTemplate>();
    public DbSet<ProfileStoreComponent> ProfileStoreComponents => Set<ProfileStoreComponent>();
    public DbSet<ProfileStoreUser> ProfileStoreUsers => Set<ProfileStoreUser>();
    public DbSet<ProfileStoreSetting> ProfileStoreSettings => Set<ProfileStoreSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<CloudSetting>().HasIndex(s => s.Key).IsUnique();

        // Both are lookup keys on the API hot path (every heartbeat resolves by PublicId, and the
        // token hash is compared right after).
        b.Entity<Instance>().HasIndex(i => i.PublicId).IsUnique();
        b.Entity<Instance>().HasIndex(i => i.TokenHash);

        // Deleting a profile must never delete the instances that used it — they fall back to
        // "no profile" and simply stop receiving configuration.
        b.Entity<Instance>()
            .HasOne(i => i.Profile)
            .WithMany()
            .HasForeignKey(i => i.ProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<InstanceEvent>()
            .HasOne(e => e.Instance)
            .WithMany()
            .HasForeignKey(e => e.InstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<InstanceEvent>().HasIndex(e => new { e.InstanceId, e.CreatedAt });

        // The join code is what an enrolling instance is resolved by, so it must be unique and fast.
        b.Entity<Profile>().HasIndex(p => p.JoinCode).IsUnique();
        b.Entity<Profile>().HasIndex(p => p.Name).IsUnique();

        // Payload identity within a profile: setting key, username, plugin key, component type.
        b.Entity<ProfileSetting>().HasIndex(s => new { s.ProfileId, s.Key }).IsUnique();
        b.Entity<ProfileUser>().HasIndex(u => new { u.ProfileId, u.Username }).IsUnique();
        b.Entity<ProfilePlugin>().HasIndex(p => new { p.ProfileId, p.Key }).IsUnique();
        b.Entity<ProfileComponent>().HasIndex(c => new { c.ProfileId, c.Type }).IsUnique();
        b.Entity<ProfileTemplate>().HasIndex(t => new { t.ProfileId, t.Name }).IsUnique();

        foreach (var relation in new Action[]
        {
            () => b.Entity<ProfileSetting>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade),
            () => b.Entity<ProfileUser>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade),
            () => b.Entity<ProfilePlugin>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade),
            () => b.Entity<ProfileComponent>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade),
            () => b.Entity<ProfileTemplate>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade)
        }) relation();

        // --- Store: identity is the same key the instance uses, so it must be unique here too ---
        b.Entity<StorePlugin>().HasIndex(p => p.Key).IsUnique();
        b.Entity<StoreTemplate>().HasIndex(t => t.Name).IsUnique();
        b.Entity<StoreComponent>().HasIndex(c => c.Type).IsUnique();
        b.Entity<StoreUser>().HasIndex(u => u.Username).IsUnique();
        b.Entity<StoreSetting>().HasIndex(s => s.Key).IsUnique();

        // Selections cascade from both sides: deleting a profile or a store entry only removes the
        // link, never the other end.
        b.Entity<ProfileStorePlugin>().HasIndex(x => new { x.ProfileId, x.StorePluginId }).IsUnique();
        b.Entity<ProfileStoreTemplate>().HasIndex(x => new { x.ProfileId, x.StoreTemplateId }).IsUnique();
        b.Entity<ProfileStoreComponent>().HasIndex(x => new { x.ProfileId, x.StoreComponentId }).IsUnique();
        b.Entity<ProfileStoreUser>().HasIndex(x => new { x.ProfileId, x.StoreUserId }).IsUnique();
        b.Entity<ProfileStoreSetting>().HasIndex(x => new { x.ProfileId, x.StoreSettingId }).IsUnique();

        foreach (var link in new Action[]
        {
            () => { b.Entity<ProfileStorePlugin>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
                    b.Entity<ProfileStorePlugin>().HasOne(x => x.StorePlugin).WithMany().HasForeignKey(x => x.StorePluginId).OnDelete(DeleteBehavior.Cascade); },
            () => { b.Entity<ProfileStoreTemplate>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
                    b.Entity<ProfileStoreTemplate>().HasOne(x => x.StoreTemplate).WithMany().HasForeignKey(x => x.StoreTemplateId).OnDelete(DeleteBehavior.Cascade); },
            () => { b.Entity<ProfileStoreComponent>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
                    b.Entity<ProfileStoreComponent>().HasOne(x => x.StoreComponent).WithMany().HasForeignKey(x => x.StoreComponentId).OnDelete(DeleteBehavior.Cascade); },
            () => { b.Entity<ProfileStoreUser>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
                    b.Entity<ProfileStoreUser>().HasOne(x => x.StoreUser).WithMany().HasForeignKey(x => x.StoreUserId).OnDelete(DeleteBehavior.Cascade); },
            () => { b.Entity<ProfileStoreSetting>().HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
                    b.Entity<ProfileStoreSetting>().HasOne(x => x.StoreSetting).WithMany().HasForeignKey(x => x.StoreSettingId).OnDelete(DeleteBehavior.Cascade); }
        }) link();
    }
}
