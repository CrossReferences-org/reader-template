using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reader.Tests;

/// <summary>tests/fixtures/cases.json: small corpora copied from the data, one per rule.</summary>
public static class Fixtures
{
    public sealed record Case(string Name, JsonArray Verses, JsonArray CrossReferences, JsonArray Panes)
    {
        public JsonObject Record(int id) =>
            Verses.Select(v => v!.AsObject()).Single(v => v["id"]!.GetValue<int>() == id);
    }

    public static string Directory { get; } = Path.Combine(RepositoryRoot(), "tests", "fixtures");

    private static readonly JsonObject File = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(Directory, "cases.json")))!.AsObject();

    public static JsonArray Books { get; } = File["books"]!.AsArray();

    public static IReadOnlyList<Case> Cases { get; } = File["cases"]!.AsArray()
        .Select(c => new Case(
            c!["name"]!.GetValue<string>(),
            c["bible_verses"]!.AsArray(),
            c["cross_references"]!.AsArray(),
            c["panes"]!.AsArray()))
        .ToList();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "Reader.slnx"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Reader.slnx not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// JSON written for people to read: anything that fits on one line stays on one line.
/// </summary>
public static class CompactJson
{
    private const int Width = 110;

    private static readonly JsonSerializerOptions Inline = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(JsonNode node)
    {
        var sb = new System.Text.StringBuilder();
        Write(sb, node, 0, 0);
        return sb.Append('\n').ToString();
    }

    /// <param name="column">Where the value starts on its line, after any property name.</param>
    private static void Write(System.Text.StringBuilder sb, JsonNode? node, int indent, int column)
    {
        var inline = node?.ToJsonString(Inline) ?? "null";
        if (node is JsonValue or null || column + inline.Length <= Width)
        {
            sb.Append(inline);
            return;
        }

        var pad = new string(' ', indent + 2);
        var items = node is JsonObject o
            ? o.Select(kv => (Name: JsonSerializer.Serialize(kv.Key, Inline) + ": ", Value: kv.Value))
            : node.AsArray().Select(v => (Name: "", Value: v));
        var (open, close) = node is JsonObject ? ('{', '}') : ('[', ']');

        sb.Append(open).Append('\n');
        var first = true;
        foreach (var (name, value) in items)
        {
            if (!first) sb.Append(",\n");
            sb.Append(pad).Append(name);
            Write(sb, value, indent + 2, indent + 2 + name.Length);
            first = false;
        }
        sb.Append('\n').Append(' ', indent).Append(close);
    }

    /// <summary>The path and both values at the first place the two differ, or null when they are equal.</summary>
    public static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (JsonObject e, JsonObject a):
                foreach (var name in e.Select(kv => kv.Key).Union(a.Select(kv => kv.Key)))
                {
                    if (FirstDifference(e[name], a[name], $"{path}.{name}") is { } d) return d;
                }
                return null;

            case (JsonArray e, JsonArray a):
                for (var i = 0; i < Math.Max(e.Count, a.Count); i++)
                {
                    if (i >= e.Count || i >= a.Count)
                        return $"{path}: expected {e.Count} items, got {a.Count}\nexpected: {e.ToJsonString(Inline)}\nactual:   {a.ToJsonString(Inline)}";
                    if (FirstDifference(e[i], a[i], $"{path}[{i}]") is { } d) return d;
                }
                return null;

            default:
                return JsonNode.DeepEquals(expected, actual) ? null
                    : $"{path}\nexpected: {expected?.ToJsonString(Inline) ?? "null"}\nactual:   {actual?.ToJsonString(Inline) ?? "null"}";
        }
    }
}
