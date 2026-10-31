using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Reader.Data;

/// <summary>
/// Reads the three JSON files (plus the optional S21 text) and precomputes what the reader
/// serves: display verses, anchor placements and colours, and the cited-by index.
/// The data never changes while the app runs; requests only render HTML from it.
/// </summary>
public static class CorpusLoader
{
    public const string S21File = "optional/s21_verses.json";

    private sealed record RawBook(
        int Id, string NameEng, string NameFra, string NameAfr,
        string? AbbreviationEng, string? AbbreviationFra, string? AbbreviationAfr);

    private sealed record RawVerse(
        int Id, int BookId,
        int KjvCh, int KjvVs, int KjvSort, string? KjvText,
        int BsbCh, int BsbVs, int BsbSort, string? BsbText,
        int AovCh, int AovVs, int AovSort, string? AovText);

    private sealed record RawS21Verse(int Id, int S21Ch, int S21Vs, int S21Sort, string? S21Text);

    private sealed record RawAnchor(int VerseId, int Sort, string? Kjv, string? Bsb, string? Aov, string? S21, int[][]? Refs);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static Corpus Load(string directory, ILogger log)
    {
        var clock = Stopwatch.StartNew();

        var books = Read<List<RawBook>>(directory, "bible_books.json")
            .Select(b => new BookInfo(
                b.Id, b.NameEng, b.NameFra, b.NameAfr,
                b.AbbreviationEng ?? "", b.AbbreviationFra ?? "", b.AbbreviationAfr ?? ""))
            .OrderBy(b => b.Id)
            .ToList();

        var fragments = new Dictionary<int, Fragment>();
        foreach (var v in Read<List<RawVerse>>(directory, "bible_verses.json"))
        {
            var coords = new Coord[Translation.Count];
            coords[Translation.Kjv.Index] = new(v.KjvCh, v.KjvVs, v.KjvSort, Clean(v.KjvText));
            coords[Translation.Bsb.Index] = new(v.BsbCh, v.BsbVs, v.BsbSort, Clean(v.BsbText));
            coords[Translation.Aov.Index] = new(v.AovCh, v.AovVs, v.AovSort, Clean(v.AovText));
            coords[Translation.S21.Index] = new(0, 0, 0, "");
            fragments[v.Id] = new Fragment(v.Id, v.BookId, coords);
        }

        var hasS21 = File.Exists(Path.Combine(directory, S21File));
        if (hasS21)
        {
            var matched = 0;
            foreach (var s in Read<List<RawS21Verse>>(directory, S21File))
            {
                if (!fragments.TryGetValue(s.Id, out var f)) continue;
                f.Coords[Translation.S21.Index] = new(s.S21Ch, s.S21Vs, s.S21Sort, Clean(s.S21Text));
                matched++;
            }
            log.LogInformation("S21 text found: {Count} of {Total} verse records", matched, fragments.Count);
        }
        else
        {
            log.LogInformation("No {File}: S21 is not available", S21File);
        }

        var anchors = new Dictionary<string, Anchor>();
        var orphans = 0;
        foreach (var x in Read<List<RawAnchor>>(directory, "cross_references.json"))
        {
            if (!fragments.ContainsKey(x.VerseId)) { orphans++; continue; }
            var a = new Anchor(x.VerseId, x.Sort, [Clean(x.Kjv), Clean(x.Bsb), Clean(x.Aov), Clean(x.S21)], x.Refs ?? []);
            anchors[a.Key] = a;
        }
        if (orphans > 0) log.LogWarning("{Count} anchors point at verse records that do not exist", orphans);

        var anchorsByFragment = anchors.Values
            .GroupBy(a => a.VerseId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Sort).ToArray());

        // Reverse index, shared by all translations: verse record to the anchors that cite it.
        var citing = new Dictionary<int, List<Anchor>>();
        foreach (var a in anchors.Values)
        {
            foreach (var id in a.Refs.SelectMany(r => r).Distinct())
            {
                if (!citing.TryGetValue(id, out var list)) citing[id] = list = [];
                list.Add(a);
            }
        }
        var citedBy = citing.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

        var bsbLayout = Layout.Load(directory, Layout.BsbFile, log);

        var views = new List<TranslationView>();
        foreach (var t in Translation.All)
        {
            if (t == Translation.S21 && !hasS21) continue;
            var stats = new PlacementStats();
            var view = BuildView(t, books, fragments, anchorsByFragment, t == Translation.Bsb ? bsbLayout : null, stats);
            // Rendering counts the overlapping segments; the HTML itself is rendered again on request.
            foreach (var verses in view.Chapters.Values) view.RenderChapter(verses, stats);
            views.Add(view);
            log.LogInformation("{Code}: {Stats}", t.Code, stats);
        }

        log.LogInformation("Corpus loaded in {Ms} ms", clock.ElapsedMilliseconds);
        return new Corpus(anchors, citedBy, views);
    }

    private static TranslationView BuildView(
        Translation t,
        IReadOnlyList<BookInfo> books,
        Dictionary<int, Fragment> fragments,
        Dictionary<int, Anchor[]> anchorsByFragment,
        Dictionary<int, LayoutBreak[]>? layout,
        PlacementStats stats)
    {
        var tr = t.Index;
        var chapters = new Dictionary<(int, int), DisplayVerse[]>();
        var byFragment = new Dictionary<int, DisplayVerse>();
        var colours = new Dictionary<string, int>();

        var grouped = fragments.Values
            .Where(f => f.Coords[tr].Exists)
            .GroupBy(f => (f.BookId, f.Coords[tr].Chapter));

        foreach (var chapter in grouped)
        {
            var verses = new List<DisplayVerse>();

            foreach (var verse in chapter.GroupBy(f => f.Coords[tr].Verse).OrderBy(g => g.Key))
            {
                // Rebuild the verse: this translation's records, in this translation's order.
                var parts = verse.OrderBy(f => f.Coords[tr].Sort).ThenBy(f => f.Id).ToList();
                var text = new StringBuilder();
                var slots = new List<AnchorSlot>();
                var verseAnchors = new List<Anchor>();
                var breaks = new List<LayoutBreak>();

                foreach (var part in parts)
                {
                    var piece = part.Coords[tr].Text;
                    if (piece.Length > 0 && text.Length > 0) text.Append(' ');
                    var start = text.Length;
                    text.Append(piece);

                    // Layout offsets are per record; here they become offsets into the verse.
                    if (layout is not null && layout.TryGetValue(part.Id, out var recordBreaks))
                    {
                        foreach (var b in recordBreaks)
                            if (b.Offset < piece.Length) breaks.Add(b with { Offset = start + b.Offset });
                    }

                    if (anchorsByFragment.TryGetValue(part.Id, out var own))
                    {
                        foreach (var a in own)
                        {
                            slots.Add(new AnchorSlot(a, start, text.Length));
                            verseAnchors.Add(a);
                        }
                    }
                }

                var dv = new DisplayVerse
                {
                    BookId = chapter.Key.BookId,
                    Chapter = chapter.Key.Chapter,
                    Number = verse.Key,
                    Text = text.ToString(),
                    Fragments = parts.Select(p => p.Id).ToArray(),
                    Anchors = verseAnchors.ToArray(),
                    Breaks = breaks.ToArray(),
                };
                foreach (var part in parts) byFragment[part.Id] = dv;

                // Verses with no text in this translation (the BSB's textual omissions) are
                // kept for lookups but not rendered.
                if (!dv.HasText) continue;

                var placed = AnchorPlacer.Place(dv.Text, slots, tr, t.Sequential, stats);
                AnchorPlacer.AssignColours(placed, colours);
                dv.Placements = placed.ToArray();
                verses.Add(dv);
            }

            if (verses.Count == 0) continue;
            chapters[chapter.Key] = verses.ToArray();
        }

        var entries = new List<BookEntry>();
        foreach (var b in books)
        {
            var max = chapters.Keys.Where(k => k.Item1 == b.Id).Select(k => k.Item2).DefaultIfEmpty(0).Max();
            if (max == 0) continue;
            entries.Add(new BookEntry(b, b.Name(t.Language), max, Spine(b.Id), StartsNewTestament: b.Id == 40));
        }

        return new TranslationView
        {
            T = t,
            Books = entries,
            BookById = entries.ToDictionary(e => e.Id),
            Chapters = chapters,
            HasLayout = layout is not null,
            ByFragment = byFragment,
            Colour = colours,
            Stats = stats,
        };
    }

    private static T Read<T>(string directory, string file)
    {
        using var stream = File.OpenRead(Path.Combine(directory, file));
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"{file} is empty");
    }

    private static string Clean(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().Normalize(NormalizationForm.FormC);

    /// <summary>Book spine colours, carried over from the CrossReferences.org reader.</summary>
    private static string Spine(int id) => id switch
    {
        <= 5 => "#399bb6",
        9 or 10 => "#24b400f1",
        <= 17 => "#24b4009e",
        19 => "#ab2fe1",
        <= 22 => "#ac2fe1a5",
        <= 27 => "#c4d026",
        32 or 35 => "#ff6600e2",
        <= 39 => "#ff66009e",
        <= 43 => "#27ae60",
        44 => "#b7950b",
        48 or 49 or 50 or 54 or 55 => "#0e8add",
        <= 57 => "#297fb9bf",
        60 or 61 => "#8827d7ef",
        <= 65 => "#8637c6a1",
        _ => "#922b21",
    };
}
