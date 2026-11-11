using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Takes the JSON a template or component editor exports — pasted under Templates/Komponenten → Importieren, or a package from the MatCMS Store
/// (<see cref="MatcmsStoreClient.InstallDesignAsync"/>). ONE importer for both, so a store package can never be read differently from the same file
/// pasted by hand.
/// </summary>
public static class DesignImport
{
    /// <summary>
    /// A template, identified by its NAME, exactly as in the backup restore — a name that already exists updates that template rather than
    /// leaving two rows nobody can tell apart.
    /// <para><c>IsActive</c> is not part of it, in either direction: which design a site runs is a per-site decision and must not travel with a theme.</para>
    /// <para><b>A field the JSON does not contain is not a field the JSON empties.</b> This used to read every property with a fallback, so pasting the
    /// two-line document the input's own placeholder invites (<c>{ "Name": "…" }</c>) wiped <c>LayoutHtml</c>, <c>CustomCss</c> and the template
    /// parameters off an existing theme — and reported "importiert" while doing it. Refusing such a document outright would be the worse answer: on a
    /// NEW template there is nothing to lose, and a hand-written partial theme is a legitimate thing to paste. So a missing field leaves what stands
    /// (on a new row: the column default, which is what the fallbacks were), and the caller NAMES the fields it left alone.</para>
    /// <para>The tuned parameter values (<c>ParamValuesJson</c>) are this site's: taken from the file only for a NEW template, or when pasted by hand
    /// (<paramref name="keepParamValues"/> false — the operator pasted them on purpose).</para>
    /// </summary>
    public static async Task<(Template Row, bool IsNew, List<string> Missing)> TemplateAsync(AppDbContext db, JsonElement root, CancellationToken ct = default,
        bool keepParamValues = true)
    {
        var name = JsonImport.Text(root, "Name").Trim();
        if (name.Length == 0) throw new ArgumentException("A template needs a name.", nameof(root));
        var row = await db.Templates.FirstOrDefaultAsync(t => t.Name == name, ct);
        var isNew = row is null;
        if (row is null)
        {
            row = new Template { Name = name };
            db.Templates.Add(row);
        }

        // Every setter below runs only when the document actually carries the property; what it did not carry is collected instead. The fallbacks
        // are still passed for the second case Has() does not cover — a property that IS there but holds the wrong JSON kind.
        var missing = new List<string>();
        void Str(string prop, Action<string> set, string fallback = "")
        {
            if (JsonImport.Has(root, prop)) set(JsonImport.Text(root, prop, fallback));
            else missing.Add(prop);
        }
        // Raw, not Text: these are nested blobs. Hand-written JSON writes them as real objects and arrays, our own export writes them as
        // strings — both have to arrive intact.
        void Blob(string prop, Action<string> set, string fallback)
        {
            if (JsonImport.Has(root, prop)) set(JsonImport.Raw(root, prop, fallback));
            else missing.Add(prop);
        }

        var t = row;
        Str("AccentColor", v => t.AccentColor = v, "#de7e11");
        Str("SecondaryColor", v => t.SecondaryColor = v);
        Str("HeadingFont", v => t.HeadingFont = v, "Geologica");
        Str("BodyFont", v => t.BodyFont = v, "Inter");
        Str("ButtonStyle", v => t.ButtonStyle = v, "solid");
        Str("HeadingColor", v => t.HeadingColor = v, "#010101");
        Str("TextColor", v => t.TextColor = v, "#1a1a1a");
        Str("BackgroundColor", v => t.BackgroundColor = v, "#ffffff");
        Str("AltBackground", v => t.AltBackground = v, "#f6f7f9");
        Str("ContainerWidth", v => t.ContainerWidth = v, "1180");
        Str("ButtonRadius", v => t.ButtonRadius = v, "0");
        Str("HeaderBackground", v => t.HeaderBackground = v);
        Str("HeaderTextColor", v => t.HeaderTextColor = v);
        Str("HeaderPadding", v => t.HeaderPadding = v, "16");
        Str("CustomCss", v => t.CustomCss = v);
        Str("CustomJs", v => t.CustomJs = v);
        Str("LayoutHtml", v => t.LayoutHtml = v);
        Blob("MenuMapJson", v => t.MenuMapJson = v, "{}");
        Blob("ParametersJson", v => t.ParametersJson = v, "[]");
        if (isNew || !keepParamValues) Blob("ParamValuesJson", v => t.ParamValuesJson = v, "{}");
        Blob("PartsJson", v => t.PartsJson = v, "{}");
        if (JsonImport.Has(root, "SchemaVersion")) t.SchemaVersion = JsonImport.Int(root, "SchemaVersion", 1);
        else missing.Add("SchemaVersion");

        await db.SaveChangesAsync(ct);
        return (row, isNew, missing);
    }

    /// <summary>
    /// A component, identified by its TYPE, because that is what a {{placeholder}} block on an existing page refers to: importing a type that already
    /// exists has to update it, or the blocks already placed would point at the older of two components. Null with a reason when type or name is missing.
    /// </summary>
    public static async Task<(Component? Row, bool IsNew, string? Error)> ComponentAsync(AppDbContext db, JsonElement root, CancellationToken ct = default)
    {
        var type = JsonImport.Text(root, "Type").Trim().ToLowerInvariant();
        var name = JsonImport.Text(root, "Name").Trim();
        if (type.Length == 0 || name.Length == 0) return (null, false, "Im JSON fehlen Typ oder Name.");

        var row = await db.Components.FirstOrDefaultAsync(c => c.Type == type, ct);
        var isNew = row is null;
        if (row is null)
        {
            row = new Component { Type = type };
            db.Components.Add(row);
        }
        row.Name = name;
        row.Description = JsonImport.Text(root, "Description");
        row.Icon = JsonImport.Text(root, "Icon");
        row.FieldsJson = JsonImport.Raw(root, "FieldsJson", "[]");
        row.TemplateHtml = JsonImport.Text(root, "TemplateHtml");
        await db.SaveChangesAsync(ct);
        return (row, isNew, null);
    }
}
