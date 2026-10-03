#!/usr/bin/env python3
"""
Builds bsb_layout.json: paragraph, poetry and heading layout for the BSB,
taken from the BSB USJ files and placed onto OUR bible_verses.json text.

Our bsb_text is canonical. The USJ contributes only where blocks start and
which headings precede them; its wording is never copied into a verse.

Output shape (keys are bible_verses.json ids):

  { "<verse_id>": [ Break, ... ], ... }

  Break = { "o": 0,          # UTF-16 offset into that record's bsb_text
            "k": "q1",       # block style: p pmo pc li1 li2 q1 q2 qr d
            "g": true,       # optional: blank line (\\b) before this block
            "h": [Heading] } # optional: headings before this block

  Heading = { "k": "s1", "t": "The Creation" }           # s1 s2 ms mr qa
          | { "k": "r",  "t": "(John 1:1–5; ...)",
              "refs": [ { "t": "John 1:1–5", "b": 43, "c": 1, "v": "1-5" } ] }

A record with no break at offset 0 continues the previous record's block
inline. Only records that carry a break appear. Offsets are into the text as
the Reader loads it: trimmed and NFC-normalised.

See README.md beside this script for how to run it.
"""
import argparse, bisect, difflib, json, re, sys, unicodedata
from collections import Counter, defaultdict
from pathlib import Path

CODES = ("GEN EXO LEV NUM DEU JOS JDG RUT 1SA 2SA 1KI 2KI 1CH 2CH EZR NEH EST "
         "JOB PSA PRO ECC SNG ISA JER LAM EZK DAN HOS JOL AMO OBA JON MIC NAM "
         "HAB ZEP HAG ZEC MAL MAT MRK LUK JHN ACT ROM 1CO 2CO GAL EPH PHP COL "
         "1TH 2TH 1TI 2TI TIT PHM HEB JAS 1PE 2PE 1JN 2JN 3JN JUD REV").split()
BOOK_ID = {c: i for i, c in enumerate(CODES, 1)}

BLOCKS   = {"p", "pmo", "m", "pc", "li1", "li2", "q1", "q2", "qr", "d"}
HEADINGS = {"s1", "s2", "ms", "mr", "qa", "r"}
IGNORED  = {"h", "toc1", "toc2", "toc3", "mt1", "mt2"}
OPENERS  = "“‘\"(["         # stay with the line they open
TOKEN    = re.compile(r"[\w’']+")
ARTIFACT = re.compile(r"\[[’‘“”]+\]|\bvvv\b")   # bsb2usfm leftovers


REPO = Path(__file__).resolve().parents[2]
JSON = REPO / "Reader" / "json"


def clean(s):
    """The same text the Reader works on: CorpusLoader.Clean()."""
    return unicodedata.normalize("NFC", s.strip()) if s and s.strip() else ""


# ---------------------------------------------------------------- USJ walk

def flat_text(node, keep_refs=None):
    """Plain text of a node, footnotes dropped. Collects \\ref items if asked."""
    out = []
    for c in node.get("content") or []:
        if isinstance(c, str):
            out.append(c)
        elif c.get("type") == "note":
            continue
        else:
            t = flat_text(c, keep_refs)
            if c.get("type") == "ref" and keep_refs is not None:
                ref = {"t": t}
                if c.get("loc"):                     # chapter spans have no loc
                    ref["loc"] = c["loc"]
                keep_refs.append(ref)
            out.append(t)
    return "".join(out)


def inline(node):
    """Yield ('v', number) for verse markers and ('t', text) for Scripture text."""
    for c in node.get("content") or []:
        if isinstance(c, str):
            yield ("t", c)
        elif c.get("type") == "verse":
            yield ("v", c["number"])
        elif c.get("type") == "note":
            continue
        else:
            yield from inline(c)


def read_usj(usj_dir, unknown):
    """
    Returns {(book, ch, vs): [[event|None, text], ...]}.
    An event marks the start of a block inside that verse's text.
    """
    verses = defaultdict(list)
    for book, code in enumerate(CODES, 1):
        doc = json.loads((usj_dir / f"{code}.usj").read_text(encoding="utf-8"))
        ch, key = None, None
        headings, gap = [], False
        for para in doc["content"]:
            t, m = para.get("type"), para.get("marker")
            if t == "chapter":
                ch, key = int(para["number"]), None
                continue
            if t == "book" or m in IGNORED:
                continue
            items = list(inline(para))
            # \d without a verse is a division subtitle ("Psalms 1–41"), not a title
            if m == "d" and not any(k == "v" for k, _ in items):
                m = "mr"
            if m in HEADINGS:
                refs = [] if m == "r" else None
                h = {"k": m, "t": re.sub(r"\s+", " ", flat_text(para, refs)).strip()}
                if refs:
                    h["refs"] = refs
                if h["t"]:
                    headings.append(h)
                continue
            if m == "b":
                gap = True
                continue
            if m not in BLOCKS:
                unknown[m] += 1
                continue

            opening = True
            for kind, val in items:
                if kind == "v":
                    key = (book, ch, int(val))
                    verses[key].append([None, ""])
                    continue
                if key is None:
                    continue
                if opening:
                    if not val.strip():
                        continue
                    ev = {"k": m}
                    if gap:      ev["g"] = True
                    if headings: ev["h"] = headings
                    verses[key].append([ev, val])
                    headings, gap, opening = [], False, False
                else:
                    verses[key][-1][1] += val
    return verses


# ------------------------------------------------------------ alignment

def tokens(s):
    out = []
    for m in TOKEN.finditer(s):
        w = m.group().strip("’'")          # quote marks are not part of the word
        if w:
            out.append((m.start() + m.group().index(w[0]), w.lower()))
    return out


def index_map(a, b):
    """
    For each token index in a: (j, None) where it matched b exactly, or
    (None, (i1, i2, j1, j2)) for the reworded span it falls in.
    """
    sm = difflib.SequenceMatcher(None, a, b, autojunk=False)
    out = [None] * (len(a) + 1)
    for op, i1, i2, j1, j2 in sm.get_opcodes():
        for i in range(i1, i2):
            out[i] = (j1 + (i - i1), None) if op == "equal" else (None, (i1, i2, j1, j2))
    out[len(a)] = (len(b), None)
    return out


CLAUSE_END = re.compile(r"[,;:.!?—–”’)]\s*$")

def place_in_reworded(i, span, ot, text):
    """
    A line starts inside wording that differs between editions. Pick the word
    in our span that most plausibly starts a line: one that follows clause
    punctuation, closest to the proportional position.
    """
    i1, i2, j1, j2 = span
    est = j1 + (i - i1) * (j2 - j1) / max(1, i2 - i1)
    cands = [j for j in range(j1, min(j2, len(ot) - 1) + 1) if j > 0]
    if not cands:
        return j1
    punct = [j for j in cands if CLAUSE_END.search(text[:ot[j][0]].rstrip(OPENERS + " "))]
    return min(punct or cands, key=lambda j: abs(j - est))


def resolve_ref(ref, last_verse):
    """ "JHN 1:1-5" -> b, c, v (the #v range). Cross-chapter spans run to the end of the
    first chapter; chapter spans without a loc ("Genesis 4–9") stay unlinked. """
    loc = ref.pop("loc", None)
    if not loc:
        return
    code, _, rest = loc.partition(" ")
    b = BOOK_ID.get(code)
    m = (re.fullmatch(r"(\d+):(\d+)(?:-(\d+))?", rest)
         or re.fullmatch(r"(\d+):(\d+)-(\d+):\d+", rest)
         or re.fullmatch(r"(\d+)-(\d+)", rest))
    if not b or not m:
        return
    g = m.groups()
    if len(g) == 2:                                   # single-chapter book: "OBA 1-14"
        c, v = 1, f"{g[0]}-{g[1]}"
    elif re.fullmatch(r"\d+:\d+-\d+:\d+", rest):  # "1CH 15:29-16:3"
        c = int(g[0]); end = last_verse.get((b, c), int(g[1]))
        v = g[1] if end <= int(g[1]) else f"{g[1]}-{end}"
    else:
        c = int(g[0]); v = g[1] + (f"-{g[2]}" if g[2] else "")
    ref.update(b=b, c=c, v=v)


def back_over_openers(s, o):
    while o > 0 and (s[o - 1] in OPENERS or (s[o - 1] == "'" and (o == 1 or s[o - 2].isspace()))):
        o -= 1
    return o


# ------------------------------------------------------------ main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--usj", required=True, type=Path, help="folder of BSB .usj files")
    ap.add_argument("--verses", default=JSON / "bible_verses.json", type=Path)
    ap.add_argument("--xrefs", default=JSON / "cross_references.json", type=Path)
    ap.add_argument("--out", default=JSON / "bsb_layout.json", type=Path)
    ap.add_argument("--report", default=Path(__file__).with_name("report.md"), type=Path)
    a = ap.parse_args()

    records = json.loads(a.verses.read_text(encoding="utf-8"))
    for r in records:
        r["bsb_text"] = clean(r["bsb_text"])
    ours = defaultdict(list)
    for r in records:
        if r["bsb_ch"] is not None:
            ours[(r["book_id"], r["bsb_ch"], r["bsb_vs"])].append(r)
    for rs in ours.values():
        rs.sort(key=lambda r: r["bsb_sort"])

    unknown = Counter()
    usj = read_usj(a.usj, unknown)

    layout = defaultdict(list)
    stats = Counter()
    review, oddities = [], []

    for key, segs in usj.items():
        rs = ours.get(key)
        if not rs or not any(r["bsb_text"] for r in rs):
            oddities.append(f"{key}: in USJ, empty or missing in bible_verses.json")
            continue

        # our verse = records joined by one space; remember where each starts
        starts, parts, pos = [], [], 0
        for r in rs:
            starts.append(pos)
            parts.append(r["bsb_text"] or "")
            pos += len(parts[-1]) + 1
        our_text = " ".join(parts)

        # USJ verse, with the position where each block's text begins
        u_text, ev_pos = "", []
        for ev, txt in segs:
            if u_text:
                u_text += " "
            if ev:
                ev_pos.append((ev, len(u_text) + (len(txt) - len(txt.lstrip()))))
            u_text += txt
        u_text_clean = ARTIFACT.sub(lambda m: " " * len(m.group()), u_text)

        ut, ot = tokens(u_text_clean), tokens(our_text)
        u_starts = [p for p, _ in ut]
        imap = index_map([w for _, w in ut], [w for _, w in ot])

        for ev, p in ev_pos:
            i = bisect.bisect_left(u_starts, p)
            j, span = imap[i]
            exact = span is None
            if j is None:
                j = place_in_reworded(i, span, ot, our_text)
            if i == 0:
                o, exact = 0, True
            elif j >= len(ot):
                stats["dropped_at_end"] += 1
                review.append((key, ev["k"], "no matching word; break dropped",
                               our_text, None, u_text[p:p + 60]))
                continue
            else:
                o = back_over_openers(our_text, ot[j][0])

            n = bisect.bisect_right(starts, o) - 1
            local = o - starts[n]
            rec = rs[n]
            if local > 0 and not (rec["bsb_text"][local - 1].isspace() or rec["bsb_text"][local - 1] in "—–"):
                oddities.append(f"{key} rec {rec['id']}: break at {local} not after whitespace "
                                f"…{rec['bsb_text'][max(0, local - 15):local]}‖{rec['bsb_text'][local:local + 15]}…")
            layout[rec["id"]].append(dict(o=local, **ev))
            stats["breaks"] += 1
            stats["breaks_exact" if exact else "breaks_approx"] += 1
            if not exact:
                review.append((key, ev["k"], "reworded near break", our_text, o, u_text[p:p + 60]))

    # ---- checks tied to our record structure
    first_of_chapter = {}
    for r in records:
        if r["bsb_ch"] is None or not r["bsb_text"]:
            continue
        k = (r["book_id"], r["bsb_ch"])
        cur = first_of_chapter.get(k)
        if cur is None or (r["bsb_vs"], r["bsb_sort"]) < (cur["bsb_vs"], cur["bsb_sort"]):
            first_of_chapter[k] = r
    for k, r in first_of_chapter.items():
        if not any(b["o"] == 0 for b in layout.get(r["id"], [])):
            oddities.append(f"chapter {k}: first record {r['id']} has no opening break")

    # superscriptions: informational. Our records split some titles off
    # (one or two records) and keep others in the same record as the first line;
    # the layout places the break either way.
    unsplit = []
    for key, rs in ours.items():
        if key[0] != 19 or not any(b["k"] == "d" for b in layout.get(rs[0]["id"], [])):
            continue
        stats["superscriptions"] += 1
        if len(rs) == 1:
            unsplit.append(f"{key[1]}")
        elif not any(b["k"] != "d" for r in rs[1:] for b in layout.get(r["id"], [])):
            oddities.append(f"Ps {key[1]}:{key[2]}: title split into records, but no line starts in a later record")
        elif any(b["k"] != "d" for b in layout[rs[0]["id"]]):
            oddities.append(f"Ps {key[1]}:{key[2]}: a line of the psalm starts inside the title record")
    for key, rs in ours.items():
        if key[0] == 19 and len(rs) > 1 and not any(b["k"] == "d" for b in layout.get(rs[0]["id"], [])):
            oddities.append(f"Ps {key[1]}:{key[2]}: split into records but no \\d in USJ")

    # \r references become link targets
    last_verse = defaultdict(int)
    for r in records:
        if r["bsb_ch"] is not None and r["bsb_text"]:
            k = (r["book_id"], r["bsb_ch"])
            last_verse[k] = max(last_verse[k], r["bsb_vs"])
    for breaks in layout.values():
        for b in breaks:
            for h in b.get("h", []):
                for ref in h.get("refs", []):
                    resolve_ref(ref, last_verse)
                    stats["refs_linked" if "b" in ref else "refs_plain"] += 1

    # sort breaks within a record; one record per line, so a rebuild diffs readably
    out = {str(k): sorted(v, key=lambda b: b["o"]) for k, v in sorted(layout.items())}
    lines = [f'"{k}":{json.dumps(v, ensure_ascii=False, separators=(",", ":"))}' for k, v in out.items()]
    a.out.write_text("{\n" + ",\n".join(lines) + "\n}\n", encoding="utf-8")

    # ---- anchors cut by a line break (what segmentation will have to handle)
    anchor_cuts = []
    if a.xrefs.exists():
        by_id = {r["id"]: r for r in records}
        for x in json.loads(a.xrefs.read_text(encoding="utf-8")):
            text, anc = (by_id[x["verse_id"]]["bsb_text"] or ""), x["bsb"]
            if not anc or x["verse_id"] not in layout:
                continue
            s = text.find(anc)
            if s < 0:
                continue
            for b in layout[x["verse_id"]]:
                if s < b["o"] < s + len(anc):
                    anchor_cuts.append((x["verse_id"], anc, text[s:b["o"]] + "‖" + text[b["o"]:s + len(anc)]))

    # ---- report
    kinds = Counter(b["k"] for v in out.values() for b in v)
    hk = Counter(h["k"] for v in out.values() for b in v for h in b.get("h", []))
    L = [f"# BSB layout report", "",
         f"Records with breaks: {len(out)}  ",
         f"Breaks: {stats['breaks']} (exact {stats['breaks_exact']}, "
         f"approximate {stats['breaks_approx']}, dropped {stats['dropped_at_end']})  ",
         f"Superscriptions: {stats['superscriptions']}, "
         f"sharing a record with the first line: {len(unsplit)} (Ps {', '.join(unsplit)})  ",
         f"Block styles: {dict(kinds.most_common())}  ",
         f"Headings: {dict(hk.most_common())}  "]
    if unknown:
        L.append(f"Unhandled markers: {dict(unknown)}  ")
    L.append(f"Parallel-passage links: {stats['refs_linked']} (unlinked chapter spans: {stats['refs_plain']})  ")
    if a.xrefs.exists():
        L.append(f"BSB anchors crossed by a line break: {len(anchor_cuts)}  ")
    L += ["", f"## Oddities ({len(oddities)})", ""] + [f"- {o}" for o in oddities]
    L += ["", f"## Breaks to review ({len(review)})", "",
          "`‖` marks where the break was placed in our text; the second line is the USJ line it came from.", ""]
    for key, k, why, text, o, uline in review:
        ref = f"{CODES[key[0] - 1]} {key[1]}:{key[2]}"
        shown = text if o is None else f"{text[max(0, o - 50):o]}‖{text[o:o + 50]}"
        L += [f"- **{ref}** `{k}` {why}", f"  - ours: …{shown}…", f"  - USJ:  {uline}…"]
    if anchor_cuts:
        L += ["", f"## Anchors crossed by a break ({len(anchor_cuts)})", ""]
        L += [f"- {vid}: {cut}" for vid, _, cut in anchor_cuts]
    a.report.write_text("\n".join(L) + "\n", encoding="utf-8")

    print(f"{stats['breaks']} breaks on {len(out)} records -> {a.out}; "
          f"{len(review)} to review, {len(oddities)} oddities -> {a.report}", file=sys.stderr)


if __name__ == "__main__":
    main()
