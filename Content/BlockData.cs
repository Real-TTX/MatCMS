using System.Text.Json.Nodes;

namespace MatCMS.Content;

/// <summary>Read-only accessor over a block's JSON payload, for use in render partials.</summary>
public class BlockData
{
    private readonly JsonObject _obj;

    public BlockData(string? json)
    {
        _obj = TryParse(json);
    }

    internal BlockData(JsonObject obj)
    {
        _obj = obj;
    }

    private static JsonObject TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public string Str(string key)
    {
        var node = _obj[key];
        if (node is null) return "";
        if (node is JsonValue value && value.TryGetValue<string>(out var s)) return s;
        return node.ToString();
    }

    public bool Has(string key) => !string.IsNullOrWhiteSpace(Str(key));

    public string StrOr(string key, string fallback)
    {
        var v = Str(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    public IReadOnlyList<BlockData> List(string key)
    {
        var result = new List<BlockData>();
        if (_obj[key] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject o)
                    result.Add(new BlockData((JsonObject)o.DeepClone()));
            }
        }
        return result;
    }
}
