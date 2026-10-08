using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Reader.Data;

namespace Reader.Tests;

/// <summary>
/// Runs every case in tests/fixtures/cases.json through the real loader and compares what it
/// builds with tests/fixtures/expected/{case}.json. See tests/fixtures/README.md.
/// <para>
/// Set UPDATE_EXPECTED=1 to rewrite the expected files from the current code instead; then
/// review the diff, since the expected files are the specification.
/// </para>
/// </summary>
public partial class FixtureTests
{
    public static TheoryData<string> Cases() => new(Fixtures.Cases.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_matches_expected(string name)
    {
        var c = Fixtures.Cases.Single(c => c.Name == name);
        var actual = Describe(c);
        var path = Path.Combine(Fixtures.Directory, "expected", name + ".json");

        if (Environment.GetEnvironmentVariable("UPDATE_EXPECTED") == "1")
        {
            File.WriteAllText(path, CompactJson.Format(actual));
            return;
        }

        Assert.True(File.Exists(path), $"No expected output for {name}: run with UPDATE_EXPECTED=1 and review it.");
        var expected = JsonNode.Parse(File.ReadAllText(path))!;
        if (CompactJson.FirstDifference(expected, actual, name) is { } difference) Assert.Fail(difference);
    }

    private static JsonObject Describe(Fixtures.Case c)
    {
        var directory = Directory.CreateTempSubdirectory("reader-fixture-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "bible_books.json"), Fixtures.Books.ToJsonString());
            File.WriteAllText(Path.Combine(directory.FullName, "bible_verses.json"), c.Verses.ToJsonString());
            File.WriteAllText(Path.Combine(directory.FullName, "cross_references.json"), c.CrossReferences.ToJsonString());
            var corpus = CorpusLoader.Load(directory.FullName, NullLogger.Instance);

            var result = new JsonObject();
            foreach (var view in corpus.Views) result[view.T.Code] = Describe(corpus, view, c);
            return result;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static JsonObject Describe(Corpus corpus, TranslationView view, Fixtures.Case c)
    {
        var s = view.Stats;
        var verses = new JsonArray();
        foreach (var v in view.ByFragment.Values.Distinct()
                     .OrderBy(v => v.BookId).ThenBy(v => v.Chapter).ThenBy(v => v.Number))
        {
            verses.Add(Describe(view, v, c));
        }

        var panes = new JsonArray();
        foreach (var request in c.Panes.Select(p => p!))
        {
            PaneModel? pane;
            if (request["verse_of"] is { } id)
            {
                var v = view.ByFragment[id.GetValue<int>()];
                pane = PaneBuilder.ForVerse(view, v.BookId, v.Chapter, v.Number);
            }
            else
            {
                pane = PaneBuilder.ForAnchors(corpus, view, request["keys"]!.AsArray().Select(k => k!.GetValue<string>()));
            }
            panes.Add(new JsonObject { ["request"] = request.DeepClone(), ["pane"] = Describe(pane) });
        }

        return new JsonObject
        {
            ["stats"] = new JsonObject
            {
                ["anchors"] = s.Anchors,
                ["placed"] = s.Placed,
                ["no_phrase"] = s.Empty,
                ["not_found"] = s.Unplaced,
                ["case_fallback"] = s.CaseFallback,
                ["occur_more_than_once"] = s.Duplicates,
                ["no_free_occurrence"] = s.DuplicateForcedOverlap,
                ["verses_with_overlap"] = s.VersesWithOverlap,
                ["overlap_segments"] = s.OverlapSegments,
                ["shared_span_segments"] = s.SharedSpanSegments,
            },
            ["verses"] = verses,
            ["panes"] = panes,
        };
    }

    private static JsonObject Describe(TranslationView view, DisplayVerse v, Fixtures.Case c)
    {
        var sortField = view.T.Code.ToLowerInvariant() + "_sort";
        var records = view.ByFragment.Where(kv => kv.Value == v).Select(kv => kv.Key)
            .OrderBy(id => c.Record(id)[sortField]!.GetValue<int>()).ThenBy(id => id);

        var o = new JsonObject
        {
            ["verse"] = $"{view.BookName(v.BookId)} {v.Chapter}:{v.Number}",
            ["records"] = new JsonArray(records.Select(id => (JsonNode)id).ToArray()),
            ["text"] = v.Text,
        };
        // Listed even for a verse without text, where it must stay empty.
        if (view.CitedBy.TryGetValue(v, out var citing)) AddKeys(o, "cited_by", citing);
        if (!v.HasText) return o;

        var tr = view.T.Index;
        var placed = v.Placements.Select(p => p.Anchor.Key).ToHashSet();
        if (v.Placements.Length > 0)
        {
            o["placed"] = new JsonArray(v.Placements.Select(p => (JsonNode)new JsonObject
            {
                ["key"] = p.Anchor.Key,
                ["start"] = p.Start,
                ["end"] = p.End,
                ["match"] = v.Text[p.Start..p.End],
                ["colour"] = view.Colour[p.Anchor.Key],
                ["case_fallback"] = v.Text[p.Start..p.End] != p.Anchor.Text[tr],
            }).ToArray());
        }
        AddKeys(o, "not_found", v.Anchors.Where(a => a.Text[tr].Length > 0 && !placed.Contains(a.Key)));
        AddKeys(o, "no_phrase", v.Anchors.Where(a => a.Text[tr].Length == 0));
        if (v.Placements.Length > 0) o["segments"] = Segments(view, v);
        return o;
    }

    private static void AddKeys(JsonObject o, string name, IEnumerable<Anchor> anchors)
    {
        var keys = anchors.Select(a => (JsonNode)a.Key).ToArray();
        if (keys.Length > 0) o[name] = new JsonArray(keys);
    }

    /// <summary>
    /// The verse as rendered: [text, [anchor keys, innermost first]] per segment, read back from
    /// the chapter HTML so the test sees exactly what the reader serves.
    /// </summary>
    private static JsonArray Segments(TranslationView view, DisplayVerse v)
    {
        var html = view.ChapterHtml[(v.BookId, v.Chapter)];
        var paragraph = Regex.Match(html, $"<p class=\"verse\" id=\"v{v.Number}\"><span class=\"vn\"[^>]*>\\d+</span> (.*?)</p>");
        Assert.True(paragraph.Success, $"verse {v.Number} not found in the chapter HTML");

        var segments = new JsonArray();
        foreach (Match m in Segment().Matches(paragraph.Groups[1].Value))
        {
            var keys = m.Groups["keys"].Success ? m.Groups["keys"].Value.Split(' ') : [];
            var text = WebUtility.HtmlDecode(m.Groups["keys"].Success ? m.Groups["anchored"].Value : m.Groups["plain"].Value);
            segments.Add(new JsonArray(text, new JsonArray(keys.Select(k => (JsonNode)k).ToArray())));
        }
        return segments;
    }

    [GeneratedRegex("<span class=\"a c\\d( m)?\" role=\"button\" tabindex=\"0\" data-a=\"(?<keys>[^\"]*)\">(?<anchored>[^<]*)</span>|(?<plain>[^<]+)")]
    private static partial Regex Segment();

    private static JsonNode? Describe(PaneModel? pane)
    {
        if (pane is null) return null;
        var groups = new JsonArray(pane.Groups.Select(g => (JsonNode)new JsonObject
        {
            ["key"] = g.Key,
            ["heading"] = g.Heading,
            ["kjv_fallback"] = g.IsKjvFallback,
            ["kjv_note"] = g.KjvNote,
            ["colour"] = g.Colour,
            ["items"] = new JsonArray(g.Items.Select(i => (JsonNode)new JsonObject
            {
                ["label"] = i.Label,
                ["href"] = i.Href,
            }).ToArray()),
        }).ToArray());

        JsonNode? citedBy = pane.CitedBy is null ? null : new JsonObject
        {
            ["heading"] = pane.CitedBy.Heading,
            ["entries"] = new JsonArray(pane.CitedBy.Entries.Select(e => (JsonNode)new JsonObject
            {
                ["label"] = e.Label,
                ["href"] = e.Href,
                ["keys"] = e.Keys,
                ["phrases"] = e.Phrases,
                ["dimmed"] = e.Dimmed,
            }).ToArray()),
        };

        return new JsonObject { ["title"] = pane.Title, ["groups"] = groups, ["cited_by"] = citedBy };
    }
}
