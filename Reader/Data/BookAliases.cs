namespace Reader.Data;

/// <summary>
/// Other ways a reader may write a book's name in a translation's language: older or newer
/// spellings, and common abbreviations that are not a prefix of the name. They are matched
/// when a reference is typed, never shown: completion always offers the book's own name.
/// Accents, case, spaces and dots are ignored when matching, so none of those need listing.
/// </summary>
public static class BookAliases
{
    private static readonly Dictionary<(Language, int), string[]> Table = new()
    {
        // English
        [(Language.English, 5)] = ["Dt"],
        [(Language.English, 7)] = ["Jdg"],
        [(Language.English, 22)] = ["Song of Songs", "Canticles"],
        [(Language.English, 40)] = ["Mt"],
        [(Language.English, 41)] = ["Mk"],
        [(Language.English, 42)] = ["Lk"],
        [(Language.English, 43)] = ["Jn"],
        [(Language.English, 50)] = ["Php"],
        [(Language.English, 57)] = ["Phm"],
        [(Language.English, 62)] = ["1 Jn"],
        [(Language.English, 63)] = ["2 Jn"],
        [(Language.English, 64)] = ["3 Jn"],
        [(Language.English, 66)] = ["Revelations"],

        // French: the Catholic names, where they differ from the Protestant ones
        [(Language.French, 21)] = ["Qohélet", "Qo"],
        [(Language.French, 23)] = ["Isaïe", "Is"],

        // Afrikaans: modern spellings of the Ou Vertaling's names
		[(Language.Afrikaans, 2)] = ["Exodus"],
        [(Language.Afrikaans, 40)] = ["Matteus"],
        [(Language.Afrikaans, 46)] = ["1 Korintiërs"],
        [(Language.Afrikaans, 47)] = ["2 Korintiërs"],
        [(Language.Afrikaans, 52)] = ["1 Tessalonisense"],
        [(Language.Afrikaans, 53)] = ["2 Tessalonisense"],
        [(Language.Afrikaans, 54)] = ["1 Timoteus"],
        [(Language.Afrikaans, 55)] = ["2 Timoteus"],
    };

    public static IReadOnlyList<string> For(Language language, int bookId) =>
        Table.TryGetValue((language, bookId), out var aliases) ? aliases : [];
}
