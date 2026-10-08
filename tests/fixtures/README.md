# Fixtures

Each case here is a rule from the reader, shown on a real verse, with what the reader builds
from it. Together they are the specification for turning the data into a reader: rebuilding
verses, placing anchors, cutting the text into highlighted segments, and labelling references.
The C# tests check the reader against them. If you port the reader to another language, check
your port against the same files.

- `cases.json`: the input. A `books` list, and per case a small corpus in the same format as
  the files in `Reader/json/` (`bible_verses`, `cross_references`), plus the panes to build.
- `expected/{case}.json`: what the reader makes of that case, per translation.

Each case's `rule` says in words what it shows.

## Data updates don't affect these

The records are copies, taken from the data in October 2026, and they stay as they are when
the data changes. A corrected word in the BSB, or a new cross-reference, does not touch
them. They test the rules, not the current text. Two edits were made to the copied records:

- Only the anchors on a case's main verses are included, and their `refs` are cut down to the
  references the case is about, or to `[]`.
- Referenced verses are included without their own anchors.

## Expected output

Per translation (`KJV`, `BSB`, `AOV`):

- `stats`: totals over the case's anchors.
  - `not_found`: the phrase is not in the verse.
  - `no_phrase`: the translation has no phrase for that anchor.
  - `occur_more_than_once`: the phrase is found more than once (outside the KJV).
  - `no_free_occurrence`: every one of those occurrences overlaps an anchor already placed.
  - Anchors on a verse the translation omits are not counted.
- `verses`: every verse in the corpus, in this translation's numbering.
  - `records`: the record ids that make up the verse, in this translation's order.
  - `text`: the rebuilt verse, with records joined by a space. It is empty for a verse the
    translation omits; such a verse has no other fields.
  - `placed`: where each anchor sits, in reading order.
    - `start` and `end` are offsets into `text`, end exclusive. Offsets count UTF-16 code
      units; every text here is in the Basic Multilingual Plane, so they equal code point
      offsets too.
    - `colour` is the anchor's colour slot (1–4).
    - `case_fallback` is true when the phrase matched only case-insensitively.
  - `not_found` and `no_phrase`: anchor keys, as in `stats`.
  - `segments`: the text as rendered. Each item is `[text, keys]`, where `keys` lists the anchors
    covering that piece, innermost (shortest) first. An empty list means plain text.
  - `cited_by`: the anchors elsewhere whose references include this verse.
- `panes`: for each request in the case, the pane the reader builds.
  - A request is either `keys` (the anchors on a selected word, innermost first) or
    `verse_of` (a selected verse number, given as the id of a record in that verse).
  - `pane` is null when there is nothing to show.
  - Group `colour` is 0 when the anchor is not highlighted in the text.

The fixtures do not cover the BSB paragraph layout, the S21, the HTML of the pane, or
`reader.js`.

## Running and updating

```sh
dotnet test
```

To add a case, add it to `cases.json` with records copied from `Reader/json/`, and generate
its expected output from the current code:

```sh
UPDATE_EXPECTED=1 dotnet test
```

(In PowerShell: `$env:UPDATE_EXPECTED=1; dotnet test`, then `Remove-Item env:UPDATE_EXPECTED`.)
This rewrites every file in `expected/`. Read the diff before you keep it. Anything that
changed is a change in the reader's behaviour.
