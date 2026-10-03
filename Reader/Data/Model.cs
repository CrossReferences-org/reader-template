namespace Reader.Data;

public sealed class BookInfo(
    int id,
    string nameEng, string nameFra, string nameAfr,
    string abbreviationEng, string abbreviationFra, string abbreviationAfr)
{
    public int Id { get; } = id;

    public string Name(Language language) => language switch
    {
        Language.Afrikaans => nameAfr,
        Language.French => nameFra,
        _ => nameEng,
    };

    public string Abbreviation(Language language) => language switch
    {
        Language.Afrikaans => abbreviationAfr,
        Language.French => abbreviationFra,
        _ => abbreviationEng,
    };
}

/// <summary>A translation's coordinates and text for one verse record.</summary>
public readonly record struct Coord(int Chapter, int Verse, int Sort, string Text)
{
    public bool Exists => Chapter > 0 && Verse > 0;
}

/// <summary>
/// One record of bible_verses.json: a piece of text every translation agrees is one unit.
/// Usually a whole verse; sometimes part of one, where versifications differ.
/// </summary>
public sealed class Fragment(int id, int bookId, Coord[] coords)
{
    public int Id { get; } = id;
    public int BookId { get; } = bookId;
    public Coord[] Coords { get; } = coords;
}

/// <summary>One row of cross_references.json.</summary>
public sealed class Anchor(int verseId, int sort, string[] text, int[][] refs)
{
    public int VerseId { get; } = verseId;
    public int Sort { get; } = sort;

    /// <summary>Stable across data releases, so it is safe to put in cacheable URLs.</summary>
    public string Key { get; } = $"{verseId}.{sort}";

    /// <summary>Anchor phrase per translation index; empty when the translation has none.</summary>
    public string[] Text { get; } = text;

    /// <summary>Each inner array is one reference, possibly spanning several verse records.</summary>
    public int[][] Refs { get; } = refs;
}

/// <summary>A verse as a translation numbers it: one or more fragments joined in that translation's order.</summary>
public sealed class DisplayVerse
{
    public required int BookId { get; init; }
    public required int Chapter { get; init; }
    public required int Number { get; init; }
    public required string Text { get; init; }

    /// <summary>Anchors on this verse, in reading order: by fragment, then by anchor sort.</summary>
    public required Anchor[] Anchors { get; init; }

    /// <summary>Where each anchor sits in <see cref="Text"/>, in reading order.</summary>
    public Placement[] Placements { get; set; } = [];

    /// <summary>
    /// Blocks that start inside this verse (paragraphs, poetry lines, headings above them),
    /// with offsets into <see cref="Text"/>. Empty for translations without a layout.
    /// </summary>
    public LayoutBreak[] Breaks { get; set; } = [];

    public bool HasText => Text.Length > 0;
}

public sealed record BookEntry(BookInfo Book, string Name, int Chapters, string Spine, bool StartsNewTestament)
{
    public int Id => Book.Id;
}

/// <summary>Everything the reader needs for one translation, built once at startup.</summary>
public sealed class TranslationView
{
    public required Translation T { get; init; }
    public required IReadOnlyList<BookEntry> Books { get; init; }
    public required IReadOnlyDictionary<int, BookEntry> BookById { get; init; }
    public required IReadOnlyDictionary<(int Book, int Chapter), DisplayVerse[]> Chapters { get; init; }

    /// <summary>Each chapter's text, pre-rendered with its anchor segments (and layout, where there is one).</summary>
    public required IReadOnlyDictionary<(int Book, int Chapter), string> ChapterHtml { get; init; }

    /// <summary>Fragment id to the display verse that contains it, in this translation.</summary>
    public required IReadOnlyDictionary<int, DisplayVerse> ByFragment { get; init; }

    /// <summary>
    /// For each verse, the anchors elsewhere whose references include it: the corpus read in
    /// the reverse direction. Each citing anchor appears once per verse it points into.
    /// </summary>
    public required IReadOnlyDictionary<DisplayVerse, Anchor[]> CitedBy { get; init; }

    /// <summary>Anchor key to its colour slot (1–4), for anchors placed in the text.</summary>
    public required IReadOnlyDictionary<string, int> Colour { get; init; }

    public DisplayVerse[]? Chapter(int book, int chapter) =>
        Chapters.TryGetValue((book, chapter), out var verses) ? verses : null;

    public DisplayVerse? Verse(int book, int chapter, int number) =>
        Chapter(book, chapter)?.FirstOrDefault(v => v.Number == number);

    public string BookName(int bookId) =>
        BookById.TryGetValue(bookId, out var b) ? b.Name : bookId.ToString();

    public (int Book, int Chapter)? Previous(int book, int chapter)
    {
        if (chapter > 1) return (book, chapter - 1);
        var i = IndexOf(book);
        return i > 0 ? (Books[i - 1].Id, Books[i - 1].Chapters) : null;
    }

    public (int Book, int Chapter)? Next(int book, int chapter)
    {
        if (BookById.TryGetValue(book, out var b) && chapter < b.Chapters) return (book, chapter + 1);
        var i = IndexOf(book);
        return i >= 0 && i < Books.Count - 1 ? (Books[i + 1].Id, 1) : null;
    }

    private int IndexOf(int book)
    {
        for (var i = 0; i < Books.Count; i++)
            if (Books[i].Id == book) return i;
        return -1;
    }
}

public sealed class Corpus(IReadOnlyDictionary<string, Anchor> anchors, IReadOnlyList<TranslationView> views)
{
    public IReadOnlyDictionary<string, Anchor> Anchors { get; } = anchors;
    public IReadOnlyList<TranslationView> Views { get; } = views;

    public TranslationView? Find(string? code) =>
        code is null ? null : Views.FirstOrDefault(v => string.Equals(v.T.Code, code, StringComparison.OrdinalIgnoreCase));
}
