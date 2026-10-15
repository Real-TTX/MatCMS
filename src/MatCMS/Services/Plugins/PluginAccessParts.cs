using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Plugins;

/// <summary>A plugin's private files (<see cref="IPluginFiles"/>): <c>appdata/plugin-files/{key}/{name}</c>.</summary>
internal sealed class PluginFilesAccess : IPluginFiles
{
    // A short relative path of safe segments. No dot-only segment, no leading dot: "..", "." and ".htaccess"-style names are out.
    private static readonly Regex Name = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,100}(/[A-Za-z0-9][A-Za-z0-9._-]{0,100}){0,3}$", RegexOptions.Compiled);

    private readonly string _root;

    public PluginFilesAccess(IWebHostEnvironment env, string pluginKey) => _root = StoragePaths.PluginFilesDir(env, pluginKey);

    public bool IsValidName(string name) => IsValidRelativeName(name);

    /// <summary>The rule for a file name inside a plugin's private folder; shared with the restore, which must not accept more than the API does.</summary>
    public static bool IsValidRelativeName(string? name) => name is not null && Name.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    private string PathOf(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"'{name}' is not a valid file name for a plugin's private files.", nameof(name));
        var full = Path.GetFullPath(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar)));
        // Belt and braces on top of the pattern: whatever the name was, the file lies inside the plugin's folder.
        if (!full.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The name leaves the plugin's folder.", nameof(name));
        return full;
    }

    public async Task SaveAsync(string name, Stream content, CancellationToken ct = default)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write beside, then move: a download that fails half-way never leaves a truncated file under the real name.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(fs, ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ } }
    }

    public Task<Stream?> OpenAsync(string name, CancellationToken ct = default)
    {
        var path = PathOf(name);
        Stream? s = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
        return Task.FromResult(s);
    }

    public Task<long?> LengthAsync(string name, CancellationToken ct = default)
    {
        var path = PathOf(name);
        return Task.FromResult(File.Exists(path) ? new FileInfo(path).Length : (long?)null);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }
}

/// <summary>Mail for a plugin (<see cref="IPluginMail"/>): the site's own <see cref="EmailService"/> and the plugin's declared templates.</summary>
internal sealed class PluginMailAccess : IPluginMail
{
    private readonly IServiceProvider _services;
    private readonly PluginRegistry.Slice? _slice;
    private readonly string _pluginKey;

    public PluginMailAccess(IServiceProvider services, string pluginKey, PluginRegistry.Slice? slice)
    {
        _services = services; _pluginKey = pluginKey; _slice = slice;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        _services.GetService(typeof(EmailService)) is EmailService mail && await mail.IsConfiguredAsync();

    public async Task<MailResult> SendAsync(string templateKey, IEnumerable<string> to, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>? lists = null, string? replyTo = null, CancellationToken ct = default)
    {
        try
        {
            if (_services.GetService(typeof(EmailService)) is not EmailService mail) return new MailResult(false, "Der Mailversand ist in diesem Zusammenhang nicht verfügbar.");
            // A plugin sends only its own templates: a key of another plugin or of the core would let one plugin speak in another's voice.
            if (!templateKey.StartsWith(_pluginKey + ".", StringComparison.Ordinal))
                return new MailResult(false, $"Die Vorlage '{templateKey}' gehört nicht zum Plugin '{_pluginKey}'.");
            var declared = _slice?.MailTemplateSpecs.FirstOrDefault(m => m.Key == templateKey);

            Dictionary<string, IReadOnlyList<MatCMS.Shared.MailTemplates.Item>>? converted = null;
            if (lists is not null)
            {
                converted = new(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, passes) in lists)
                    converted[name] = passes.Select(p =>
                    {
                        var item = new MatCMS.Shared.MailTemplates.Item();
                        foreach (var (k, v) in p) item[k] = v;
                        return item;
                    }).ToList();
            }

            var (ok, error) = await mail.SendTemplateAsync(templateKey, to, values, replyTo, converted, declared is null ? null : Convert(declared));
            return new MailResult(ok, error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new MailResult(false, ex.Message); }
    }

    /// <summary>A plugin's declaration as the shared definition the mail machinery works with.</summary>
    internal static MatCMS.Shared.MailTemplates.Definition Convert(MailTemplateSpec s) => new(
        s.Key, s.Name, s.Description, s.Subject, s.Body,
        s.Placeholders.Select(p => new MatCMS.Shared.MailTemplates.Placeholder(p.Token, p.Description)).ToList(),
        s.Loops.Select(l => new MatCMS.Shared.MailTemplates.Loop(l.Name, l.Description, l.Fields.Select(f => new MatCMS.Shared.MailTemplates.Placeholder(f.Token, f.Description)).ToList())).ToList(),
        s.HtmlBody);
}

/// <summary>Puts a plugin's declared mails where the operator finds them: known to the mail machinery, and a row each in the template list.</summary>
internal static class PluginMailTemplates
{
    /// <summary>
    /// Registers the definitions and creates the row of every template that has none. An existing row is LEFT ALONE — it holds wording an
    /// operator may have written, and this runs on every load.
    /// </summary>
    public static async Task EnsureAsync(AppDbContext db, IReadOnlyList<MailTemplateSpec> specs, CancellationToken ct = default)
    {
        foreach (var s in specs)
        {
            MatCMS.Shared.MailTemplates.Register(PluginMailAccess.Convert(s));
            if (await db.MailTemplates.AnyAsync(t => t.Key == s.Key, ct)) continue;
            db.MailTemplates.Add(new MailTemplate { Key = s.Key, Name = s.Name, Description = s.Description, Subject = s.Subject, Body = s.Body });
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The site's customer accounts for a plugin (<see cref="IPluginMembers"/>), over the host's <see cref="MemberService"/>.</summary>
internal sealed class PluginMembersAccess : IPluginMembers
{
    private readonly IServiceProvider _services;
    public PluginMembersAccess(IServiceProvider services) => _services = services;

    private MemberService Service => _services.GetService(typeof(MemberService)) as MemberService
                                     ?? throw new InvalidOperationException("Die Kundenkonten sind in diesem Zusammenhang nicht verfügbar.");
    private AppDbContext Db => _services.GetService(typeof(AppDbContext)) as AppDbContext
                               ?? throw new InvalidOperationException("Die Datenbank ist in diesem Zusammenhang nicht verfügbar.");

    private static MemberAccount ToAccount(SiteMember m) =>
        new(m.Id, m.Username, string.IsNullOrWhiteSpace(m.DisplayName) ? m.Username : m.DisplayName, m.Email, m.EmailConfirmedAt is not null, m.IsActive,
            m.Roles.ToList(), DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc));

    public async Task<MemberAccount?> FindAsync(long id, CancellationToken ct = default) =>
        await Db.SiteMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct) is { } m ? ToAccount(m) : null;

    public async Task<MemberAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var e = MemberService.NormalizeEmail(email);
        if (e.Length == 0) return null;
        return await Db.SiteMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Email == e, ct) is { } m ? ToAccount(m) : null;
    }

    public async Task<MemberCreateResult> CreateAsync(string email, string password, string displayName, CancellationToken ct = default)
    {
        var (status, member) = await Service.CreateAsync(email, password, displayName, ct);
        return status switch
        {
            MemberService.CreateStatus.Created => new MemberCreateResult(MemberCreateStatus.Created, ToAccount(member!)),
            MemberService.CreateStatus.EmailTaken => new MemberCreateResult(MemberCreateStatus.EmailTaken, null),
            MemberService.CreateStatus.InvalidEmail => new MemberCreateResult(MemberCreateStatus.InvalidEmail, null),
            _ => new MemberCreateResult(MemberCreateStatus.WeakPassword, null)
        };
    }

    public async Task SignInAsync(MemberAccount member, bool persistent = false, CancellationToken ct = default)
    {
        var http = (_services.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor)?.HttpContext
                   ?? throw new InvalidOperationException("Anmelden braucht eine HTTP-Anfrage.");
        var row = await Db.SiteMembers.FirstOrDefaultAsync(m => m.Id == member.Id, ct);
        if (row is null || !row.IsActive) throw new InvalidOperationException("Dieses Konto gibt es nicht oder es ist gesperrt.");
        await Service.SignInAsync(http, row, persistent);
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        var http = (_services.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor)?.HttpContext
                   ?? throw new InvalidOperationException("Abmelden braucht eine HTTP-Anfrage.");
        return Service.SignOutAsync(http);
    }

    public Task<bool> ChangePasswordAsync(long id, string currentPassword, string newPassword, CancellationToken ct = default) =>
        Service.ChangePasswordAsync(id, currentPassword, newPassword, ct);

    public Task<bool> DeleteAsync(long id, CancellationToken ct = default) => Service.DeleteAsync(id, ct);
}
