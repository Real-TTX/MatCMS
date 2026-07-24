using System.Text.Encodings.Web;
using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Export / Import (backup &amp; restore) of the site content:
/// Pages (incl. their ContentBlocks), MenuItems and SiteSettings.
/// Users are deliberately excluded for security reasons.
/// </summary>
public class ContentTransferService
{
    private const int CurrentVersion = 1;

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

    /// <summary>Serializes the whole site content to pretty JSON.</summary>
    public async Task<string> ExportAsync(string? exportedAtUtc = null)
    {
        var pages = await _db.Pages
            .AsNoTracking()
            .Include(p => p.Blocks)
            .OrderBy(p => p.Id)
            .ToListAsync();

        var menuItems = await _db.MenuItems
            .AsNoTracking()
            .OrderBy(m => m.Menu).ThenBy(m => m.SortOrder)
            .ToListAsync();

        var settings = await _db.SiteSettings
            .AsNoTracking()
            .OrderBy(s => s.Key)
            .ToListAsync();

        var dto = new TransferDto
        {
            Version = CurrentVersion,
            ExportedAtUtc = exportedAtUtc ?? DateTime.UtcNow.ToString("o"),
            Pages = pages.Select(p => new PageDto
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
                Blocks = p.Blocks
                    .OrderBy(b => b.SortOrder)
                    .Select(b => new BlockDto
                    {
                        BlockType = b.BlockType,
                        SortOrder = b.SortOrder,
                        DataJson = b.DataJson
                    }).ToList()
            }).ToList(),
            MenuItems = menuItems.Select(m => new MenuItemDto
            {
                Menu = m.Menu,
                Label = m.Label,
                Url = m.Url,
                SortOrder = m.SortOrder,
                OpenInNewTab = m.OpenInNewTab
            }).ToList(),
            Settings = settings.Select(s => new SettingDto
            {
                Key = s.Key,
                Value = s.Value
            }).ToList()
        };

        return JsonSerializer.Serialize(dto, WriteOpts);
    }

    /// <summary>
    /// Parses <paramref name="json"/> and restores the content. When
    /// <paramref name="replace"/> is true, all existing Pages/ContentBlocks/
    /// MenuItems/SiteSettings are removed first, then re-created from the file
    /// (ids are regenerated; slugs and sort orders are kept). Runs in a
    /// transaction. Throws a clear message on invalid input.
    /// </summary>
    public async Task<string> ImportAsync(string json, bool replace = true)
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

        if (dto is null)
            throw new InvalidOperationException("Die Datei enthält keine gültigen Daten.");

        if (dto.Pages is null && dto.MenuItems is null && dto.Settings is null)
            throw new InvalidOperationException(
                "Die Datei hat kein bekanntes Format (keine Seiten, Menüs oder Einstellungen gefunden).");

        var pages = dto.Pages ?? new List<PageDto>();
        var menuItems = dto.MenuItems ?? new List<MenuItemDto>();
        var settings = dto.Settings ?? new List<SettingDto>();

        await using var tx = await _db.Database.BeginTransactionAsync();

        if (replace)
        {
            _db.ContentBlocks.RemoveRange(_db.ContentBlocks);
            _db.Pages.RemoveRange(_db.Pages);
            _db.MenuItems.RemoveRange(_db.MenuItems);
            _db.SiteSettings.RemoveRange(_db.SiteSettings);
            await _db.SaveChangesAsync();
        }

        var blockCount = 0;
        foreach (var p in pages)
        {
            var page = new Page
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
                Blocks = (p.Blocks ?? new List<BlockDto>()).Select(b =>
                {
                    blockCount++;
                    return new ContentBlock
                    {
                        BlockType = b.BlockType ?? "",
                        SortOrder = b.SortOrder,
                        DataJson = string.IsNullOrWhiteSpace(b.DataJson) ? "{}" : b.DataJson
                    };
                }).ToList()
            };
            _db.Pages.Add(page);
        }

        foreach (var m in menuItems)
        {
            _db.MenuItems.Add(new MenuItem
            {
                Menu = string.IsNullOrWhiteSpace(m.Menu) ? "header" : m.Menu,
                Label = m.Label ?? "",
                Url = m.Url ?? "",
                SortOrder = m.SortOrder,
                OpenInNewTab = m.OpenInNewTab
            });
        }

        foreach (var s in settings)
        {
            if (string.IsNullOrWhiteSpace(s.Key))
                continue;
            _db.SiteSettings.Add(new SiteSetting { Key = s.Key, Value = s.Value ?? "" });
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return $"{pages.Count} Seiten ({blockCount} Blöcke), " +
               $"{menuItems.Count} Menüeinträge und {settings.Count} Einstellungen importiert.";
    }

    // ------------------------------------------------------------------
    // Transfer DTOs (stable JSON shape, independent of the EF entities)
    // ------------------------------------------------------------------

    private sealed class TransferDto
    {
        public int Version { get; set; }
        public string ExportedAtUtc { get; set; } = "";
        public List<PageDto>? Pages { get; set; }
        public List<MenuItemDto>? MenuItems { get; set; }
        public List<SettingDto>? Settings { get; set; }
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
}
