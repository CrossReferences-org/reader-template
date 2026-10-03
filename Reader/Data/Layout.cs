using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reader.Data;

/// <summary>
/// A block starting at this point of a verse: a paragraph, poetry line, list item or Psalm
/// title, with any headings above it. <see cref="Style"/> is the USFM marker (p, q1, d, …).
/// </summary>
public sealed record LayoutBreak(int Offset, string Style, bool Gap, Heading[] Headings);

/// <summary>An editorial heading: s1, s2, ms (Psalm book), mr, qa (acrostic letter) or r (parallel passages).</summary>
public sealed record Heading(string Kind, string Text, HeadingRef[] Refs);

/// <summary>A passage named in a heading, with the chapter and verse range it links to.</summary>
public sealed record HeadingRef(string Text, int Book, int Chapter, string Verses);

/// <summary>
/// Paragraphs, poetry and headings for a translation, from a layout file built outside the app
/// (tools/bsb_layout). The translation's own text stays as it is; the layout only says where
/// blocks start, as offsets into each verse record's text.
/// </summary>
public static partial class Layout
{
    public const string BsbFile = "bsb_layout.json";

    private sealed record RawRef(string T, int? B, int? C, string? V);
    private sealed record RawHeading(string K, string T, List<RawRef>? Refs);
    private sealed record RawBreak(int O, string K, bool? G, List<RawHeading>? H);

    private static readonly HashSet<string> Styles = ["p", "pmo", "m", "pc", "li1", "li2", "q1", "q2", "qr", "d"];
    private static readonly HashSet<string> Kinds = ["s1", "s2", "ms", "mr", "qa", "r"];

    /// <summary>Styles whose opening verse number hangs in the indent (see reader.css).</summary>
    private static readonly HashSet<string> Hanging = ["q1", "q2", "li1", "li2"];

    /// <summary>
    /// Styles grouped into one div.poem while they run on: the div is as wide as the poem's
    /// longest line, so a right-aligned qr line (Selah, "declares the LORD") ends where the
    /// poem does, not at the far edge of the column. A heading or prose block ends the run.
    /// </summary>
    private static readonly HashSet<string> Poetry = ["q1", "q2", "qr", "li1", "li2"];

    [GeneratedRegex(@"^\d+(-\d+)?$")]
    private static partial Regex VerseRange();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Verse record id to its breaks, offsets into that record's text; null when there is no file.</summary>
    public static Dictionary<int, LayoutBreak[]>? Load(string directory, string file, ILogger log)
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path))
        {
            log.LogInformation("No {File}: one verse per line", file);
            return null;
        }

        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<Dictionary<int, List<RawBreak>>>(stream, Options) ?? [];

        var skipped = 0;
        var layout = new Dictionary<int, LayoutBreak[]>(raw.Count);
        foreach (var (id, breaks) in raw)
        {
            var list = new List<LayoutBreak>(breaks.Count);
            foreach (var b in breaks)
            {
                if (b.O < 0 || !Styles.Contains(b.K)) { skipped++; continue; }
                var headings = (b.H ?? [])
                    .Where(h => Kinds.Contains(h.K) && !string.IsNullOrWhiteSpace(h.T))
                    .Select(h => new Heading(h.K, h.T, (h.Refs ?? [])
                        .Where(r => r.B > 0 && r.C > 0 && r.V is not null && VerseRange().IsMatch(r.V))
                        .Select(r => new HeadingRef(r.T, r.B!.Value, r.C!.Value, r.V!))
                        .ToArray()))
                    .ToArray();
                list.Add(new LayoutBreak(b.O, b.K, b.G == true, headings));
            }
            layout[id] = list.OrderBy(b => b.Offset).ToArray();
        }

        log.LogInformation("{File}: {Breaks} breaks on {Records} verse records", file, layout.Values.Sum(b => b.Length), layout.Count);
        if (skipped > 0) log.LogWarning("{File}: {Count} breaks with an unknown style or offset were ignored", file, skipped);
        return layout;
    }

    /// <summary>
    /// Renders a chapter as flowing text: verses run inline within blocks, and a verse that
    /// crosses a block boundary becomes several <c>span.verse</c> pieces sharing its
    /// <c>data-v</c>; the first carries the <c>id</c>. Anchors are cut at block boundaries too,
    /// so an anchor crossing a poetry line becomes two segments with the same <c>data-a</c>.
    /// </summary>
    public static string RenderChapter(IReadOnlyList<DisplayVerse> verses, IReadOnlyDictionary<string, int> colours, string tr, PlacementStats stats)
    {
        var sb = new StringBuilder(verses.Sum(v => v.Text.Length) * 2 + 256);
        var blockOpen = false;
        var poemOpen = false;
        var atBlockStart = false;
        var style = "p";
        var pendingHeadings = new List<Heading>();
        var pendingGap = false;

        void OpenBlock()
        {
            if (blockOpen) sb.Append("</p>");
            if (poemOpen && (pendingHeadings.Count > 0 || !Poetry.Contains(style)))
            {
                sb.Append("</div>");
                poemOpen = false;
            }
            foreach (var h in pendingHeadings) AppendHeading(sb, h, tr);
            if (!poemOpen && Poetry.Contains(style))
            {
                sb.Append("<div class=\"poem\">");
                poemOpen = true;
            }
            sb.Append("<p class=\"ln p-").Append(style);
            if (pendingGap) sb.Append(" stanza");
            sb.Append("\">");
            pendingHeadings.Clear();
            pendingGap = false;
            blockOpen = true;
            atBlockStart = true;
        }

        foreach (var v in verses)
        {
            var text = v.Text;

            // The verse cut into pieces, each with the break that opens it (null: continues inline).
            var pieces = new List<(int From, int To, LayoutBreak? Break)>();
            var breaks = v.Breaks.Where(b => b.Offset < text.Length).ToList();
            if (breaks.Count == 0 || breaks[0].Offset > 0) pieces.Add((0, breaks.Count > 0 ? breaks[0].Offset : text.Length, null));
            for (var i = 0; i < breaks.Count; i++)
                pieces.Add((breaks[i].Offset, i + 1 < breaks.Count ? breaks[i + 1].Offset : text.Length, breaks[i]));

            // The number goes on the first piece that is not a Psalm title: as in print, it
            // sits on the psalm's first line. A verse that is all title keeps it on the title.
            var styles = new string[pieces.Count];
            var s = style;
            for (var i = 0; i < pieces.Count; i++) styles[i] = s = pieces[i].Break?.Style ?? s;
            var numberAt = Array.FindIndex(styles, x => x != "d");
            if (numberAt < 0) numberAt = 0;

            var hadOverlap = false;
            var first = true;
            for (var i = 0; i < pieces.Count; i++)
            {
                var (from, to, br) = pieces[i];
                if (br is not null)
                {
                    pendingHeadings.AddRange(br.Headings);
                    pendingGap |= br.Gap;
                    style = br.Style;
                    if (from == to) continue;       // two breaks at one offset: the later one opens
                    OpenBlock();
                }
                else if (!blockOpen)
                {
                    OpenBlock();
                }

                while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
                while (from < to && char.IsWhiteSpace(text[from])) from++;
                if (from == to && i != numberAt) continue;

                sb.Append("<span class=\"verse\" data-v=\"").Append(v.Number).Append('"');
                if (first) sb.Append(" id=\"v").Append(v.Number).Append('"');
                sb.Append('>');
                first = false;
                if (i == numberAt)
                {
                    // A number that hangs in a poetry indent needs no space after it; anywhere
                    // else it is kept to its first word, so it never ends a line on its own.
                    sb.Append("<span class=\"vn\" role=\"button\" tabindex=\"0\" data-v=\"").Append(v.Number).Append("\">")
                      .Append(v.Number).Append("</span>");
                    if (!(atBlockStart && Hanging.Contains(style))) sb.Append('\u00A0');
                }
                atBlockStart = false;
                AnchorPlacer.AppendSegments(sb, text, from, to, v.Placements, colours, stats, ref hadOverlap);
                sb.Append("</span> ");
            }
            if (hadOverlap) stats.VersesWithOverlap++;
        }

        if (blockOpen) sb.Append("</p>");
        if (poemOpen) sb.Append("</div>");
        return sb.ToString();
    }

    private static void AppendHeading(StringBuilder sb, Heading h, string tr)
    {
        var tag = h.Kind switch { "s1" or "ms" => "h2", "s2" => "h3", _ => "p" };
        sb.Append('<').Append(tag).Append(" class=\"sh sh-").Append(h.Kind).Append("\">");

        // Parallel passages become links, in the order they appear in the heading text.
        var cursor = 0;
        foreach (var r in h.Refs)
        {
            var at = h.Text.IndexOf(r.Text, cursor, StringComparison.Ordinal);
            if (at < 0 || r.Text.Length == 0) continue;
            Html.Append(sb, h.Text, cursor, at - cursor);
            sb.Append("<a class=\"xr\" href=\"").Append(r.Book).Append('/').Append(r.Chapter).Append('/')
              .Append(tr).Append("#v").Append(r.Verses).Append("\">");
            Html.Append(sb, r.Text);
            sb.Append("</a>");
            cursor = at + r.Text.Length;
        }
        Html.Append(sb, h.Text, cursor, h.Text.Length - cursor);
        sb.Append("</").Append(tag).Append('>');
    }
}
