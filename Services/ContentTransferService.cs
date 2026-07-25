using System.Text.Encodings.Web;
using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Backup &amp; Restore of the site content. Each section can be included independently:
/// Templates, Pages (incl. their ContentBlocks), Menu items, Site settings and contact submissions.
/// Users are deliberately excluded for security reasons.
/// On restore, only the sections present in the file are replaced; missing sections are left untouched.
/// </summary>
public class ContentTransferService
{
    private const int CurrentVersion = 2;

    private readonly AppDbContext _db;
    public ContentTransferService(AppDbContext db) => _db = db;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Which sections to include in a backup.</summary>
    public sealed class BackupOptions
    {
        public bool Templates { get; set; } = true;
        public bool Pages { get; set; } = true;
        public bool Menus { get; set; } = true;
        public bool Settings { get; set; } = true;
        public bool Submissions { get; set; } = true;

        public bool Any => Templates || Pages || Menus || Settings || Submissions;
    }

    public async Task<string> ExportAsync(BackupOptions options, string? exportedAtUtc = null)
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
                    Name = t.Name,
                    IsActive = t.IsActive,
                    AccentColor = t.AccentColor,
                    HeadingFont = t.HeadingFont,
                    BodyFont = t.BodyFont,
                    ButtonStyle = t.ButtonStyle
                }).ToList();
        }

        if (options.Pages)
        {
            var pages = await _db.Pages.AsNoTracking().Include(p => p.Blocks).OrderBy(p => p.Id).ToListAsync();
            dto.Pages = pages.Select(p => new PageDto
            {
                Title = p.Title,
                Slug = p.Slug,
                NavLabel = p.NavLabel,
                IsPublished = p.IsPublished,
                ShowInNav = p.ShowInNav,
                ShowInFooter = p.ShowInFooter,
                NavOrder = p.NavOrder,
                FooterOrder = p.FooterOrder,
                MetaDescription = p.MetaDescription,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
                Blocks = p.Blocks.OrderBy(b => b.SortOrder).Select(b => new BlockDto
                {
                    BlockType = b.BlockType,
                    SortOrder = b.SortOrder,
                    DataJson = b.DataJson
                }).ToList()
            }).ToList();
        }

        if (options.Menus)
        {
            dto.MenuItems = (await _db.MenuItems.AsNoTracking().OrderBy(m => m.Menu).ThenBy(m => m.SortOrder).ToListAsync())
                .Select(m => new MenuItemDto
                {
                    Menu = m.Menu, Label = m.Label, Url = m.Url, SortOrder = m.SortOrder, OpenInNewTab = m.OpenInNewTab
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

        return JsonSerializer.Serialize(dto, WriteOpts);
    }

    /// <summary>
    /// Restores the sections present in the file (transactional). Each present section replaces
    /// the corresponding table; sections not in the file are left untouched.
    /// </summary>
    public async Task<string> ImportAsync(string json)
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
                            && dto.Settings is null && dto.Submissions is null))
            throw new InvalidOperationException(
                "Die Datei hat kein bekanntes Backup-Format (keine Abschnitte gefunden).");

        var summary = new List<string>();

        await using var tx = await _db.Database.BeginTransactionAsync();

        if (dto.Templates is not null)
        {
            _db.Templates.RemoveRange(_db.Templates);
            await _db.SaveChangesAsync();
            foreach (var t in dto.Templates)
            {
                _db.Templates.Add(new Template
                {
                    Name = t.Name ?? "Template",
                    IsActive = t.IsActive,
                    AccentColor = string.IsNullOrWhiteSpace(t.AccentColor) ? "#2563eb" : t.AccentColor!,
                    HeadingFont = string.IsNullOrWhiteSpace(t.HeadingFont) ? "Geologica" : t.HeadingFont!,
                    BodyFont = string.IsNullOrWhiteSpace(t.BodyFont) ? "Inter" : t.BodyFont!,
                    ButtonStyle = string.IsNullOrWhiteSpace(t.ButtonStyle) ? "solid" : t.ButtonStyle!
                });
            }
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
                    Label = m.Label ?? "", Url = m.Url ?? "", SortOrder = m.SortOrder, OpenInNewTab = m.OpenInNewTab
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

        await tx.CommitAsync();
        return summary.Count == 0 ? "Nichts importiert." : string.Join(", ", summary) + " wiederhergestellt.";
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
    }

    private sealed class TemplateDto
    {
        public string? Name { get; set; }
        public bool IsActive { get; set; }
        public string? AccentColor { get; set; }
        public string? HeadingFont { get; set; }
        public string? BodyFont { get; set; }
        public string? ButtonStyle { get; set; }
    }

    private sealed class PageDto
    {
        public string? Title { get; set; }
        public string? Slug { get; set; }
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
