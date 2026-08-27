using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Backup &amp; Restore of the site. A backup is a ZIP containing <c>content.json</c>
/// (Templates, Pages incl. ContentBlocks, Menu items, Settings, contact submissions, Plugins) and,
/// optionally, an <c>assets/</c> folder with the uploaded media (appdata/uploads) plus a
/// <c>plugin-assets/{key}/</c> folder per plugin with the files uploaded into that plugin.
/// Admin users (incl. password hashes) are an OPT-IN section (off by default) — sensitive, meant for
/// full migrations; on restore they UPSERT by username so the current admin can't be locked out.
/// Plugins UPSERT by <c>Key</c> and a newly created one comes back DISABLED (see the import below).
/// On restore, only the sections present are replaced; missing sections/assets are left untouched.
/// Legacy plain-JSON backups are also accepted.
/// </summary>
public class ContentTransferService
{
    private const int CurrentVersion = 2;

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public ContentTransferService(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] AllowedAssetExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg",
         ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".zip"];

    /// <summary>Which sections to include in a backup.</summary>
    public sealed class BackupOptions
    {
        public bool Templates { get; set; } = true;
        public bool Pages { get; set; } = true;
        public bool Menus { get; set; } = true;
        public bool Settings { get; set; } = true;
        public bool Submissions { get; set; } = true;
        public bool Forms { get; set; } = true;
        public bool Assets { get; set; } = true;
        /// <summary>User-authored plugins (code + further script files + config). ON by default: a plugin
        /// only exists in this database, so a backup without it is the one thing a nightly backup cannot
        /// bring back.</summary>
        public bool Plugins { get; set; } = true;
        /// <summary>
        /// The files uploaded into the plugins' own asset folders (<c>appdata/plugin-assets/{Key}/</c>).
        /// ON by default, because a restored plugin without its images and stylesheets is a plugin that
        /// looks installed and renders broken.
        /// <para>Its OWN switch, next to the other sections rather than folded into "Plugins": these are
        /// binaries and can make a backup noticeably bigger, backups sit unencrypted in the cloud volume
        /// and there is a per-instance storage quota. Anything that can grow a backup has to be
        /// switchable where everything else about a backup is decided — not ride along quietly.</para>
        /// </summary>
        public bool PluginAssets { get; set; } = true;
        /// <summary>Admin users incl. password hashes. OFF by default — sensitive; only for full migrations.</summary>
        public bool Users { get; set; } = false;

        /// <summary>
        /// Granular selection within a section (non-empty = only these items; null/empty = the whole
        /// section). Restore treats such a backup as "partial" and upserts the items by their key
        /// instead of replacing the whole table.
        /// </summary>
        public List<string>? TemplateNames { get; set; } // by template Name
        public List<string>? PageKeys { get; set; }      // by "slug|locale" (see PageKey)
        public List<string>? FormSlugs { get; set; }     // by form Slug

        public bool Any => Templates || Pages || Menus || Settings || Submissions || Forms || Assets
                           || Plugins || PluginAssets || Users;
    }

    /// <summary>Stable identity of a page for granular backup/restore: slug + content locale.</summary>
    public static string PageKey(string? slug, string? locale) =>
        $"{(slug ?? "").Trim()}|{(string.IsNullOrWhiteSpace(locale) ? Localizer.DefaultCulture : locale!.Trim())}";

    /// <summary>What a backup file contains — shown before a restore so nothing is overwritten blind.</summary>
    public sealed class BackupInfo
    {
        public string? ExportedAtUtc { get; set; }
        public int Version { get; set; }
        public List<string> TemplateNames { get; set; } = new();
        public int Pages { get; set; }
        public int Menus { get; set; }
        public int MenuItems { get; set; }
        public int Settings { get; set; }
        public int Submissions { get; set; }
        public int Forms { get; set; }
        public int Media { get; set; }
        public int Components { get; set; }
        public int Plugins { get; set; }
        public int Users { get; set; }
        public bool HasAssets { get; set; }
        /// <summary>How many plugin-asset files the ZIP carries. Shown before a restore like every other
        /// section — a number nobody sees is a section that gets restored blind.</summary>
        public int PluginAssets { get; set; }
        // True when that section restores as a per-item UPSERT (others survive); false = replace-all.
        public bool TemplatesPartial { get; set; }
        public bool PagesPartial { get; set; }
        public bool FormsPartial { get; set; }
    }

    private string UploadsDir => StoragePaths.Uploads(_env);
    private string PluginAssetsDir => StoragePaths.PluginAssets(_env);

    /// <summary>
    /// Folder the plugins' uploaded files live under inside the ZIP — one level deeper than the media,
    /// because they belong to a plugin: <c>plugin-assets/{key}/{file}</c>, the same shape as on disk.
    /// <para>Deliberately the same MECHANISM as <c>assets/</c> and not a second one: entries in the same
    /// zip, an extension allow-list, and "restore what is in the file, leave the rest alone". What
    /// differs is only the allow-list — the plugin one from <c>PluginBundle</c>, which excludes SVG
    /// because a plugin asset is served from the site's own origin.</para>
    /// </summary>
    private const string PluginAssetFolder = "plugin-assets/";

    /// <summary>Builds a ZIP backup (content.json + optional assets/) and returns its bytes.</summary>
    public async Task<byte[]> ExportAsync(BackupOptions options, string? exportedAtUtc = null)
    {
        var json = await BuildJsonAsync(options, exportedAtUtc);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentEntry = zip.CreateEntry("content.json", CompressionLevel.Optimal);
            await using (var w = new StreamWriter(contentEntry.Open(), Encoding.UTF8))
                await w.WriteAsync(json);

            if (options.Assets && Directory.Exists(UploadsDir))
            {
                foreach (var file in Directory.GetFiles(UploadsDir))
                {
                    var name = Path.GetFileName(file);
                    var entry = zip.CreateEntry("assets/" + name, CompressionLevel.Optimal);
                    await using var es = entry.Open();
                    await using var fs = File.OpenRead(file);
                    await fs.CopyToAsync(es);
                }
            }

            // The plugins' own uploaded files. One folder per plugin key, exactly as on disk — the key
            // has to travel, or a restore would not know which plugin a stylesheet belongs to.
            if (options.PluginAssets && Directory.Exists(PluginAssetsDir))
            {
                foreach (var pluginDir in Directory.GetDirectories(PluginAssetsDir))
                {
                    var key = Path.GetFileName(pluginDir);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    foreach (var file in Directory.GetFiles(pluginDir))
                    {
                        var entry = zip.CreateEntry(
                            PluginAssetFolder + key + "/" + Path.GetFileName(file), CompressionLevel.Optimal);
                        await using var es = entry.Open();
                        await using var fs = File.OpenRead(file);
                        await fs.CopyToAsync(es);
                    }
                }
            }
        }
        return ms.ToArray();
    }

    private async Task<string> BuildJsonAsync(BackupOptions options, string? exportedAtUtc)
    {
        var dto = new TransferDto
        {
            Version = CurrentVersion,
            ExportedAtUtc = exportedAtUtc ?? DateTime.UtcNow.ToString("o"),
            TemplatesPartial = options.Templates && options.TemplateNames is { Count: > 0 },
            PagesPartial = options.Pages && options.PageKeys is { Count: > 0 },
            FormsPartial = options.Forms && options.FormSlugs is { Count: > 0 }
        };

        if (options.Templates)
        {
            var tplQuery = await _db.Templates.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
            if (options.TemplateNames is { Count: > 0 })
            {
                var wanted = new HashSet<string>(options.TemplateNames, StringComparer.OrdinalIgnoreCase);
                tplQuery = tplQuery.Where(t => wanted.Contains(t.Name)).ToList();
            }
            // Attached files, grouped by template — carried in the backup (bytes and all) so a self-hosted
            // script/font is not lost on restore. Only for the templates actually being exported.
            var exportIds = tplQuery.Select(t => t.Id).ToHashSet();
            var assetsByTpl = (await _db.TemplateAssets.AsNoTracking()
                    .Where(a => exportIds.Contains(a.TemplateId)).ToListAsync())
                .GroupBy(a => a.TemplateId)
                .ToDictionary(g => g.Key, g => g.ToList());
            dto.Templates = tplQuery
                .Select(t => new TemplateDto
                {
                    Name = t.Name, IsActive = t.IsActive, AccentColor = t.AccentColor,
                    HeadingFont = t.HeadingFont, BodyFont = t.BodyFont, ButtonStyle = t.ButtonStyle,
                    SecondaryColor = t.SecondaryColor, HeadingColor = t.HeadingColor, TextColor = t.TextColor,
                    BackgroundColor = t.BackgroundColor, AltBackground = t.AltBackground,
                    ContainerWidth = t.ContainerWidth, ButtonRadius = t.ButtonRadius,
                    HeaderBackground = t.HeaderBackground, HeaderTextColor = t.HeaderTextColor, HeaderPadding = t.HeaderPadding,
                    CustomCss = t.CustomCss, CustomJs = t.CustomJs, LayoutHtml = t.LayoutHtml,
                    LoginHtml = t.LoginHtml,
                    Assets = assetsByTpl.TryGetValue(t.Id, out var al)
                        ? al.Select(a => new TemplateAssetDto
                        {
                            Name = a.Name, ContentType = a.ContentType, Base64 = Convert.ToBase64String(a.Bytes)
                        }).ToList()
                        : null,
                    MenuMapJson = t.MenuMapJson,
                    ParametersJson = t.ParametersJson, ParamValuesJson = t.ParamValuesJson,
                    SchemaVersion = t.SchemaVersion, PartsJson = t.PartsJson
                }).ToList();

            // Component-designer components travel with the full template section, but not when the
            // user narrowed the export to specific templates (then they want just those templates).
            if (options.TemplateNames is null or { Count: 0 })
            {
                dto.Components = (await _db.Components.AsNoTracking().OrderBy(c => c.Name).ToListAsync())
                    .Select(c => new ComponentDto
                    {
                        Type = c.Type, Name = c.Name, Description = c.Description,
                        FieldsJson = c.FieldsJson, TemplateHtml = c.TemplateHtml, CreatedAt = c.CreatedAt
                    }).ToList();
            }
        }

        if (options.Pages)
        {
            var pages = await _db.Pages.AsNoTracking().Include(p => p.Blocks).OrderBy(p => p.Id).ToListAsync();
            if (options.PageKeys is { Count: > 0 })
            {
                var wanted = new HashSet<string>(options.PageKeys, StringComparer.OrdinalIgnoreCase);
                pages = pages.Where(p => wanted.Contains(PageKey(p.Slug, p.Locale))).ToList();
            }
            // Resolve a page's TemplateId to the template NAME for the export (ids shift on restore).
            var tplNameById = await _db.Templates.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name);
            dto.Pages = pages.Select(p => new PageDto
            {
                Title = p.Title, Slug = p.Slug, NavLabel = p.NavLabel,
                Locale = p.Locale, TranslationGroup = p.TranslationGroup,
                IsPublished = p.IsPublished, ShowInNav = p.ShowInNav, ShowInFooter = p.ShowInFooter,
                NavOrder = p.NavOrder, FooterOrder = p.FooterOrder, MetaDescription = p.MetaDescription,
                CustomCss = p.CustomCss,
                TemplateName = p.TemplateId is int tid && tplNameById.TryGetValue(tid, out var tn) ? tn : null,
                Access = p.Access, RequiredRole = p.RequiredRole, TemplateParamsJson = p.TemplateParamsJson,
                CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt,
                // Preserve the block hierarchy: top-level blocks with their children nested inside.
                Blocks = BuildBlockDtos(p.Blocks, null)
            }).ToList();
        }

        if (options.Menus)
        {
            dto.Menus = (await _db.Menus.AsNoTracking().OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync())
                .Select(m => new MenuDto { Key = m.Key, Name = m.Name, SortOrder = m.SortOrder, BuiltIn = m.BuiltIn })
                .ToList();
            dto.MenuItems = (await _db.MenuItems.AsNoTracking().OrderBy(m => m.Menu).ThenBy(m => m.SortOrder).ToListAsync())
                .Select(m => new MenuItemDto
                {
                    Id = m.Id, ParentId = m.ParentId,
                    Menu = m.Menu, Label = m.Label, Url = m.Url, SortOrder = m.SortOrder,
                    OpenInNewTab = m.OpenInNewTab, Locale = m.Locale, Icon = m.Icon
                }).ToList();
        }

        if (options.Settings)
        {
            dto.Settings = (await _db.SiteSettings.AsNoTracking().OrderBy(s => s.Key).ToListAsync())
                .Select(s => new SettingDto { Key = s.Key, Value = s.Value }).ToList();
        }

        if (options.Submissions)
        {
            dto.Submissions = (await _db.ContactSubmissions.AsNoTracking().OrderBy(s => s.Id).ToListAsync())
                .Select(s => new SubmissionDto
                {
                    Name = s.Name, Email = s.Email, Category = s.Category,
                    Message = s.Message, IsRead = s.IsRead, CreatedAt = s.CreatedAt
                }).ToList();
        }

        if (options.Forms)
        {
            var forms = await _db.Forms.AsNoTracking().OrderBy(f => f.Id).ToListAsync();
            if (options.FormSlugs is { Count: > 0 })
            {
                var wanted = new HashSet<string>(options.FormSlugs, StringComparer.OrdinalIgnoreCase);
                forms = forms.Where(f => wanted.Contains(f.Slug)).ToList();
            }
            dto.Forms = forms.Select(f => new FormDto
            {
                Name = f.Name, Slug = f.Slug, DefinitionJson = f.DefinitionJson, CreatedAt = f.CreatedAt,
                SuccessMessage = f.SuccessMessage, SubmitLabel = f.SubmitLabel,
                NotifyEnabled = f.NotifyEnabled, NotifyJson = f.NotifyJson
            }).ToList();

            var idToSlug = forms.ToDictionary(f => f.Id, f => f.Slug);
            var subs = await _db.FormSubmissions.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
            dto.FormSubmissions = subs
                .Where(s => idToSlug.ContainsKey(s.FormId))
                .Select(s => new FormSubmissionDto
                {
                    FormSlug = idToSlug[s.FormId], DataJson = s.DataJson, IsRead = s.IsRead, CreatedAt = s.CreatedAt
                }).ToList();
        }

        // Media-library records travel with the asset files.
        if (options.Assets)
        {
            dto.Media = (await _db.Media.AsNoTracking().OrderBy(m => m.Id).ToListAsync())
                .Select(m => new MediaDto
                {
                    Url = m.Url, FileName = m.FileName, Alt = m.Alt, Tags = m.Tags,
                    ContentType = m.ContentType, SizeBytes = m.SizeBytes, SortOrder = m.SortOrder, CreatedAt = m.CreatedAt
                }).ToList();
        }

        // Blog posts travel with the full Pages section (not a granular page subset).
        if (options.Pages && (options.PageKeys is null || options.PageKeys.Count == 0))
        {
            dto.Posts = (await _db.Posts.AsNoTracking()
                .OrderByDescending(p => p.PublishedAt).ThenBy(p => p.Id).ToListAsync())
                .Select(p => new PostDto
                {
                    Title = p.Title, Slug = p.Slug, TitleImage = p.TitleImage, Excerpt = p.Excerpt,
                    ContentHtml = p.ContentHtml, Tags = p.Tags, AttachmentsJson = p.AttachmentsJson,
                    Locale = p.Locale, IsPublished = p.IsPublished,
                    PublishedAt = p.PublishedAt, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt
                }).ToList();
        }

        // Plugins. Their code lives NOWHERE but this database — no file on disk, no image layer — so a
        // backup that leaves them out makes a deleted plugin unrecoverable however many nightly copies
        // exist. FilesJson travels with Code: a plugin whose entry file `#load`s further script files
        // would otherwise come back as a broken shell that still looks complete.
        if (options.Plugins)
        {
            dto.Plugins = (await _db.Plugins.AsNoTracking().OrderBy(p => p.Id).ToListAsync())
                .Select(p => new PluginDto
                {
                    Key = p.Key, Name = p.Name, Description = p.Description,
                    Version = p.Version, DataVersion = p.DataVersion,
                    Code = p.Code, FilesJson = p.FilesJson, MappingJson = p.MappingJson,
                    ConfigJson = p.ConfigJson,
                    Enabled = p.Enabled, CreatedAt = p.CreatedAt
                }).ToList();
        }

        // Admin users (opt-in) — includes password hashes; only for full migrations.
        if (options.Users)
        {
            dto.Users = (await _db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync())
                .Select(u => new UserDto
                {
                    Username = u.Username, PasswordHash = u.PasswordHash, Role = u.Role,
                    DisplayName = u.DisplayName, Email = u.Email, CreatedAt = u.CreatedAt
                }).ToList();
        }

        return JsonSerializer.Serialize(dto, WriteOpts);
    }

    /// <summary>Restores a backup. Accepts a ZIP (content.json + assets/) or a legacy plain-JSON file.</summary>
    public async Task<string> ImportAsync(byte[] data)
    {
        if (data is null || data.Length == 0)
            throw new InvalidOperationException("Die Datei ist leer.");

        // ZIP magic bytes "PK\x03\x04"
        var isZip = data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;

        if (!isZip)
            return await ImportJsonAsync(Encoding.UTF8.GetString(data));

        using var ms = new MemoryStream(data);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var contentEntry = zip.GetEntry("content.json")
            ?? throw new InvalidOperationException("Das ZIP enthält keine content.json.");

        string json;
        using (var r = new StreamReader(contentEntry.Open(), Encoding.UTF8))
            json = await r.ReadToEndAsync();

        var summary = await ImportJsonAsync(json);

        // Restore assets into wwwroot/uploads (filename only -> no path traversal; image types only).
        var assetCount = 0;
        var uploads = UploadsDir;
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(entry.FullName);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!AllowedAssetExtensions.Contains(ext)) continue;

            Directory.CreateDirectory(uploads);
            var dest = Path.Combine(uploads, name);
            await using (var es = entry.Open())
            await using (var fs = File.Create(dest))
                await es.CopyToAsync(fs);
            assetCount++;
            // The original was just OVERWRITTEN (File.Create truncates). Any cached thumbnail of that
            // name now shows the previous picture, and it would keep showing it forever because the
            // cache is keyed by file name and never expires. Drop it; the next request rebuilds it.
            DropThumbnails(name);
        }

        var pluginAssetCount = await RestorePluginAssetsAsync(zip);

        var extra = new List<string>();
        if (assetCount > 0) extra.Add($"{assetCount} Medien");
        if (pluginAssetCount > 0) extra.Add($"{pluginAssetCount} Plugin-Dateien");
        return extra.Count > 0 ? $"{summary} ({string.Join(", ", extra)})" : summary;
    }

    /// <summary>
    /// Forgets every cached thumbnail of one upload, across all widths and including a "could not be
    /// scaled" marker — the replacement file may well be decodable where the old one was not.
    /// <para>Note what is NOT here: an export side. Thumbnails live in <c>appdata/thumbs</c>, a sibling
    /// of <c>uploads/</c>, so the flat <c>Directory.GetFiles(UploadsDir)</c> above never sees them and
    /// they stay out of the ZIP. That is deliberate. Backups sit unencrypted in the cloud volume
    /// against a per-instance quota, and a thumbnail carries no information the original does not — it
    /// is rebuilt from it in milliseconds. Packing them would roughly double a media-heavy backup to
    /// transport nothing.</para>
    /// </summary>
    private void DropThumbnails(string name)
    {
        var root = StoragePaths.Thumbs(_env);
        if (!Directory.Exists(root)) return;
        foreach (var width in ThumbnailService.Widths)
        {
            var file = Path.Combine(root, width.ToString(), name + ".webp");
            try { if (File.Exists(file)) File.Delete(file); } catch { /* a stale thumbnail must never fail a restore */ }
            try { if (File.Exists(file + ".failed")) File.Delete(file + ".failed"); } catch { /* same */ }
        }
    }

    /// <summary>
    /// Puts the plugins' uploaded files back under <c>appdata/plugin-assets/{key}/</c>.
    /// <para>Both path parts are rebuilt rather than trusted, which is the whole security of this: the
    /// key is re-slugified to a single folder name and the file name is stripped of any directory part,
    /// so a crafted backup cannot write one byte outside the plugin-assets tree — a restore runs as the
    /// server, and a backup is a file somebody hands us.</para>
    /// <para>Nothing is removed. A file that is no longer in the backup stays where it is, exactly as a
    /// restore leaves untouched sections alone: a restore is not a synchronisation.</para>
    /// </summary>
    private async Task<int> RestorePluginAssetsAsync(ZipArchive zip)
    {
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            var norm = entry.FullName.Replace('\\', '/');
            if (!norm.StartsWith(PluginAssetFolder, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = norm[PluginAssetFolder.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) continue;                                   // no plugin folder → not ours

            var key = Pages.Admin.Pages.IndexModel.Slugify(rest[..slash]);
            var name = PluginPackager.SanitizeFileName(rest[(slash + 1)..]);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name)) continue;

            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!MatCMS.Shared.PluginBundle.AllowedAssetExt.Contains(ext)) continue;
            if (entry.Length > MatCMS.Shared.PluginBundle.MaxAssetBytes) continue;

            var dir = StoragePaths.PluginAssetDir(_env, key);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, name);
            await using (var es = entry.Open())
            await using (var fs = File.Create(dest))
                await es.CopyToAsync(fs);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Restores the sections present in the JSON (transactional). Each present section replaces
    /// the corresponding table; sections not in the file are left untouched.
    /// </summary>
    private async Task<string> ImportJsonAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Die Datei ist leer.");

        TransferDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TransferDto>(json, ReadOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Ungültige JSON-Datei: {ex.Message}");
        }

        if (dto is null || (dto.Templates is null && dto.Pages is null && dto.Menus is null
                            && dto.MenuItems is null && dto.Settings is null && dto.Submissions is null
                            && dto.Forms is null && dto.FormSubmissions is null
                            && dto.Media is null && dto.Components is null && dto.Users is null
                            && dto.Posts is null && dto.Plugins is null))
            throw new InvalidOperationException(
                "Die Datei hat kein bekanntes Backup-Format (keine Abschnitte gefunden).");

        var summary = new List<string>();

        await using var tx = await _db.Database.BeginTransactionAsync();

        if (dto.Templates is not null && dto.TemplatesPartial)
        {
            // Granular restore: upsert each template by name, leaving the site's other themes and the
            // currently-active template untouched (unless nothing is active afterwards).
            var existing = await _db.Templates.ToListAsync();
            foreach (var t in dto.Templates)
            {
                var name = string.IsNullOrWhiteSpace(t.Name) ? "Template" : t.Name!;
                var row = existing.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    row = new Template { Name = name };
                    _db.Templates.Add(row);
                    existing.Add(row);   // same-named entries in one backup map to one row, no duplicate insert
                }
                ApplyTemplate(row, t); // fields only — the active theme is deliberately left as-is
            }
            await _db.SaveChangesAsync();
            var all = await _db.Templates.ToListAsync();
            if (all.Count > 0 && all.All(t => !t.IsActive)) all[0].IsActive = true;
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Templates.Count} Templates (aktualisiert)");
        }
        else if (dto.Templates is not null)
        {
            _db.Templates.RemoveRange(_db.Templates);
            await _db.SaveChangesAsync();
            foreach (var t in dto.Templates)
            {
                var row = new Template
                {
                    Name = t.Name ?? "Template",
                    IsActive = t.IsActive,
                    AccentColor = string.IsNullOrWhiteSpace(t.AccentColor) ? "#2563eb" : t.AccentColor!,
                    HeadingFont = string.IsNullOrWhiteSpace(t.HeadingFont) ? "Geologica" : t.HeadingFont!,
                    BodyFont = string.IsNullOrWhiteSpace(t.BodyFont) ? "Inter" : t.BodyFont!,
                    ButtonStyle = string.IsNullOrWhiteSpace(t.ButtonStyle) ? "solid" : t.ButtonStyle!,
                    SecondaryColor = t.SecondaryColor ?? "",
                    HeadingColor = string.IsNullOrWhiteSpace(t.HeadingColor) ? "#010101" : t.HeadingColor!,
                    TextColor = string.IsNullOrWhiteSpace(t.TextColor) ? "#1a1a1a" : t.TextColor!,
                    BackgroundColor = string.IsNullOrWhiteSpace(t.BackgroundColor) ? "#ffffff" : t.BackgroundColor!,
                    AltBackground = string.IsNullOrWhiteSpace(t.AltBackground) ? "#f6f7f9" : t.AltBackground!,
                    ContainerWidth = string.IsNullOrWhiteSpace(t.ContainerWidth) ? "1180" : t.ContainerWidth!,
                    ButtonRadius = string.IsNullOrWhiteSpace(t.ButtonRadius) ? "0" : t.ButtonRadius!,
                    HeaderBackground = t.HeaderBackground ?? "",
                    HeaderTextColor = t.HeaderTextColor ?? "",
                    HeaderPadding = string.IsNullOrWhiteSpace(t.HeaderPadding) ? "16" : t.HeaderPadding!,
                    CustomCss = t.CustomCss ?? "",
                    CustomJs = t.CustomJs ?? "",
                    LayoutHtml = t.LayoutHtml ?? "",
                    LoginHtml = t.LoginHtml ?? "",
                    MenuMapJson = string.IsNullOrWhiteSpace(t.MenuMapJson) ? "{}" : t.MenuMapJson!,
                    // This branch builds a BRAND NEW row, so there is nothing to preserve: an older
                    // backup that carries neither field lands on the column defaults, exactly as before.
                    ParametersJson = string.IsNullOrWhiteSpace(t.ParametersJson) ? "[]" : t.ParametersJson!,
                    ParamValuesJson = string.IsNullOrWhiteSpace(t.ParamValuesJson) ? "{}" : t.ParamValuesJson!,
                    SchemaVersion = t.SchemaVersion <= 0 ? 1 : t.SchemaVersion,
                    PartsJson = string.IsNullOrWhiteSpace(t.PartsJson) ? "{}" : t.PartsJson!
                };
                // Bring a template from an older backup up to the current format immediately.
                MatCMS.Content.TemplateSchema.Upgrade(row);
                _db.Templates.Add(row);
            }
            await _db.SaveChangesAsync();
            var all = await _db.Templates.ToListAsync();
            if (all.Count > 0 && all.All(t => !t.IsActive)) all[0].IsActive = true;
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Templates.Count} Templates");
        }

        // Template files: applied after the templates themselves exist (either branch), matched to their
        // now-saved row by name. Each template's set is replaced wholesale from the backup — a template
        // the backup carries with no files ends up with none, mirroring how its style fields restore.
        if (dto.Templates is not null && dto.Templates.Any(t => t.Assets is { Count: > 0 }))
        {
            var rowsByName = await _db.Templates.ToDictionaryAsync(
                t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
            var assetCount = 0;
            foreach (var t in dto.Templates)
            {
                var name = string.IsNullOrWhiteSpace(t.Name) ? "Template" : t.Name!;
                if (t.Assets is null || !rowsByName.TryGetValue(name, out var row)) continue;
                var old = await _db.TemplateAssets.Where(a => a.TemplateId == row.Id).ToListAsync();
                _db.TemplateAssets.RemoveRange(old);
                foreach (var a in t.Assets)
                {
                    if (string.IsNullOrWhiteSpace(a.Name)) continue;
                    _db.TemplateAssets.Add(new TemplateAsset
                    {
                        TemplateId = row.Id,
                        Name = a.Name!.Trim(),
                        ContentType = string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType!,
                        Bytes = string.IsNullOrEmpty(a.Base64) ? Array.Empty<byte>() : Convert.FromBase64String(a.Base64)
                    });
                    assetCount++;
                }
            }
            await _db.SaveChangesAsync();
            if (assetCount > 0) summary.Add($"{assetCount} Template-Dateien");
        }

        if (dto.Components is not null)
        {
            _db.Components.RemoveRange(_db.Components);
            await _db.SaveChangesAsync();
            foreach (var c in dto.Components)
            {
                if (string.IsNullOrWhiteSpace(c.Type)) continue;
                _db.Components.Add(new Component
                {
                    Type = c.Type!,
                    Name = c.Name ?? c.Type!,
                    Description = c.Description ?? "",
                    FieldsJson = string.IsNullOrWhiteSpace(c.FieldsJson) ? "[]" : c.FieldsJson!,
                    TemplateHtml = c.TemplateHtml ?? "",
                    CreatedAt = c.CreatedAt == default ? DateTime.UtcNow : c.CreatedAt
                });
            }
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Components.Count} Komponenten");
        }

        if (dto.Pages is not null)
        {
            var blockCount = 0;

            // Re-link each page to its own template by NAME — template ids changed on restore, and the
            // templates were imported just above. Unknown/absent name → no per-page template (active one).
            var tplIdByName = await _db.Templates.AsNoTracking()
                .ToDictionaryAsync(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
            int? ResolveTpl(string? name) =>
                !string.IsNullOrWhiteSpace(name) && tplIdByName.TryGetValue(name!, out var id) ? id : (int?)null;

            if (dto.PagesPartial)
            {
                // Granular restore: upsert each page by (slug, locale); its blocks are replaced.
                // Pages not in the backup are left untouched.
                var existing = await _db.Pages.Include(p => p.Blocks).ToListAsync();
                foreach (var p in dto.Pages)
                {
                    var slug = (p.Slug ?? "").Trim();
                    var locale = (string.IsNullOrWhiteSpace(p.Locale) ? Localizer.DefaultCulture : p.Locale!).Trim();
                    var row = existing.FirstOrDefault(x =>
                        string.Equals(x.Slug, slug, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Locale, locale, StringComparison.OrdinalIgnoreCase));
                    if (row is null)
                    {
                        row = new Page();
                        ApplyPageFields(row, p, locale);
                        _db.Pages.Add(row);
                        existing.Add(row);   // so a duplicate (slug,locale) in the same backup upserts, not double-inserts
                    }
                    else
                    {
                        _db.ContentBlocks.RemoveRange(row.Blocks); // drop the page's old blocks (all levels)
                        ApplyPageFields(row, p, locale);
                    }
                    row.TemplateId = ResolveTpl(p.TemplateName);
                    await _db.SaveChangesAsync();               // ensure page Id exists + old blocks removed
                    blockCount += await InsertBlockTreeAsync(p.Blocks, row.Id, null);
                }
                summary.Add($"{dto.Pages.Count} Seiten (aktualisiert, {blockCount} Blöcke)");
            }
            else
            {
                _db.ContentBlocks.RemoveRange(_db.ContentBlocks);
                _db.Pages.RemoveRange(_db.Pages);
                await _db.SaveChangesAsync();
                foreach (var p in dto.Pages)
                {
                    var row = new Page();
                    ApplyPageFields(row, p, string.IsNullOrWhiteSpace(p.Locale) ? Localizer.DefaultCulture : p.Locale!);
                    row.TemplateId = ResolveTpl(p.TemplateName);
                    row.CreatedAt = p.CreatedAt == default ? DateTime.UtcNow : p.CreatedAt;
                    _db.Pages.Add(row);
                    await _db.SaveChangesAsync();               // page Id
                    blockCount += await InsertBlockTreeAsync(p.Blocks, row.Id, null);
                }
                summary.Add($"{dto.Pages.Count} Seiten ({blockCount} Blöcke)");
            }
        }

        if (dto.Menus is not null)
        {
            var existing = await _db.Menus.ToListAsync();
            var kept = new HashSet<string>(StringComparer.Ordinal);
            foreach (var md in dto.Menus)
            {
                if (string.IsNullOrWhiteSpace(md.Key)) continue;
                var m = existing.FirstOrDefault(x => x.Key == md.Key);
                if (m is null)
                {
                    m = new Menu { Key = md.Key!, BuiltIn = md.BuiltIn };
                    _db.Menus.Add(m);
                    existing.Add(m);   // dedupe same-key entries within one backup
                }
                m.Name = md.Name ?? md.Key!;
                m.SortOrder = md.SortOrder;
                kept.Add(md.Key!);
            }
            // Full-restore replace semantics: drop custom menus the backup did not carry (never the
            // built-in header/footer/toolbar). Their items are wiped by the MenuItems replace below.
            var orphans = existing.Where(x => !x.BuiltIn && !kept.Contains(x.Key)).ToList();
            if (orphans.Count > 0) _db.Menus.RemoveRange(orphans);
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Menus.Count} Menüs");
        }

        if (dto.MenuItems is not null)
        {
            _db.MenuItems.RemoveRange(_db.MenuItems);
            await _db.SaveChangesAsync();
            // Pass 1: insert every item, tracking original id → new row.
            var menuMap = new Dictionary<int, MenuItem>();
            foreach (var m in dto.MenuItems)
            {
                var row = new MenuItem
                {
                    Menu = string.IsNullOrWhiteSpace(m.Menu) ? "header" : m.Menu!,
                    Label = m.Label ?? "", Url = m.Url ?? "", SortOrder = m.SortOrder, OpenInNewTab = m.OpenInNewTab,
                    Locale = string.IsNullOrWhiteSpace(m.Locale) ? Localizer.DefaultCulture : m.Locale!,
                    Icon = MenuIcons.IsValid(m.Icon) ? m.Icon : null
                };
                _db.MenuItems.Add(row);
                if (m.Id != 0) menuMap[m.Id] = row;
            }
            await _db.SaveChangesAsync();
            // Pass 2: re-link ParentId via the id map (legacy backups without ids stay flat).
            var relinked = false;
            foreach (var m in dto.MenuItems)
                if (m.ParentId is int pid && menuMap.TryGetValue(m.Id, out var child) && menuMap.TryGetValue(pid, out var parent))
                { child.ParentId = parent.Id; relinked = true; }
            if (relinked) await _db.SaveChangesAsync();
            summary.Add($"{dto.MenuItems.Count} Menüeinträge");
        }

        if (dto.Settings is not null)
        {
            // The cloud link survives a restore, whatever the backup says.
            //
            // Everything under `cloud.*` describes THIS container's connection — its token, which
            // revision it is on, what it has already seeded once. A backup carries the values from
            // the moment it was taken, so restoring one used to rewind all of that: a token rotated
            // since would be replaced by a dead one and the site would drop off the cloud seconds
            // after a restore the cloud itself had just triggered.
            //
            // It also stops a backup from carrying an identity between sites. Restoring another
            // instance's ZIP by hand would otherwise hand this container that instance's token, and
            // two sites reporting as the same instance is not a state anything downstream expects.
            var keep = await _db.SiteSettings
                .Where(s => SettingKeys.Cloud.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);

            _db.SiteSettings.RemoveRange(_db.SiteSettings);
            await _db.SaveChangesAsync();
            foreach (var s in dto.Settings.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
            {
                if (keep.ContainsKey(s.Key!)) continue;
                _db.SiteSettings.Add(new SiteSetting { Key = s.Key!, Value = s.Value ?? "" });
            }
            foreach (var (key, value) in keep)
                _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Settings.Count} Einstellungen");
        }

        if (dto.Submissions is not null)
        {
            _db.ContactSubmissions.RemoveRange(_db.ContactSubmissions);
            await _db.SaveChangesAsync();
            foreach (var s in dto.Submissions)
                _db.ContactSubmissions.Add(new ContactSubmission
                {
                    Name = s.Name ?? "", Email = s.Email ?? "", Category = s.Category,
                    Message = s.Message ?? "", IsRead = s.IsRead,
                    CreatedAt = s.CreatedAt == default ? DateTime.UtcNow : s.CreatedAt
                });
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Submissions.Count} Anfragen");
        }

        var partialForms = dto.Forms is not null && dto.FormsPartial;

        if (partialForms)
        {
            // Granular restore: upsert forms by slug; forms not in the backup are left untouched.
            var existing = await _db.Forms.ToListAsync();
            var newlyCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in dto.Forms!.Where(f => !string.IsNullOrWhiteSpace(f.Slug)))
            {
                var row = existing.FirstOrDefault(x => string.Equals(x.Slug, f.Slug, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    row = new Form { Slug = f.Slug! };
                    _db.Forms.Add(row);
                    existing.Add(row);          // so a duplicate slug in the same backup upserts, not double-inserts
                    newlyCreated.Add(f.Slug!);
                }
                row.Name = string.IsNullOrWhiteSpace(f.Name) ? (string.IsNullOrWhiteSpace(row.Name) ? "Formular" : row.Name) : f.Name!;
                row.DefinitionJson = string.IsNullOrWhiteSpace(f.DefinitionJson) ? "[]" : f.DefinitionJson!;
                row.SuccessMessage = f.SuccessMessage;
                row.SubmitLabel = f.SubmitLabel;
                row.NotifyEnabled = f.NotifyEnabled;
                row.NotifyJson = f.NotifyJson ?? "";
                if (row.CreatedAt == default) row.CreatedAt = f.CreatedAt == default ? DateTime.UtcNow : f.CreatedAt;
            }
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Forms!.Count} Formulare (aktualisiert)");

            // Restore submissions ONLY for forms this restore just CREATED. For forms that already
            // existed we deliberately leave their live submissions untouched — reverting a form's
            // definition must never destroy answers collected since the backup was taken.
            if (dto.FormSubmissions is not null && newlyCreated.Count > 0)
            {
                var slugToId = (await _db.Forms.ToListAsync())
                    .Where(f => newlyCreated.Contains(f.Slug))
                    .ToDictionary(f => f.Slug, f => f.Id, StringComparer.OrdinalIgnoreCase);
                var count = 0;
                foreach (var s in dto.FormSubmissions)
                {
                    if (s.FormSlug is null || !slugToId.TryGetValue(s.FormSlug, out var fid)) continue;
                    _db.FormSubmissions.Add(new FormSubmission
                    {
                        FormId = fid,
                        DataJson = string.IsNullOrWhiteSpace(s.DataJson) ? "[]" : s.DataJson!,
                        IsRead = s.IsRead,
                        CreatedAt = s.CreatedAt == default ? DateTime.UtcNow : s.CreatedAt
                    });
                    count++;
                }
                if (count > 0) { await _db.SaveChangesAsync(); summary.Add($"{count} Formular-Einsendungen"); }
            }
        }
        else if (dto.Forms is not null)
        {
            // Removing forms cascades to their submissions; re-added below if present.
            _db.FormSubmissions.RemoveRange(_db.FormSubmissions);
            _db.Forms.RemoveRange(_db.Forms);
            await _db.SaveChangesAsync();
            foreach (var f in dto.Forms.Where(f => !string.IsNullOrWhiteSpace(f.Slug)))
                _db.Forms.Add(new Form
                {
                    Name = f.Name ?? "Formular",
                    Slug = f.Slug!,
                    DefinitionJson = string.IsNullOrWhiteSpace(f.DefinitionJson) ? "[]" : f.DefinitionJson!,
                    SuccessMessage = f.SuccessMessage,
                    SubmitLabel = f.SubmitLabel,
                    NotifyEnabled = f.NotifyEnabled,
                    NotifyJson = f.NotifyJson ?? "",
                    CreatedAt = f.CreatedAt == default ? DateTime.UtcNow : f.CreatedAt
                });
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Forms.Count} Formulare");
        }

        // Generic submissions restore — skipped when a partial forms restore already handled them.
        if (dto.FormSubmissions is not null && !partialForms)
        {
            var slugToId = await _db.Forms.ToDictionaryAsync(f => f.Slug, f => f.Id);
            // If forms were part of this import, their old submissions are already gone.
            // Otherwise replace all submissions to keep the import deterministic.
            if (dto.Forms is null)
            {
                _db.FormSubmissions.RemoveRange(_db.FormSubmissions);
                await _db.SaveChangesAsync();
            }
            var count = 0;
            foreach (var s in dto.FormSubmissions)
            {
                if (s.FormSlug is null || !slugToId.TryGetValue(s.FormSlug, out var fid)) continue;
                _db.FormSubmissions.Add(new FormSubmission
                {
                    FormId = fid,
                    DataJson = string.IsNullOrWhiteSpace(s.DataJson) ? "[]" : s.DataJson!,
                    IsRead = s.IsRead,
                    CreatedAt = s.CreatedAt == default ? DateTime.UtcNow : s.CreatedAt
                });
                count++;
            }
            await _db.SaveChangesAsync();
            summary.Add($"{count} Formular-Einsendungen");
        }

        if (dto.Media is not null)
        {
            _db.Media.RemoveRange(_db.Media);
            await _db.SaveChangesAsync();
            foreach (var m in dto.Media)
            {
                if (string.IsNullOrWhiteSpace(m.Url)) continue;
                _db.Media.Add(new Media
                {
                    Url = m.Url!,
                    FileName = m.FileName ?? "",
                    Alt = m.Alt,
                    Tags = m.Tags ?? "",
                    ContentType = m.ContentType ?? "",
                    SizeBytes = m.SizeBytes,
                    SortOrder = m.SortOrder,
                    CreatedAt = m.CreatedAt == default ? DateTime.UtcNow : m.CreatedAt
                });
            }
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Media.Count} Medien");
        }

        if (dto.Posts is not null)
        {
            _db.Posts.RemoveRange(_db.Posts);
            await _db.SaveChangesAsync();
            foreach (var p in dto.Posts)
            {
                if (string.IsNullOrWhiteSpace(p.Slug)) continue;
                _db.Posts.Add(new Post
                {
                    Title = p.Title ?? "", Slug = p.Slug!, TitleImage = p.TitleImage,
                    Excerpt = p.Excerpt ?? "", ContentHtml = p.ContentHtml ?? "", Tags = p.Tags ?? "",
                    AttachmentsJson = string.IsNullOrWhiteSpace(p.AttachmentsJson) ? "[]" : p.AttachmentsJson!,
                    Locale = string.IsNullOrWhiteSpace(p.Locale) ? "de" : p.Locale!,
                    IsPublished = p.IsPublished,
                    PublishedAt = p.PublishedAt == default ? DateTime.UtcNow : p.PublishedAt,
                    CreatedAt = p.CreatedAt == default ? DateTime.UtcNow : p.CreatedAt,
                    UpdatedAt = p.UpdatedAt == default ? DateTime.UtcNow : p.UpdatedAt
                });
            }
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Posts.Count} Beiträge");
        }

        // Plugins: UPSERT by Key — the same identity the cloud rollout uses (PluginPackager.ImportAsync),
        // so a plugin restored here and the same plugin pushed from a profile land on one row.
        //
        // Deliberately NOT a replace-all like the sections above: plugin code is the one payload that can
        // be authored on the site itself, and wiping the ones a backup happens not to carry would destroy
        // exactly the work this section was added to protect.
        //
        // A plugin that is NEW here comes back DISABLED. Plugin code runs server-side, and a restore is
        // not a review — the same rule the cloud rollout follows ("imported plugins stay disabled"). An
        // EXISTING plugin keeps whatever state it has: it was already reviewed and switched on here, and
        // a nightly restore that silently turned the site's plugins off would be its own outage.
        if (dto.Plugins is not null)
        {
            var existingPlugins = await _db.Plugins.ToListAsync();
            var n = 0;
            var disabled = 0;
            foreach (var p in dto.Plugins)
            {
                var key = (p.Key ?? "").Trim();
                if (key.Length == 0) continue;
                var row = existingPlugins.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    row = new Plugin { Key = key, Enabled = false, CreatedAt = p.CreatedAt == default ? DateTime.UtcNow : p.CreatedAt };
                    _db.Plugins.Add(row);
                    existingPlugins.Add(row);   // same key twice in one backup updates one row, no duplicate insert
                    disabled++;
                }
                row.Name = string.IsNullOrWhiteSpace(p.Name) ? key : p.Name!;
                row.Description = p.Description ?? "";
                row.Version = p.Version ?? "";
                row.DataVersion = p.DataVersion ?? "";
                row.Code = p.Code ?? "";
                row.FilesJson = string.IsNullOrWhiteSpace(p.FilesJson) ? "{}" : p.FilesJson!;
                // LEER bleibt leer: eine leere Rollenkarte heißt „nie geschrieben“ und wird als
                // Bestand gelesen. Sie hier auf „{}“ zu setzen hieße, jedem alten Backup beim
                // Einspielen den Assets-Ordner wegzunehmen.
                row.MappingJson = p.MappingJson ?? "";
                row.ConfigJson = string.IsNullOrWhiteSpace(p.ConfigJson) ? "{}" : p.ConfigJson!;
                n++;
            }
            await _db.SaveChangesAsync();
            summary.Add(disabled > 0 ? $"{n} Plugins ({disabled} deaktiviert)" : $"{n} Plugins");
        }

        // Users: UPSERT by username (never a replace-all) so the current admin can't be locked out.
        if (dto.Users is not null)
        {
            var existingUsers = await _db.Users.ToListAsync();
            var n = 0;
            foreach (var u in dto.Users)
            {
                var uname = (u.Username ?? "").Trim();
                if (uname.Length == 0 || string.IsNullOrWhiteSpace(u.PasswordHash)) continue;
                var row = existingUsers.FirstOrDefault(x => string.Equals(x.Username, uname, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    row = new User { Username = uname };
                    _db.Users.Add(row);
                    existingUsers.Add(row);
                }
                row.PasswordHash = u.PasswordHash!;
                row.Role = string.IsNullOrWhiteSpace(u.Role) ? "Admin" : u.Role!;
                row.DisplayName = u.DisplayName;
                row.Email = u.Email;
                if (u.CreatedAt != default) row.CreatedAt = u.CreatedAt;
                n++;
            }
            await _db.SaveChangesAsync();
            summary.Add($"{n} Benutzer");
        }

        await tx.CommitAsync();
        return summary.Count == 0 ? "Nichts importiert" : string.Join(", ", summary) + " wiederhergestellt";
    }

    /// <summary>Copies a backed-up template's style fields onto an entity (Name set; IsActive left to the caller).</summary>
    private static void ApplyTemplate(Template row, TemplateDto t)
    {
        if (!string.IsNullOrWhiteSpace(t.Name)) row.Name = t.Name!;
        row.AccentColor = string.IsNullOrWhiteSpace(t.AccentColor) ? "#2563eb" : t.AccentColor!;
        row.HeadingFont = string.IsNullOrWhiteSpace(t.HeadingFont) ? "Geologica" : t.HeadingFont!;
        row.BodyFont = string.IsNullOrWhiteSpace(t.BodyFont) ? "Inter" : t.BodyFont!;
        row.ButtonStyle = string.IsNullOrWhiteSpace(t.ButtonStyle) ? "solid" : t.ButtonStyle!;
        row.SecondaryColor = t.SecondaryColor ?? "";
        row.HeadingColor = string.IsNullOrWhiteSpace(t.HeadingColor) ? "#010101" : t.HeadingColor!;
        row.TextColor = string.IsNullOrWhiteSpace(t.TextColor) ? "#1a1a1a" : t.TextColor!;
        row.BackgroundColor = string.IsNullOrWhiteSpace(t.BackgroundColor) ? "#ffffff" : t.BackgroundColor!;
        row.AltBackground = string.IsNullOrWhiteSpace(t.AltBackground) ? "#f6f7f9" : t.AltBackground!;
        row.ContainerWidth = string.IsNullOrWhiteSpace(t.ContainerWidth) ? "1180" : t.ContainerWidth!;
        row.ButtonRadius = string.IsNullOrWhiteSpace(t.ButtonRadius) ? "0" : t.ButtonRadius!;
        row.HeaderBackground = t.HeaderBackground ?? "";
        row.HeaderTextColor = t.HeaderTextColor ?? "";
        row.HeaderPadding = string.IsNullOrWhiteSpace(t.HeaderPadding) ? "16" : t.HeaderPadding!;
        row.CustomCss = t.CustomCss ?? "";
        row.CustomJs = t.CustomJs ?? "";
        row.LayoutHtml = t.LayoutHtml ?? "";
        // Absent ≠ empty: an older backup carries no LoginHtml, and blanking an existing row's custom
        // login page on that basis would be wrong. Only an included value (empty allowed) is applied.
        if (t.LoginHtml is not null) row.LoginHtml = t.LoginHtml;
        row.MenuMapJson = string.IsNullOrWhiteSpace(t.MenuMapJson) ? "{}" : t.MenuMapJson!;
        // Here the row may already EXIST and carry parameters, so absent ≠ empty: a backup written
        // before these two fields existed contains no such property (null), and overwriting the row
        // with "[]"/"{}" on that basis would destroy exactly what this fix is about. Only a value the
        // backup actually contains is applied — an explicitly empty one included, because clearing
        // the parameters is a legitimate state the operator may have backed up.
        if (t.ParametersJson is not null)
            row.ParametersJson = string.IsNullOrWhiteSpace(t.ParametersJson) ? "[]" : t.ParametersJson;
        if (t.ParamValuesJson is not null)
            row.ParamValuesJson = string.IsNullOrWhiteSpace(t.ParamValuesJson) ? "{}" : t.ParamValuesJson;
        row.SchemaVersion = t.SchemaVersion <= 0 ? 1 : t.SchemaVersion;
        row.PartsJson = string.IsNullOrWhiteSpace(t.PartsJson) ? "{}" : t.PartsJson!;
        MatCMS.Content.TemplateSchema.Upgrade(row); // bring older backups up to the current format
    }

    /// <summary>Builds the nested block DTO tree for a page: top-level blocks (parentId == null),
    /// each carrying its children recursively. Ordered by SortOrder at every level.</summary>
    private static List<BlockDto> BuildBlockDtos(IEnumerable<ContentBlock> all, int? parentId)
    {
        var list = all as IReadOnlyCollection<ContentBlock> ?? all.ToList();
        return list.Where(b => b.ParentId == parentId).OrderBy(b => b.SortOrder).Select(b => new BlockDto
        {
            BlockType = b.BlockType,
            SortOrder = b.SortOrder,
            DataJson = b.DataJson,
            Children = BuildBlockDtos(list, b.Id) is { Count: > 0 } c ? c : null
        }).ToList();
    }

    /// <summary>Inserts a block tree under a page, preserving nesting: each block is saved to obtain
    /// its Id before its children are inserted with that Id as their ParentId. Returns the block count.</summary>
    private async Task<int> InsertBlockTreeAsync(List<BlockDto>? dtos, int pageId, int? parentId)
    {
        var count = 0;
        foreach (var b in dtos ?? new())
        {
            var cb = new ContentBlock
            {
                PageId = pageId,
                ParentId = parentId,
                BlockType = b.BlockType ?? "",
                SortOrder = b.SortOrder,
                DataJson = string.IsNullOrWhiteSpace(b.DataJson) ? "{}" : b.DataJson!
            };
            _db.ContentBlocks.Add(cb);
            await _db.SaveChangesAsync();
            count++;
            if (b.Children is { Count: > 0 })
                count += await InsertBlockTreeAsync(b.Children, pageId, cb.Id);
        }
        return count;
    }

    /// <summary>Copies a backed-up page's scalar fields onto an entity (Blocks handled by the caller).</summary>
    private static void ApplyPageFields(Page row, PageDto p, string locale)
    {
        row.Title = p.Title ?? "";
        row.Slug = (p.Slug ?? "").Trim();
        row.Locale = locale;
        if (string.IsNullOrWhiteSpace(row.TranslationGroup))
            row.TranslationGroup = string.IsNullOrWhiteSpace(p.TranslationGroup)
                ? Guid.NewGuid().ToString("N") : p.TranslationGroup;
        row.NavLabel = p.NavLabel;
        row.IsPublished = p.IsPublished;
        row.ShowInNav = p.ShowInNav;
        row.ShowInFooter = p.ShowInFooter;
        row.NavOrder = p.NavOrder;
        row.FooterOrder = p.FooterOrder;
        row.MetaDescription = p.MetaDescription;
        row.CustomCss = p.CustomCss;
        // Absent Access reads as Public (an older backup has no such property), so an existing members-only
        // page is only ever demoted to public when the backup actually says so. TemplateId is NOT set here
        // — it needs the imported templates' fresh ids and is resolved by name in the page-import loop.
        row.Access = p.Access ?? PageAccess.Public;
        row.RequiredRole = p.RequiredRole;
        row.TemplateParamsJson = p.TemplateParamsJson;
        row.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reads a backup file (ZIP or legacy JSON) WITHOUT importing it, returning a summary of its
    /// contents so a restore can be reviewed first. Throws on an unreadable file.
    /// </summary>
    public BackupInfo Inspect(byte[] data)
    {
        if (data is null || data.Length == 0)
            throw new InvalidOperationException("Die Datei ist leer.");

        var isZip = data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;
        string json;
        var hasAssets = false;
        var pluginAssets = 0;
        if (isZip)
        {
            using var ms = new MemoryStream(data);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var contentEntry = zip.GetEntry("content.json")
                ?? throw new InvalidOperationException("Das ZIP enthält keine content.json.");
            using var r = new StreamReader(contentEntry.Open(), Encoding.UTF8);
            json = r.ReadToEnd();
            hasAssets = zip.Entries.Any(e => e.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                                             && !string.IsNullOrWhiteSpace(Path.GetFileName(e.FullName)));
            pluginAssets = zip.Entries.Count(e => e.FullName.StartsWith(PluginAssetFolder, StringComparison.OrdinalIgnoreCase)
                                                  && !string.IsNullOrWhiteSpace(Path.GetFileName(e.FullName)));
        }
        else
        {
            json = Encoding.UTF8.GetString(data);
        }

        TransferDto? dto;
        try { dto = JsonSerializer.Deserialize<TransferDto>(json, ReadOpts); }
        catch (JsonException ex) { throw new InvalidOperationException($"Ungültige JSON-Datei: {ex.Message}"); }
        if (dto is null) throw new InvalidOperationException("Kein bekanntes Backup-Format.");

        return new BackupInfo
        {
            Version = dto.Version,
            ExportedAtUtc = dto.ExportedAtUtc,
            TemplateNames = dto.Templates?.Select(t => t.Name ?? "Template").ToList() ?? new(),
            Pages = dto.Pages?.Count ?? 0,
            Menus = dto.Menus?.Count ?? 0,
            MenuItems = dto.MenuItems?.Count ?? 0,
            Settings = dto.Settings?.Count ?? 0,
            Submissions = dto.Submissions?.Count ?? 0,
            Forms = dto.Forms?.Count ?? 0,
            Media = dto.Media?.Count ?? 0,
            Components = dto.Components?.Count ?? 0,
            Plugins = dto.Plugins?.Count ?? 0,
            Users = dto.Users?.Count ?? 0,
            HasAssets = hasAssets,
            PluginAssets = pluginAssets,
            TemplatesPartial = dto.TemplatesPartial,
            PagesPartial = dto.PagesPartial,
            FormsPartial = dto.FormsPartial
        };
    }

    // ------------------------------------------------------------------
    // Transfer DTOs (stable JSON shape, independent of the EF entities)
    // ------------------------------------------------------------------
    private sealed class TransferDto
    {
        public int Version { get; set; }
        public string ExportedAtUtc { get; set; } = "";
        /// <summary>True when the export was narrowed to specific items → restore upserts those items
        /// by their key instead of replacing the whole table (so it can't wipe the untouched rest).</summary>
        public bool TemplatesPartial { get; set; }
        public bool PagesPartial { get; set; }
        public bool FormsPartial { get; set; }
        public List<TemplateDto>? Templates { get; set; }
        public List<PageDto>? Pages { get; set; }
        public List<MenuDto>? Menus { get; set; }
        public List<MenuItemDto>? MenuItems { get; set; }
        public List<SettingDto>? Settings { get; set; }
        public List<SubmissionDto>? Submissions { get; set; }
        public List<FormDto>? Forms { get; set; }
        public List<FormSubmissionDto>? FormSubmissions { get; set; }
        public List<MediaDto>? Media { get; set; }
        public List<ComponentDto>? Components { get; set; }
        public List<UserDto>? Users { get; set; }
        public List<PostDto>? Posts { get; set; }
        public List<PluginDto>? Plugins { get; set; }
    }

    private sealed class PluginDto
    {
        /// <summary>Identity on restore — the plugin's stable slug, same as the cloud rollout uses.</summary>
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? DataVersion { get; set; }
        /// <summary>The entry script.</summary>
        public string? Code { get; set; }
        /// <summary>Further script files (path → content) reachable from Code via <c>#load</c>.</summary>
        public string? FilesJson { get; set; }
        /// <summary>Die Rollen der Ordner (Ordnerpfad → Rolle). Leer = nie geschrieben, wird als
        /// Bestand gelesen — siehe <see cref="MatCMS.Services.PluginMapping"/>.</summary>
        public string? MappingJson { get; set; }
        public string? ConfigJson { get; set; }
        /// <summary>State at the time of the backup. Recorded for the record only — the import never
        /// applies it (a new plugin lands disabled, an existing one keeps its own state).</summary>
        public bool Enabled { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class PostDto
    {
        public string? Title { get; set; }
        public string? Slug { get; set; }
        public string? TitleImage { get; set; }
        public string? Excerpt { get; set; }
        public string? ContentHtml { get; set; }
        public string? Tags { get; set; }
        public string? AttachmentsJson { get; set; }
        public string? Locale { get; set; }
        public bool IsPublished { get; set; }
        public DateTime PublishedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class UserDto
    {
        public string? Username { get; set; }
        public string? PasswordHash { get; set; }
        public string? Role { get; set; }
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class FormDto
    {
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public string? DefinitionJson { get; set; }
        public string? SuccessMessage { get; set; }
        public string? SubmitLabel { get; set; }
        public bool NotifyEnabled { get; set; }
        public string? NotifyJson { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class FormSubmissionDto
    {
        public string? FormSlug { get; set; }
        public string? DataJson { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class TemplateDto
    {
        public string? Name { get; set; }
        public bool IsActive { get; set; }
        public string? AccentColor { get; set; }
        public string? HeadingFont { get; set; }
        public string? BodyFont { get; set; }
        public string? ButtonStyle { get; set; }
        public string? SecondaryColor { get; set; }
        public string? HeadingColor { get; set; }
        public string? TextColor { get; set; }
        public string? BackgroundColor { get; set; }
        public string? AltBackground { get; set; }
        public string? ContainerWidth { get; set; }
        public string? ButtonRadius { get; set; }
        public string? HeaderBackground { get; set; }
        public string? HeaderTextColor { get; set; }
        public string? HeaderPadding { get; set; }
        public string? CustomCss { get; set; }
        public string? CustomJs { get; set; }
        public string? LayoutHtml { get; set; }
        /// <summary>The template's custom /anmelden page. Nullable for the same reason as the fields
        /// below: an older backup has no such property, and "absent" must be read as "leave as default",
        /// not "blank the login page".</summary>
        public string? LoginHtml { get; set; }
        /// <summary>Files attached to the template ({{asset:name}} → /template-assets/{id}/{name}), bytes
        /// and all, so a self-hosted script/font survives backup→restore. Null on an older backup.</summary>
        public List<TemplateAssetDto>? Assets { get; set; }
        public string? MenuMapJson { get; set; }
        /// <summary>The parameter SCHEMA a template designer published ({{param:id}}), and below it the
        /// values this site's admin set on them. Both were missing here while every other carrier of a
        /// template (the editor's JSON export, the wire contract, the cloud's store and profile rows)
        /// took them along — so a full restore, which drops all templates and rebuilds them from this
        /// DTO, silently handed the site back a theme with no parameters and no values.
        /// <para>Deliberately nullable, unlike most fields here: a backup written before these existed
        /// contains no such property, and "not contained" must not be applied as "set it to empty".</para></summary>
        public string? ParametersJson { get; set; }
        /// <inheritdoc cref="ParametersJson"/>
        public string? ParamValuesJson { get; set; }
        public int SchemaVersion { get; set; }
        public string? PartsJson { get; set; }
    }

    private sealed class TemplateAssetDto
    {
        public string? Name { get; set; }
        public string? ContentType { get; set; }
        /// <summary>The file's bytes, Base64-encoded (JSON has no byte type).</summary>
        public string? Base64 { get; set; }
    }

    private sealed class PageDto
    {
        public string? Title { get; set; }
        public string? Slug { get; set; }
        public string? Locale { get; set; }
        public string? TranslationGroup { get; set; }
        public string? NavLabel { get; set; }
        public bool IsPublished { get; set; }
        public bool ShowInNav { get; set; }
        public bool ShowInFooter { get; set; }
        public int NavOrder { get; set; }
        public int FooterOrder { get; set; }
        public string? MetaDescription { get; set; }
        public string? CustomCss { get; set; }
        /// <summary>The page's own template, exported by NAME (not Id): a restore rebuilds templates with
        /// fresh ids, so the link is re-resolved against the imported template names. Null = the site's
        /// active template, exactly as an older backup (which has no such property) restores.</summary>
        public string? TemplateName { get; set; }
        /// <summary>Public vs. members-only, and the required member role — otherwise a restore would
        /// silently make every members-only page public. Nullable Access so absent reads as Public.</summary>
        public PageAccess? Access { get; set; }
        public string? RequiredRole { get; set; }
        /// <summary>Per-page template parameter overrides (JSON). Null on an older backup.</summary>
        public string? TemplateParamsJson { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<BlockDto>? Blocks { get; set; }
    }

    private sealed class BlockDto
    {
        public string? BlockType { get; set; }
        public int SortOrder { get; set; }
        public string? DataJson { get; set; }
        /// <summary>Nested child blocks (container blocks). Null/absent for leaf and legacy-flat blocks.</summary>
        public List<BlockDto>? Children { get; set; }
    }

    private sealed class ComponentDto
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? FieldsJson { get; set; }
        public string? TemplateHtml { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class MediaDto
    {
        public string? Url { get; set; }
        public string? FileName { get; set; }
        public string? Alt { get; set; }
        public string? Tags { get; set; }
        public string? ContentType { get; set; }
        public long SizeBytes { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class MenuDto
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public int SortOrder { get; set; }
        public bool BuiltIn { get; set; }
    }

    private sealed class MenuItemDto
    {
        /// <summary>Original id — used only to re-link ParentId on import (ids regenerate).</summary>
        public int Id { get; set; }
        /// <summary>Original parent id (hierarchical menus); remapped to the new item on import.</summary>
        public int? ParentId { get; set; }
        public string? Menu { get; set; }
        public string? Label { get; set; }
        public string? Url { get; set; }
        public int SortOrder { get; set; }
        public bool OpenInNewTab { get; set; }
        public string? Locale { get; set; }
        public string? Icon { get; set; }
    }

    private sealed class SettingDto
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
    }

    private sealed class SubmissionDto
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Category { get; set; }
        public string? Message { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
