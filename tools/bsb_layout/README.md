# BSB layout

Builds `Reader/json/bsb_layout.json`: the BSB's paragraphs, poetry lines, Psalm titles and
section headings, placed onto the Reader's own `bible_verses.json` text.

Our `bsb_text` stays canonical. The BSB USJ files only say where blocks start and which
headings come before them; their wording is never copied in. Where the two editions word a
passage differently, a break is placed at the matching word boundary, and every such case is
listed in `report.md` for checking.

The Reader works without this file: if it is missing, the BSB shows one verse per line.

## Rebuild

Needs Python 3.10+ and the BSB USJ files (one `.usj` per book, e.g. `GEN.usj`; the files used
so far are the `bsb2usfm` output). Run it from anywhere:

```sh
python3 tools/bsb_layout/build_bsb_layout.py --usj /path/to/bsb_usj
```

By default it reads `Reader/json/bible_verses.json` and `cross_references.json`, writes
`Reader/json/bsb_layout.json`, and writes `report.md` beside the script. Override with
`--verses`, `--xrefs`, `--out` and `--report`.

Rebuild whenever `bsb_text` changes: offsets are character positions into each verse
record's text (trimmed and NFC-normalised, as the Reader loads it).

## Format

One line per verse record id, only for records where a block starts:

```json
"142560":[{"o":0,"k":"d","h":[{"k":"s1","t":"The LORD Is My Shepherd"},{"k":"r","t":"(Ezekiel 34:11–24; John 10:1–21)","refs":[{"t":"Ezekiel 34:11–24","b":26,"c":34,"v":"11-24"}, …]}]},{"o":18,"k":"q1"},{"o":43,"k":"q2"}]
```

- `o`: offset into the record's text where the block starts
- `k`: block style, as the USFM marker: `p` `pmo` `pc` `li1` `li2` `q1` `q2` `qr` `d`
- `g`: a blank line (`\b`) before the block
- `h`: headings before it. `k` is `s1` `s2` `ms` `mr` `qa` or `r`; an `r` heading lists the
  passages it names, with book id, chapter and `#v` range for links

A record with no break at offset 0 continues the block before it.

## Report

`report.md` records the counts, the breaks placed inside reworded passages, oddities in the
record structure (e.g. Ps 59:1), and the anchors a line break passes through.
