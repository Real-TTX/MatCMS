using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>
/// "Rückgängig" for a whole run of the assistant: every entity the run touched goes back to the snapshot taken before its
/// first change (<see cref="AiChange"/>), created ones are removed. Blocks and menu entries come back WITH THEIR OLD IDS —
/// SQLite never hands out an id twice, so they are free, and keeping them means the conversation's earlier answers ("block
/// 42") still point at the right thing.
/// <para>Only the NEWEST run with changes may be undone (and then the one before it, and so on): run 3 may have built on
/// what run 2 did, and restoring run 2's snapshot under it would quietly throw run 3 away too.</para>
/// </summary>
public sealed class AssistantUndo
{
    private readonly AppDbContext _db;
    public AssistantUndo(AppDbContext db) => _db = db;

    public sealed record BlockRow(int Id, int? ParentId, string Type, string Data, int Sort);
    public sealed record MenuRow(int Id, int? ParentId, string Label, string Url, int Sort, bool NewTab, string? Icon, string Visibility);

    private sealed record PageState(
        int Id, string Title, string Slug, string Locale, string? TranslationGroup, string? NavLabel, bool IsPublished, bool ShowInNav, bool ShowInFooter,
        int NavOrder, int FooterOrder, string? MetaDescription, int? TemplateId, string? TemplateParamsJson, string? CustomCss, PageAccess Access,
        string? RequiredRole, DateTime CreatedAt, List<BlockRow> Blocks);

    private sealed record TemplateState(
        string AccentColor, string SecondaryColor, string HeadingColor, string TextColor, string BackgroundColor, string AltBackground,
        string HeaderBackground, string HeaderTextColor, string HeadingFont, string BodyFont, string ButtonStyle, string ButtonRadius,
        string ContainerWidth, string HeaderPadding, string CustomCss, string ParamValuesJson);

    public static string PageSnapshot(Page p, List<ContentBlock> blocks) => JsonSerializer.Serialize(new PageState(
        p.Id, p.Title, p.Slug, p.Locale, p.TranslationGroup, p.NavLabel, p.IsPublished, p.ShowInNav, p.ShowInFooter, p.NavOrder, p.FooterOrder,
        p.MetaDescription, p.TemplateId, p.TemplateParamsJson, p.CustomCss, p.Access, p.RequiredRole, p.CreatedAt,
        blocks.Select(b => new BlockRow(b.Id, b.ParentId, b.BlockType, b.DataJson, b.SortOrder)).ToList()));

    public static string TemplateSnapshot(Template t) => JsonSerializer.Serialize(new TemplateState(
        t.AccentColor, t.SecondaryColor, t.HeadingColor, t.TextColor, t.BackgroundColor, t.AltBackground, t.HeaderBackground, t.HeaderTextColor,
        t.HeadingFont, t.BodyFont, t.ButtonStyle, t.ButtonRadius, t.ContainerWidth, t.HeaderPadding, t.CustomCss, t.ParamValuesJson));

    /// <summary>The newest run of the conversation that still has changes to undo, or null.</summary>
    public async Task<int?> UndoableRunAsync(int conversationId, CancellationToken ct = default) =>
        await _db.AiChanges.Where(c => c.ConversationId == conversationId && !c.Reverted).MaxAsync(c => (int?)c.Run, ct);

    /// <summary>Undoes one run. Returns what was restored, as lines for the conversation.</summary>
    public async Task<List<string>> RevertAsync(int conversationId, int run, CancellationToken ct = default)
    {
        if (await UndoableRunAsync(conversationId, ct) != run)
            throw new InvalidOperationException("Nur der letzte Auftrag mit Änderungen kann rückgängig gemacht werden.");
        var changes = await _db.AiChanges.Where(c => c.ConversationId == conversationId && c.Run == run && !c.Reverted)
            .OrderByDescending(c => c.Id).ToListAsync(ct);
        var lines = new List<string>();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        foreach (var c in changes)
        {
            switch (c.Kind)
            {
                case "page": lines.Add(await RevertPageAsync(c, ct)); break;
                case "menu": lines.Add(await RevertMenuAsync(c, ct)); break;
                case "template": lines.Add(await RevertTemplateAsync(c, ct)); break;
                case "setting": lines.Add(await RevertSettingAsync(c, ct)); break;
                case "media": lines.Add(await RevertMediaAsync(c, ct)); break;
            }
            c.Reverted = true;
            await _db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return lines;
    }

    private async Task<string> RevertPageAsync(AiChange c, CancellationToken ct)
    {
        var id = int.Parse(c.Key);
        var current = await _db.Pages.FirstOrDefaultAsync(p => p.Id == id, ct);
        var currentBlocks = await _db.ContentBlocks.Where(b => b.PageId == id).ToListAsync(ct);
        _db.ContentBlocks.RemoveRange(currentBlocks);
        if (c.BeforeJson is null)
        {
            // The run created the page: it goes.
            if (current is not null) _db.Pages.Remove(current);
            await _db.SaveChangesAsync(ct);
            return current is null ? "" : $"Seite „{current.Title}“ entfernt";
        }
        var s = JsonSerializer.Deserialize<PageState>(c.BeforeJson)!;
        await _db.SaveChangesAsync(ct);
        var page = current;
        if (page is null)
        {
            // The run deleted it: it comes back under its old id.
            page = new Page { Id = s.Id };
            _db.Pages.Add(page);
        }
        page.Title = s.Title; page.Slug = s.Slug; page.Locale = s.Locale; page.TranslationGroup = s.TranslationGroup; page.NavLabel = s.NavLabel;
        page.IsPublished = s.IsPublished; page.ShowInNav = s.ShowInNav; page.ShowInFooter = s.ShowInFooter; page.NavOrder = s.NavOrder;
        page.FooterOrder = s.FooterOrder; page.MetaDescription = s.MetaDescription; page.TemplateId = s.TemplateId;
        page.TemplateParamsJson = s.TemplateParamsJson; page.CustomCss = s.CustomCss; page.Access = s.Access; page.RequiredRole = s.RequiredRole;
        page.CreatedAt = s.CreatedAt; page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        // Parents before children, so every ParentId already exists when its child is written.
        var pending = s.Blocks.ToList();
        var placed = new HashSet<int>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(b => b.ParentId is null || placed.Contains(b.ParentId.Value)).ToList();
            if (ready.Count == 0) ready = pending.ToList();   // an orphan in the snapshot: write it rather than loop forever
            foreach (var b in ready)
            {
                _db.ContentBlocks.Add(new ContentBlock { Id = b.Id, PageId = page.Id, ParentId = b.ParentId is int p && (placed.Contains(p) || s.Blocks.Any(x => x.Id == p)) ? p : null, BlockType = b.Type, DataJson = b.Data, SortOrder = b.Sort });
                placed.Add(b.Id);
                pending.Remove(b);
            }
            await _db.SaveChangesAsync(ct);
        }
        return $"Seite „{s.Title}“ wiederhergestellt";
    }

    private async Task<string> RevertMenuAsync(AiChange c, CancellationToken ct)
    {
        var parts = c.Key.Split('|');
        var (menu, locale) = (parts[0], parts.Length > 1 ? parts[1] : Localizer.DefaultCulture);
        _db.MenuItems.RemoveRange(await _db.MenuItems.Where(m => m.Menu == menu && m.Locale == locale).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
        var rows = JsonSerializer.Deserialize<List<MenuRow>>(c.BeforeJson ?? "[]") ?? new();
        var pending = rows.ToList();
        var placed = new HashSet<int>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(r => r.ParentId is null || placed.Contains(r.ParentId.Value)).ToList();
            if (ready.Count == 0) ready = pending.ToList();
            foreach (var r in ready)
            {
                _db.MenuItems.Add(new MenuItem { Id = r.Id, Menu = menu, Locale = locale, ParentId = r.ParentId is int p && placed.Contains(p) ? p : null, Label = r.Label, Url = r.Url, SortOrder = r.Sort, OpenInNewTab = r.NewTab, Icon = r.Icon, Visibility = r.Visibility });
                placed.Add(r.Id);
                pending.Remove(r);
            }
            await _db.SaveChangesAsync(ct);
        }
        return $"Menü „{menu}“ ({locale}) wiederhergestellt";
    }

    private async Task<string> RevertTemplateAsync(AiChange c, CancellationToken ct)
    {
        var t = await _db.Templates.FirstOrDefaultAsync(x => x.Id == int.Parse(c.Key), ct);
        if (t is null || c.BeforeJson is null) return "";
        var s = JsonSerializer.Deserialize<TemplateState>(c.BeforeJson)!;
        t.AccentColor = s.AccentColor; t.SecondaryColor = s.SecondaryColor; t.HeadingColor = s.HeadingColor; t.TextColor = s.TextColor;
        t.BackgroundColor = s.BackgroundColor; t.AltBackground = s.AltBackground; t.HeaderBackground = s.HeaderBackground; t.HeaderTextColor = s.HeaderTextColor;
        t.HeadingFont = s.HeadingFont; t.BodyFont = s.BodyFont; t.ButtonStyle = s.ButtonStyle; t.ButtonRadius = s.ButtonRadius;
        t.ContainerWidth = s.ContainerWidth; t.HeaderPadding = s.HeaderPadding; t.CustomCss = s.CustomCss; t.ParamValuesJson = s.ParamValuesJson;
        await _db.SaveChangesAsync(ct);
        return $"Design „{t.Name}“ wiederhergestellt";
    }

    /// <summary>A picture the run drew leaves the media library (the file itself is left to Aufräumen — it costs a few KB, and a
    /// page outside the run might already point at it).</summary>
    private async Task<string> RevertMediaAsync(AiChange c, CancellationToken ct)
    {
        if (c.BeforeJson is not null) return "";
        var m = await _db.Media.FirstOrDefaultAsync(x => x.Id == int.Parse(c.Key), ct);
        if (m is null) return "";
        _db.Media.Remove(m);
        await _db.SaveChangesAsync(ct);
        return $"Grafik „{m.FileName}“ entfernt";
    }

    private async Task<string> RevertSettingAsync(AiChange c, CancellationToken ct)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == c.Key, ct);
        if (c.BeforeJson is null) { if (row is not null) _db.SiteSettings.Remove(row); }
        else
        {
            var value = JsonSerializer.Deserialize<string>(c.BeforeJson) ?? "";
            if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = c.Key, Value = value }); else row.Value = value;
        }
        await _db.SaveChangesAsync(ct);
        return $"Einstellung „{c.Key}“ wiederhergestellt";
    }
}
