namespace Reader.Data;

public enum Language { English, Afrikaans, French }

/// <summary>
/// A translation the reader can show. <see cref="Index"/> is the slot this translation
/// occupies in the per-translation arrays on <see cref="Fragment"/> and <see cref="Anchor"/>.
/// </summary>
public sealed class Translation
{
    public static readonly Translation Kjv = new(0, "KJV", "English (KJV)", Language.English, sequential: true);
    public static readonly Translation Bsb = new(1, "BSB", "English (BSB)", Language.English, sequential: false);
    public static readonly Translation Aov = new(2, "AOV", "Afrikaans", Language.Afrikaans, sequential: false);
    public static readonly Translation S21 = new(3, "S21", "Français", Language.French, sequential: false);

    public static readonly IReadOnlyList<Translation> All = [Kjv, Bsb, Aov, S21];
    public const int Count = 4;

    private Translation(int index, string code, string label, Language language, bool sequential)
    {
        Index = index;
        Code = code;
        Label = label;
        Language = language;
        Sequential = sequential;
    }

    public int Index { get; }
    public string Code { get; }
    public string Label { get; }
    public Language Language { get; }

    /// <summary>
    /// The TSK locates anchors with a cursor: each anchor is the next occurrence after the
    /// previous one. That only holds for the KJV, whose wording the anchors were written in.
    /// </summary>
    public bool Sequential { get; }

    public string HtmlLang => Language switch
    {
        Language.Afrikaans => "af",
        Language.French => "fr",
        _ => "en",
    };

    public UiText Ui => UiText.For(Language);
}

public sealed record UiText(
    string CrossReferences,
    string PaneHint,
    string NoReferences,
    string LoadError,
    string ShowLess,
    string PreviousChapter,
    string NextChapter,
    string Books,
    string Close,
    string ToggleTheme,
    string Translation,
    string CitedBy,
    string GoTo,
    string GoToReference,
    string AttributionBefore,
    string AttributionAfter,
    string Description)
{
    private static readonly UiText English = new(
        CrossReferences: "Cross references",
        PaneHint: "Select a highlighted word or a verse number.",
        NoReferences: "No cross references for this verse.",
        LoadError: "The references could not be loaded. Try again.",
        ShowLess: "Show less",
        PreviousChapter: "Previous chapter",
        NextChapter: "Next chapter",
        Books: "Books",
        Close: "Close",
        ToggleTheme: "Toggle dark mode",
        Translation: "Translation",
        CitedBy: "{0} is cited by {1}",
        GoTo: "Go to {0}",
        GoToReference: "Go to a reference",
        AttributionBefore: "Cross references from",
        AttributionAfter: "(CC BY 4.0), built on the Treasury of Scripture Knowledge.",
        Description: "Read {0} with phrase-level cross references.");

    private static readonly UiText Afrikaans = new(
        CrossReferences: "Kruisverwysings",
        PaneHint: "Kies ’n gemerkte woord of ’n versnommer.",
        NoReferences: "Geen kruisverwysings vir hierdie vers nie.",
        LoadError: "Die verwysings kon nie gelaai word nie. Probeer weer.",
        ShowLess: "Wys minder",
        PreviousChapter: "Vorige hoofstuk",
        NextChapter: "Volgende hoofstuk",
        Books: "Boeke",
        Close: "Maak toe",
        ToggleTheme: "Wissel donker modus",
        Translation: "Vertaling",
        CitedBy: "{0} word aangehaal deur {1}",
        GoTo: "Gaan na {0}",
        GoToReference: "Gaan na ’n verwysing",
        AttributionBefore: "Kruisverwysings van",
        AttributionAfter: "(CC BY 4.0), gebou op die Treasury of Scripture Knowledge.",
        Description: "Lees {0} met kruisverwysings op frasevlak.");

    private static readonly UiText French = new(
        CrossReferences: "Références croisées",
        PaneHint: "Choisissez un mot en couleur ou un numéro de verset.",
        NoReferences: "Aucune référence croisée pour ce verset.",
        LoadError: "Impossible de charger les références. Réessayez.",
        ShowLess: "Voir moins",
        PreviousChapter: "Chapitre précédent",
        NextChapter: "Chapitre suivant",
        Books: "Livres",
        Close: "Fermer",
        ToggleTheme: "Changer de thème",
        Translation: "Traduction",
        CitedBy: "{0} est cité par {1}",
        GoTo: "Aller à {0}",
        GoToReference: "Aller à une référence",
        AttributionBefore: "Références croisées de",
        AttributionAfter: "(CC BY 4.0), fondées sur le Treasury of Scripture Knowledge.",
        Description: "Lire {0} avec des références croisées au niveau de la phrase.");

    public static UiText For(Language language) => language switch
    {
        Language.Afrikaans => Afrikaans,
        Language.French => French,
        _ => English,
    };
}
