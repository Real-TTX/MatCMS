using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MatCMS.Plugin;

namespace MatCMS.Services.Plugins;

/// <summary>
/// The plugin API as data, for the script editor's completion: which names a script can use, what each member returns (so a chain such as
/// <c>Plugin.Admin.Section(…).</c> can be followed) and the one-line summary from the API's own documentation. Read from the compiled
/// <c>MatCMS.Plugin.Abstractions</c> assembly and its XML documentation — never written by hand, so it cannot drift from the API.
/// </summary>
public static class PluginApiCatalog
{
    private static readonly Lazy<string> Json = new(() => Build().ToJsonString());

    /// <summary>The catalogue as JSON (built once; the assembly cannot change while the host runs).</summary>
    public static string AsJson() => Json.Value;

    // The names a script finds in scope, and the type each stands for. "ctx" is the usual name of a handler's parameter.
    private static readonly (string Name, Type Type)[] Roots =
    {
        ("Plugin", typeof(IPluginBuilder)),
        ("ctx", typeof(IPluginContext)),
    };

    internal static JsonObject Build()
    {
        var asm = typeof(IPlugin).Assembly;
        var docs = LoadDocs(asm);
        var types = new JsonObject();

        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.Name))
        {
            if (t.IsEnum)
            {
                types[t.Name] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode)Member(n, "field", n, t.Name, Doc(docs, $"F:{t.FullName}.{n}"))).ToArray());
                continue;
            }
            var members = new JsonArray();
            var seen = new HashSet<string>();
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                         .Where(m => m is MethodInfo { IsSpecialName: false } or PropertyInfo or FieldInfo))
            {
                if (m is MethodInfo mi && mi.DeclaringType == typeof(object)) continue;
                var name = m.Name;
                if (name is "Equals" or "GetHashCode" or "ToString" or "GetType" or "Deconstruct" or "PrintMembers" or "<Clone>$") continue;
                var isStatic = m switch { MethodInfo x => x.IsStatic, PropertyInfo p => p.GetMethod?.IsStatic == true, FieldInfo f => f.IsStatic, _ => false };
                var returns = m switch { MethodInfo x => Simple(x.ReturnType), PropertyInfo p => Simple(p.PropertyType), FieldInfo f => Simple(f.FieldType), _ => "" };
                var sig = m is MethodInfo mm ? name + "(" + string.Join(", ", mm.GetParameters().Select(p => Simple(p.ParameterType) + " " + p.Name)) + ")" : name;
                if (!seen.Add(sig)) continue;
                var id = m is MethodInfo ? $"M:{t.FullName}.{name}(" : m is PropertyInfo ? $"P:{t.FullName}.{name}" : $"F:{t.FullName}.{name}";
                members.Add(Member(name, m is MethodInfo ? "method" : "property", sig, returns, Doc(docs, id), isStatic));
            }
            types[t.Name] = members;
        }

        // Extension methods belong to the type of their first parameter: Page(...) and Badge(...) on IAdminSection, On(...) on IPluginBuilder.
        foreach (var ext in asm.GetExportedTypes().Where(t => t.IsAbstract && t.IsSealed))
            foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)))
            {
                var target = Simple(m.GetParameters()[0].ParameterType);
                if (types[target] is not JsonArray list) continue;
                var ps = m.GetParameters().Skip(1);
                var sig = m.Name + "(" + string.Join(", ", ps.Select(p => Simple(p.ParameterType) + " " + p.Name)) + ")";
                if (list.Any(x => x?["sig"]?.GetValue<string>() == sig)) continue;
                list.Add(Member(m.Name, "method", sig, Simple(m.ReturnType), Doc(docs, $"M:{ext.FullName}.{m.Name}("), false));
            }

        // Types that a script uses by name (static factories, enums, constants).
        var names = new[] { "Ui", "PluginResult", "JobSchedule", "DataQuery", "HostEvents", "UiTone", "UiButtonKind", "UiInputType", "AdminGroup", "FieldKind",
                            "SettingKind", "ColumnType", "DataOp", "PluginMethod" }.Where(n => types[n] is not null).ToArray();
        var roots = new JsonObject();
        foreach (var (name, type) in Roots) roots[name] = Simple(type);
        foreach (var n in names) roots[n] = n;
        return new JsonObject { ["roots"] = roots, ["types"] = types, ["records"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()) };
    }

    private static JsonObject Member(string name, string kind, string sig, string returns, string doc, bool isStatic = false) =>
        new() { ["name"] = name, ["kind"] = kind, ["sig"] = sig, ["returns"] = returns, ["doc"] = doc, ["static"] = isStatic };

    // List<T>/IReadOnlyList<T>/Task<T>… → the element or result type name; plain names stay.
    private static string Simple(Type t)
    {
        if (t.IsByRef) t = t.GetElementType()!;
        if (Nullable.GetUnderlyingType(t) is { } u) return Simple(u);
        if (t.IsArray) return Simple(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            var n = t.Name[..t.Name.IndexOf('`')];
            var args = t.GetGenericArguments();
            if (n is "Task" or "ValueTask") return Simple(args[0]);
            return n + "<" + string.Join(", ", args.Select(Simple)) + ">";
        }
        return t == typeof(void) ? "void" : t.Name switch { "String" => "string", "Boolean" => "bool", "Int32" => "int", "Int64" => "long", "Double" => "double", _ => t.Name };
    }

    private static Dictionary<string, string> LoadDocs(Assembly asm)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = Path.ChangeExtension(asm.Location, ".xml");
            if (!File.Exists(path)) return d;
            foreach (var m in XDocument.Load(path).Descendants("member"))
            {
                var id = m.Attribute("name")?.Value;
                var summary = m.Element("summary");
                if (id is null || summary is null) continue;
                var text = Regex.Replace(string.Concat(summary.Nodes().Select(n => n switch
                {
                    XText t => t.Value,
                    XElement e when e.Attribute("cref")?.Value is { } c => c[(c.LastIndexOfAny(new[] { '.', ':' }) + 1)..].Split('(')[0],
                    XElement e when e.Attribute("name")?.Value is { } name => name,       // <paramref name="x"/>
                    XElement e => e.Value,                                                 // <c>, <b>, <para>
                    _ => ""
                })), @"\s+", " ").Trim();
                if (text.Length > 0) d[id] = text.Length > 220 ? text[..217] + "…" : text;
            }
        }
        catch (Exception) { /* no docs: completion still works, just without the summary line */ }
        return d;
    }

    // Overloads share a summary prefix: the first documented one wins, which is what a summary line needs.
    private static string Doc(Dictionary<string, string> docs, string idPrefix) =>
        docs.TryGetValue(idPrefix, out var exact) ? exact : docs.FirstOrDefault(kv => kv.Key.StartsWith(idPrefix, StringComparison.Ordinal)).Value ?? "";
}
