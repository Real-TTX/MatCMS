using System.Text;
using System.Text.Json;

namespace MatCMS.Shared;

/// <summary>
/// A template or a component in the MatCMS Store (<c>docs/official-store.md</c> S4): ONE JSON file — exactly what the editors' "Als JSON exportieren"
/// writes and their import reads — signed like a plugin package (<see cref="StoreSignature"/>, kind <see cref="KindTemplate"/> or
/// <see cref="KindComponent"/>). The JSON names no key and no version: the version is the store's (and part of what is signed), the key follows
/// from the content by <see cref="Key"/> — so a site can check that the file it got IS the entry the store named, and a package cannot overwrite a
/// template or component of another name. The store server keeps its own copy of the rule (a plugin is written against the plugin API only);
/// <c>MatcmsStoreTests</c> makes both agree.
/// </summary>
public static class StoreItemPackage
{
    public const string KindTemplate = "template", KindComponent = "component";

    /// <summary>A template or component file is small; anything bigger is not one.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>The store key of a template (by its name) or a component (by its type): lower case, umlauts spelled out, every run of other
    /// characters one hyphen, none at the ends.</summary>
    public static string Key(string nameOrType)
    {
        var s = (nameOrType ?? "").Trim().ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>The file as a JSON document, or null when it is not a JSON object (or too big). The caller disposes it.</summary>
    public static JsonDocument? Parse(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
        try
        {
            var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>The key a file of this kind carries: a template's from its <c>Name</c>, a component's from its <c>Type</c>; "" when it has none.</summary>
    public static string KeyOf(string kind, JsonElement root)
    {
        var field = kind == KindComponent ? "Type" : "Name";
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? Key(v.GetString() ?? "") : "";
    }
}
