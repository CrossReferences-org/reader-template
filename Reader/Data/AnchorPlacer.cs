using System.Globalization;
using System.Text;

namespace Reader.Data;

/// <summary>An anchor to be placed, with the span of its own fragment inside the display verse.</summary>
public readonly record struct AnchorSlot(Anchor Anchor, int FragmentStart, int FragmentEnd);

public readonly record struct Placement(Anchor Anchor, int Start, int End)
{
    public int Length => End - Start;
    public bool Overlaps(int start, int end) => start < End && Start < end;
}

public sealed class PlacementStats
{
    public int Anchors;
    public int Empty;
    public int Placed;
    public int CaseFallback;
    public int Unplaced;
    public int Duplicates;
    public int DuplicateForcedOverlap;
    public int VersesWithOverlap;
    public int OverlapSegments;
    public int SharedSpanSegments;

    public override string ToString() =>
        $"{Anchors} anchors: {Placed} placed, {Empty} empty, {Unplaced} not found; " +
        $"{CaseFallback} matched only case-insensitively; " +
        $"{Duplicates} occur more than once ({DuplicateForcedOverlap} had no free occurrence); " +
        $"{VersesWithOverlap} verses with overlapping anchors ({OverlapSegments} overlapping segments); " +
        $"{SharedSpanSegments} words carrying several anchors on the identical span";
}

public static class AnchorPlacer
{
    /// <summary>
    /// Decides where each anchor sits in the display verse text. Every anchor is searched for
    /// only inside its own fragment: case-sensitively first, then case-insensitively.
    /// <para>
    /// Sequential (KJV): the TSK cursor. Each anchor is the first occurrence after the previous one.
    /// </para>
    /// <para>
    /// Otherwise: anchors that occur once are placed first. An anchor that occurs more than once
    /// then takes its first occurrence not overlapping anything already placed, in anchor order,
    /// or its first occurrence if none is free (the overlap is resolved by segmentation).
    /// </para>
    /// </summary>
    public static List<Placement> Place(string text, IReadOnlyList<AnchorSlot> slots, int tr, bool sequential, PlacementStats stats)
    {
        var placed = new List<Placement>();

        if (sequential)
        {
            var cursor = 0;
            foreach (var (anchor, fs, fe) in slots)
            {
                stats.Anchors++;
                var needle = anchor.Text[tr];
                if (needle.Length == 0) { stats.Empty++; continue; }

                var from = Math.Max(cursor, fs);
                var hit = FindFirst(text, needle, from, fe, StringComparison.Ordinal);
                if (hit < 0)
                {
                    hit = FindFirst(text, needle, from, fe, StringComparison.OrdinalIgnoreCase);
                    if (hit >= 0) stats.CaseFallback++;
                }
                if (hit < 0) { stats.Unplaced++; continue; }

                placed.Add(new Placement(anchor, hit, hit + needle.Length));
                cursor = hit + needle.Length;
                stats.Placed++;
            }
            return placed;
        }

        var found = new List<(Anchor Anchor, List<int> Hits, int Length)>();
        foreach (var (anchor, fs, fe) in slots)
        {
            stats.Anchors++;
            var needle = anchor.Text[tr];
            if (needle.Length == 0) { stats.Empty++; continue; }

            var hits = FindAll(text, needle, fs, fe, StringComparison.Ordinal);
            if (hits.Count == 0)
            {
                hits = FindAll(text, needle, fs, fe, StringComparison.OrdinalIgnoreCase);
                if (hits.Count > 0) stats.CaseFallback++;
            }
            if (hits.Count == 0) { stats.Unplaced++; continue; }

            found.Add((anchor, hits, needle.Length));
        }

        foreach (var (anchor, hits, length) in found)
        {
            if (hits.Count != 1) continue;
            placed.Add(new Placement(anchor, hits[0], hits[0] + length));
            stats.Placed++;
        }

        foreach (var (anchor, hits, length) in found)
        {
            if (hits.Count == 1) continue;
            stats.Duplicates++;

            var start = -1;
            foreach (var h in hits)
            {
                if (!placed.Any(p => p.Overlaps(h, h + length))) { start = h; break; }
            }
            if (start < 0)
            {
                start = hits[0];
                stats.DuplicateForcedOverlap++;
            }

            placed.Add(new Placement(anchor, start, start + length));
            stats.Placed++;
        }

        return placed;
    }

    /// <summary>
    /// Colour slots cycle 1–4 in reading order, so neighbouring anchors differ. Anchors on the
    /// identical span (several KJV anchors mapped to one word) share a slot, since they read as
    /// one highlight. Ties at the same start go to the longer anchor first.
    /// </summary>
    public static void AssignColours(List<Placement> placed, Dictionary<string, int> colours)
    {
        placed.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var slot = -1;
        for (var i = 0; i < placed.Count; i++)
        {
            var sameSpan = i > 0 && placed[i].Start == placed[i - 1].Start && placed[i].End == placed[i - 1].End;
            if (!sameSpan) slot++;
            colours[placed[i].Anchor.Key] = slot % 4 + 1;
        }
    }

    /// <summary>
    /// Renders the verse paragraph: one verse per line, for translations without a layout.
    /// </summary>
    public static string RenderVerse(DisplayVerse verse, List<Placement> placed, IReadOnlyDictionary<string, int> colours, PlacementStats stats)
    {
        var text = verse.Text;
        var sb = new StringBuilder(text.Length * 2 + 64);
        sb.Append("<p class=\"verse\" id=\"v").Append(verse.Number)
          .Append("\"><span class=\"vn\" role=\"button\" tabindex=\"0\" data-v=\"").Append(verse.Number).Append("\">")
          .Append(verse.Number).Append("</span> ");

        var hadOverlap = false;
        AppendSegments(sb, text, 0, text.Length, placed, colours, stats, ref hadOverlap);

        if (hadOverlap) stats.VersesWithOverlap++;
        sb.Append("</p>");
        return sb.ToString();
    }

    /// <summary>
    /// Writes <c>text[from..to)</c> cut at every anchor boundary, so each segment is covered by
    /// a fixed set of anchors. A segment's <c>data-a</c> lists them innermost first (shortest
    /// anchor), and it takes the innermost anchor's colour. An anchor running past
    /// <paramref name="to"/> is cut there and continues in the next call.
    /// </summary>
    public static void AppendSegments(StringBuilder sb, string text, int from, int to, IReadOnlyList<Placement> placed,
        IReadOnlyDictionary<string, int> colours, PlacementStats stats, ref bool hadOverlap)
    {
        var cuts = new SortedSet<int> { from, to };
        foreach (var p in placed)
        {
            if (p.Start > from && p.Start < to) cuts.Add(p.Start);
            if (p.End > from && p.End < to) cuts.Add(p.End);
        }

        var covering = new List<Placement>();
        int? previous = null;
        foreach (var cut in cuts)
        {
            if (previous is int start && cut > start)
            {
                covering.Clear();
                foreach (var p in placed)
                    if (p.Start <= start && cut <= p.End) covering.Add(p);

                if (covering.Count == 0)
                {
                    Html.Append(sb, text, start, cut - start);
                }
                else
                {
                    covering.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.Start.CompareTo(b.Start));
                    var multi = covering.Count > 1;
                    if (multi)
                    {
                        var identical = covering.All(p => p.Start == covering[0].Start && p.End == covering[0].End);
                        if (identical) stats.SharedSpanSegments++;
                        else { stats.OverlapSegments++; hadOverlap = true; }
                    }

                    sb.Append("<span class=\"a c").Append(colours[covering[0].Anchor.Key]);
                    if (multi) sb.Append(" m");
                    sb.Append("\" role=\"button\" tabindex=\"0\" data-a=\"");
                    for (var i = 0; i < covering.Count; i++)
                    {
                        if (i > 0) sb.Append(' ');
                        sb.Append(covering[i].Anchor.Key);
                    }
                    sb.Append("\">");
                    Html.Append(sb, text, start, cut - start);
                    sb.Append("</span>");
                }
            }
            previous = cut;
        }
    }

    private static int FindFirst(string text, string needle, int from, int end, StringComparison comparison)
    {
        var pos = from;
        while (pos <= end - needle.Length)
        {
            var hit = text.IndexOf(needle, pos, end - pos, comparison);
            if (hit < 0) return -1;
            if (IsWholeMatch(text, needle, hit)) return hit;
            pos = hit + 1;
        }
        return -1;
    }

    private static List<int> FindAll(string text, string needle, int from, int end, StringComparison comparison)
    {
        var hits = new List<int>();
        var pos = from;
        while (pos <= end - needle.Length)
        {
            var hit = text.IndexOf(needle, pos, end - pos, comparison);
            if (hit < 0) break;
            if (IsWholeMatch(text, needle, hit)) hits.Add(hit);
            pos = hit + 1;
        }
        return hits;
    }

    /// <summary>
    /// A match must not start or end in the middle of a word. The check only applies at an edge
    /// that is itself a word character: an anchor ending in "—" or "!" may be followed directly
    /// by a word, as in "morning—the first day".
    /// </summary>
    private static bool IsWholeMatch(string text, string needle, int start)
    {
        var end = start + needle.Length;
        if (IsWordChar(needle[0]) && start > 0 && IsWordChar(text[start - 1])) return false;
        if (IsWordChar(needle[^1]) && end < text.Length && IsWordChar(text[end])) return false;
        return true;
    }

    private static bool IsWordChar(char c)
    {
        if (char.IsLetterOrDigit(c) || c == '_') return true;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation;
    }
}

public static class Html
{
    public static void Append(StringBuilder sb, string s, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    public static void Append(StringBuilder sb, string s) => Append(sb, s, 0, s.Length);
}
