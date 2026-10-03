using System.Text;

namespace Reader.Data;

public sealed record PaneModel(UiText Ui, string Title, IReadOnlyList<GroupModel> Groups, CitedSection? CitedBy);

public sealed record CitedSection(string Heading, IReadOnlyList<CitedEntry> Entries);

/// <param name="Phrases">The citing anchors, each trimmed to a few words.</param>
/// <param name="FullPhrases">The same anchors in full, for the tooltip.</param>
/// <param name="Dimmed">The verse is already among the references shown above it in the pane.</param>
/// <param name="Keys">The citing anchors' keys, comma-separated: what the preview fragment asks for.</param>
public sealed record CitedEntry(string Label, string Href, string Keys, string Phrases, string FullPhrases, bool Dimmed);

/// <summary>A citing verse shown in place, with the words that point back marked.</summary>
public sealed record PreviewModel(string TextHtml, string GoLabel, string Href);

/// <param name="Key">The anchor's key, so a tapped phrase can find its group.</param>
/// <param name="Heading">The anchor phrase, or the KJV anchor when this translation has none.</param>
/// <param name="KjvNote">
/// The KJV anchor, shown beside the heading when several groups in the pane share one heading:
/// the TSK's alternate spellings (Sichem, Sychar) all land on the same modern word.
/// </param>
/// <param name="Colour">The anchor's colour in the text; 0 when it is not highlighted there.</param>
public sealed record GroupModel(string Key, string Heading, bool IsKjvFallback, string? KjvNote, int Colour, IReadOnlyList<ReferenceItem> Items);

public sealed record ReferenceItem(string Label, string Href, string TextHtml);

public static class PaneBuilder
{
    /// <summary>Cited text shows this many verses before the rest is folded away.</summary>
    private const int VisibleVerses = 3;

    private const int MaxAnchorsPerRequest = 12;

    /// <summary>Citing anchor phrases are cut to this many words in the cited-by list.</summary>
    private const int CitedPhraseWords = 4;

    /// <summary>The groups for one or more anchors, in the order given (innermost first).</summary>
    public static PaneModel? ForAnchors(Corpus corpus, TranslationView view, IEnumerable<string> keys)
    {
        var anchors = keys
            .Distinct()
            .Take(MaxAnchorsPerRequest)
            .Select(k => corpus.Anchors.GetValueOrDefault(k))
            .OfType<Anchor>()
            .Where(a => view.ByFragment.ContainsKey(a.VerseId))
            .ToList();
        if (anchors.Count == 0) return null;

        var verse = view.ByFragment[anchors[0].VerseId];
        var shown = new HashSet<DisplayVerse>();
        var groups = Groups(view, anchors, shown);
        return new PaneModel(view.T.Ui, Title(view, verse), groups, CitedBy(view, verse, shown));
    }

    /// <summary>Every group on a verse, including those whose anchor has no phrase in this translation.</summary>
    public static PaneModel? ForVerse(TranslationView view, int book, int chapter, int number)
    {
        var verse = view.Verse(book, chapter, number);
        if (verse is null) return null;
        var shown = new HashSet<DisplayVerse>();
        var groups = Groups(view, verse.Anchors, shown);
        return new PaneModel(view.T.Ui, Title(view, verse), groups, CitedBy(view, verse, shown));
    }

    private static string Title(TranslationView view, DisplayVerse v) =>
        $"{view.BookName(v.BookId)} {v.Chapter}:{v.Number}";

    /// <param name="shown">Collects every verse the groups display, for dimming in the cited-by list.</param>
    private static List<GroupModel> Groups(TranslationView view, IEnumerable<Anchor> anchors, HashSet<DisplayVerse> shown)
    {
        var groups = new List<GroupModel>();
        foreach (var a in anchors)
        {
            var items = new List<ReferenceItem>();
            foreach (var r in a.Refs)
            {
                if (Item(view, r, shown) is { } item) items.Add(item);
            }
            if (items.Count == 0) continue;

            var heading = a.Text[view.T.Index];
            var fallback = false;
            if (heading.Length == 0 && view.T != Translation.Kjv)
            {
                heading = a.Text[Translation.Kjv.Index];
                fallback = heading.Length > 0;
            }

            var kjv = a.Text[Translation.Kjv.Index];
            var note = fallback || view.T == Translation.Kjv || kjv == heading ? null : kjv;
            groups.Add(new GroupModel(a.Key, heading, fallback, note, view.Colour.GetValueOrDefault(a.Key), items));
        }

        var shared = groups.GroupBy(g => g.Heading).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return groups.Select(g => shared.Contains(g.Heading) ? g : g with { KjvNote = null }).ToList();
    }

    /// <summary>
    /// One reference, resolved to the verses this translation numbers it as. A verse stored as
    /// several records appears once, with its whole text.
    /// </summary>
    private static ReferenceItem? Item(TranslationView view, int[] ids, HashSet<DisplayVerse> shown)
    {
        var verses = new List<DisplayVerse>();
        foreach (var id in ids)
        {
            if (view.ByFragment.TryGetValue(id, out var v) && v.HasText && !verses.Contains(v))
                verses.Add(v);
        }
        if (verses.Count == 0) return null;
        shown.UnionWith(verses);

        verses.Sort((a, b) =>
            a.BookId != b.BookId ? a.BookId.CompareTo(b.BookId) :
            a.Chapter != b.Chapter ? a.Chapter.CompareTo(b.Chapter) :
            a.Number.CompareTo(b.Number));

        return new ReferenceItem(Label(view, verses), Href(view, verses), TextHtml(view, verses));
    }

    /// <summary>
    /// Every other verse whose anchors point at this one, in canonical order, each listed once
    /// with the phrases that do the pointing. Incoming references land on the verse, not on a
    /// phrase in it, so the same list follows both the anchor view and the verse view.
    /// </summary>
    private static CitedSection? CitedBy(TranslationView view, DisplayVerse verse, HashSet<DisplayVerse> shown)
    {
        if (!view.CitedBy.TryGetValue(verse, out var citing)) return null;

        var tr = view.T.Index;
        var entries = new List<CitedEntry>();
        foreach (var bySource in citing.GroupBy(a => view.ByFragment[a.VerseId]))
        {
            var source = bySource.Key;
            var phrases = bySource.Select(a => a.Text[tr]).Where(p => p.Length > 0).Distinct().ToList();
            entries.Add(new CitedEntry(
                Label: Title(view, source),
                Href: VerseHref(view, source),
                Keys: string.Join(',', bySource.Select(a => a.Key)),
                Phrases: string.Join(" · ", phrases.Select(TrimWords)),
                FullPhrases: string.Join(" · ", phrases),
                Dimmed: shown.Contains(source)));
        }

        var heading = string.Format(view.T.Ui.CitedBy, Title(view, verse), entries.Count);
        return new CitedSection(heading, entries);
    }

    /// <summary>
    /// A citing verse for the cited-by preview: its whole text, with only the given anchors
    /// marked, so the reader sees exactly which words point back.
    /// </summary>
    public static PreviewModel? ForPreview(Corpus corpus, TranslationView view, IEnumerable<string> keys)
    {
        var anchors = keys
            .Distinct()
            .Take(MaxAnchorsPerRequest)
            .Select(k => corpus.Anchors.GetValueOrDefault(k))
            .OfType<Anchor>()
            .Where(a => view.ByFragment.ContainsKey(a.VerseId))
            .ToList();
        if (anchors.Count == 0) return null;

        var verse = view.ByFragment[anchors[0].VerseId];
        if (!verse.HasText) return null;

        var chosen = anchors.Where(a => view.ByFragment[a.VerseId] == verse).Select(a => a.Key).ToHashSet();
        var ranges = new List<(int Start, int End)>();
        foreach (var p in verse.Placements.Where(p => chosen.Contains(p.Anchor.Key)).OrderBy(p => p.Start))
        {
            if (ranges.Count > 0 && p.Start <= ranges[^1].End)
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, p.End));
            else
                ranges.Add((p.Start, p.End));
        }

        var sb = new StringBuilder();
        sb.Append("<sup>").Append(verse.Number).Append("</sup>");
        var at = 0;
        foreach (var (start, end) in ranges)
        {
            Html.Append(sb, verse.Text, at, start - at);
            sb.Append("<mark>");
            Html.Append(sb, verse.Text, start, end - start);
            sb.Append("</mark>");
            at = end;
        }
        Html.Append(sb, verse.Text, at, verse.Text.Length - at);

        return new PreviewModel(sb.ToString(), string.Format(view.T.Ui.GoTo, Title(view, verse)), VerseHref(view, verse));
    }

    private static string VerseHref(TranslationView view, DisplayVerse v) =>
        $"{v.BookId}/{v.Chapter}/{view.T.Code}#v{v.Number}";

    private static string TrimWords(string phrase)
    {
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= CitedPhraseWords) return phrase;
        return string.Join(' ', words.Take(CitedPhraseWords)).TrimEnd(',', ';', ':', '.', '—', '–', '-', '“', '‘', '"') + "…";
    }

    /// <summary>"Proverbs 8:22-24", "Genesis 31:55; 32:1-2", "Matthew 13:10-17; Mark 4:11".</summary>
    private static string Label(TranslationView view, List<DisplayVerse> verses)
    {
        var sb = new StringBuilder();
        foreach (var book in verses.GroupBy(v => v.BookId))
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(view.BookName(book.Key)).Append(' ');
            var first = true;
            foreach (var chapter in book.GroupBy(v => v.Chapter))
            {
                if (!first) sb.Append("; ");
                sb.Append(chapter.Key).Append(':').Append(Compress(chapter.Select(v => v.Number)));
                first = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>Links to the reference's first chapter, with its verses there in the fragment.</summary>
    private static string Href(TranslationView view, List<DisplayVerse> verses)
    {
        var first = verses[0];
        var here = verses.Where(v => v.BookId == first.BookId && v.Chapter == first.Chapter).Select(v => v.Number);
        return $"{first.BookId}/{first.Chapter}/{view.T.Code}#v{Compress(here)}";
    }

    private static string TextHtml(TranslationView view, List<DisplayVerse> verses)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < verses.Count; i++)
        {
            var v = verses[i];

            if (i == VisibleVerses) sb.Append("<span class=\"rest\">");

            if (i > 0)
            {
                var p = verses[i - 1];
                var sameChapter = p.BookId == v.BookId && p.Chapter == v.Chapter;
                var consecutive = sameChapter ? v.Number == p.Number + 1 : p.BookId == v.BookId && v.Number == 1;
                sb.Append(consecutive ? " " : " <span class=\"gap\">…</span> ");
            }

            var label = i > 0 && verses[i - 1].Chapter != v.Chapter ? $"{v.Chapter}:{v.Number}" : v.Number.ToString();
            sb.Append("<span class=\"rv\"><sup>").Append(label).Append("</sup>");
            Html.Append(sb, v.Text);
            sb.Append("</span>");
        }

        if (verses.Count > VisibleVerses)
        {
            sb.Append("</span> <button type=\"button\" class=\"more\"><span class=\"more-n\">… (+")
              .Append(verses.Count - VisibleVerses)
              .Append(")</span><span class=\"more-less\">");
            Html.Append(sb, view.T.Ui.ShowLess);
            sb.Append("</span></button>");
        }
        return sb.ToString();
    }

    /// <summary>[22, 23, 24, 26] → "22-24,26".</summary>
    private static string Compress(IEnumerable<int> numbers)
    {
        var sorted = numbers.Distinct().Order().ToList();
        var sb = new StringBuilder();
        var i = 0;
        while (i < sorted.Count)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(sorted[i]);
            if (j > i) sb.Append('-').Append(sorted[j]);
            i = j + 1;
        }
        return sb.ToString();
    }
}
