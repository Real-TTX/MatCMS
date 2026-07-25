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
/// (Templates, Pages incl. ContentBlocks, Menu items, Settings, contact submissions) and,
/// optionally, an <c>assets/</c> folder with the uploaded media (wwwroot/uploads).
/// Users are deliberately excluded for security. On restore, only the sections present are
/// replaced; missing sections/assets are left untouched. Legacy plain-JSON backups are also accepted.
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
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"];

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

        public bool Any => Templates || Pages || Menus || Settings || Submissions || Forms || Assets;
    }

    private string UploadsDir => Path.Combine(_env.WebRootPath, "uploads");

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
        }
        return ms.ToArray();
    }

    private async Task<string> BuildJsonAsync(BackupOptions options, string? exportedAtUtc)
    {
        var dto = new TransferDto
        {
            Version = CurrentVersion,
            ExportedAtUtc = exportedAtUtc ?? DateTime.UtcNow.ToString("o")
        };

        if (options.Templates)
        {
            dto.Templates = (await _db.Templates.AsNoTracking().OrderBy(t => t.Id).ToListAsync())
                .Select(t => new TemplateDto
                {
                    Name = t.Name, IsActive = t.IsActive, AccentColor = t.AccentColor,
                    HeadingFont = t.HeadingFont, BodyFont = t.BodyFont, ButtonStyle = t.ButtonStyle,
                    SecondaryColor = t.SecondaryColor, HeadingColor = t.HeadingColor, TextColor = t.TextColor,
                    BackgroundColor = t.BackgroundColor, AltBackground = t.AltBackground,
                    ContainerWidth = t.ContainerWidth, ButtonRadius = t.ButtonRadius,
                    CustomCss = t.CustomCss, CustomJs = t.CustomJs
                }).ToList();
        }

        if (options.Pages)
        {
            var pages = await _db.Pages.AsNoTracking().Include(p => p.Blocks).OrderBy(p => p.Id).ToListAsync();
            dto.Pages = pages.Select(p => new PageDto
            {
                Title = p.Title, Slug = p.Slug, NavLabel = p.NavLabel,
                Locale = p.Locale, TranslationGroup = p.TranslationGroup,
                IsPublished = p.IsPublished, ShowInNav = p.ShowInNav, ShowInFooter = p.ShowInFooter,
                NavOrder = p.NavOrder, FooterOrder = p.FooterOrder, MetaDescription = p.MetaDescription,
                CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt,
                Blocks = p.Blocks.OrderBy(b => b.SortOrder).Select(b => new BlockDto
                {
                    BlockType = b.BlockType, SortOrder = b.SortOrder, DataJson = b.DataJson
                }).ToList()
            }).ToList();
        }

        if (options.Menus)
        {
            dto.MenuItems = (await _db.MenuItems.AsNoTracking().OrderBy(m => m.Menu).ThenBy(m => m.SortOrder).ToListAsync())
                .Select(m => new MenuItemDto
                {
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
            dto.Forms = forms.Select(f => new FormDto
            {
                Name = f.Name, Slug = f.Slug, DefinitionJson = f.DefinitionJson, CreatedAt = f.CreatedAt
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
        }

        return assetCount > 0 ? $"{summary} ({assetCount} Medien)" : summary;
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

        if (dto is null || (dto.Templates is null && dto.Pages is null && dto.MenuItems is null
                            && dto.Settings is null && dto.Submissions is null
                            && dto.Forms is null && dto.FormSubmissions is null))
            throw new InvalidOperationException(
                "Die Datei hat kein bekanntes Backup-Format (keine Abschnitte gefunden).");

        var summary = new List<string>();

        await using var tx = await _db.Database.BeginTransactionAsync();

        if (dto.Templates is not null)
        {
            _db.Templates.RemoveRange(_db.Templates);
            await _db.SaveChangesAsync();
            foreach (var t in dto.Templates)
                _db.Templates.Add(new Template
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
                    CustomCss = t.CustomCss ?? "",
                    CustomJs = t.CustomJs ?? ""
                });
            await _db.SaveChangesAsync();
            var all = await _db.Templates.ToListAsync();
            if (all.Count > 0 && all.All(t => !t.IsActive)) all[0].IsActive = true;
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Templates.Count} Templates");
        }

        if (dto.Pages is not null)
        {
            _db.ContentBlocks.RemoveRange(_db.ContentBlocks);
            _db.Pages.RemoveRange(_db.Pages);
            await _db.SaveChangesAsync();
            var blockCount = 0;
            foreach (var p in dto.Pages)
            {
                _db.Pages.Add(new Page
                {
                    Title = p.Title ?? "",
                    Slug = p.Slug ?? "",
                    // Legacy backups (pre-P2) carry no locale → treat as the default locale.
                    Locale = string.IsNullOrWhiteSpace(p.Locale) ? Localizer.DefaultCulture : p.Locale!,
                    TranslationGroup = string.IsNullOrWhiteSpace(p.TranslationGroup)
                        ? Guid.NewGuid().ToString("N")
                        : p.TranslationGroup,
                    NavLabel = p.NavLabel,
                    IsPublished = p.IsPublished,
                    ShowInNav = p.ShowInNav,
                    ShowInFooter = p.ShowInFooter,
                    NavOrder = p.NavOrder,
                    FooterOrder = p.FooterOrder,
                    MetaDescription = p.MetaDescription,
                    CreatedAt = p.CreatedAt == default ? DateTime.UtcNow : p.CreatedAt,
                    UpdatedAt = p.UpdatedAt == default ? DateTime.UtcNow : p.UpdatedAt,
                    Blocks = (p.Blocks ?? new()).Select(b =>
                    {
                        blockCount++;
                        return new ContentBlock
                        {
                            BlockType = b.BlockType ?? "",
                            SortOrder = b.SortOrder,
                            DataJson = string.IsNullOrWhiteSpace(b.DataJson) ? "{}" : b.DataJson!
                        };
                    }).ToList()
                });
            }
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Pages.Count} Seiten ({blockCount} Blöcke)");
        }

        if (dto.MenuItems is not null)
        {
            _db.MenuItems.RemoveRange(_db.MenuItems);
            await _db.SaveChangesAsync();
            foreach (var m in dto.MenuItems)
                _db.MenuItems.Add(new MenuItem
                {
                    Menu = string.IsNullOrWhiteSpace(m.Menu) ? "header" : m.Menu!,
                    Label = m.Label ?? "", Url = m.Url ?? "", SortOrder = m.SortOrder, OpenInNewTab = m.OpenInNewTab,
                    Locale = string.IsNullOrWhiteSpace(m.Locale) ? Localizer.DefaultCulture : m.Locale!,
                    Icon = MenuIcons.IsValid(m.Icon) ? m.Icon : null
                });
            await _db.SaveChangesAsync();
            summary.Add($"{dto.MenuItems.Count} Menüeinträge");
        }

        if (dto.Settings is not null)
        {
            _db.SiteSettings.RemoveRange(_db.SiteSettings);
            await _db.SaveChangesAsync();
            foreach (var s in dto.Settings.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
                _db.SiteSettings.Add(new SiteSetting { Key = s.Key!, Value = s.Value ?? "" });
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

        if (dto.Forms is not null)
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
                    CreatedAt = f.CreatedAt == default ? DateTime.UtcNow : f.CreatedAt
                });
            await _db.SaveChangesAsync();
            summary.Add($"{dto.Forms.Count} Formulare");
        }

        if (dto.FormSubmissions is not null)
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

        await tx.CommitAsync();
        return summary.Count == 0 ? "Nichts importiert" : string.Join(", ", summary) + " wiederhergestellt";
    }

    // ------------------------------------------------------------------
    // Transfer DTOs (stable JSON shape, independent of the EF entities)
    // ------------------------------------------------------------------
    private sealed class TransferDto
    {
        public int Version { get; set; }
        public string ExportedAtUtc { get; set; } = "";
        public List<TemplateDto>? Templates { get; set; }
        public List<PageDto>? Pages { get; set; }
        public List<MenuItemDto>? MenuItems { get; set; }
        public List<SettingDto>? Settings { get; set; }
        public List<SubmissionDto>? Submissions { get; set; }
        public List<FormDto>? Forms { get; set; }
        public List<FormSubmissionDto>? FormSubmissions { get; set; }
    }

    private sealed class FormDto
    {
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public string? DefinitionJson { get; set; }
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
        public string? CustomCss { get; set; }
        public string? CustomJs { get; set; }
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
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<BlockDto>? Blocks { get; set; }
    }

    private sealed class BlockDto
    {
        public string? BlockType { get; set; }
        public int SortOrder { get; set; }
        public string? DataJson { get; set; }
    }

    private sealed class MenuItemDto
    {
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
