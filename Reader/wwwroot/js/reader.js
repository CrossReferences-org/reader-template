/*
 * reader.js — everything here is presentational. The server renders the text, the anchors
 * and every reference fragment; this script only fetches fragments and swaps them in,
 * builds chapter links on demand, and keeps small bits of UI state.
 *
 * Works with or without Blazor's enhanced navigation: listeners are delegated on the
 * document once, and per-page setup runs on load and again after every enhanced navigation.
 */
(() => {
    'use strict';

    const doc = document;
    const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
    // Phones: the reference pane covers the text, so it behaves as a screen of its own.
    const narrow = matchMedia('(max-width: 760px)');
    const page = () => doc.querySelector('.reader');
    const resolve = path => new URL(path, doc.baseURI).href;

    /** The chapter a URL points to ({ book, chapter, tr }), or null for anything else. */
    function chapterOf(url) {
        const base = new URL(doc.baseURI);
        if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) return null;
        const m = /^(\d+)\/(\d+)\/([^/]+)\/?$/.exec(url.pathname.slice(base.pathname.length));
        return m ? { book: m[1], chapter: m[2], tr: m[3].toUpperCase() } : null;
    }

    /** Whether that is the chapter on screen. Asked of the page, not of location.href,
        which Blazor may already have moved on by the time other listeners run. */
    function isShown(target) {
        const r = page();
        return !!r && !!target && target.book === r.dataset.book
            && target.chapter === r.dataset.chapter && target.tr === r.dataset.tr;
    }

    // ---- Reference pane ---------------------------------------------------------------

    let inFlight = null;

    /** What the pane shows, so it can be reopened on Back: { a: segment keys } or { v: verse }. */
    let selection = null;

    async function loadPane(path, open = true) {
        const reader = page();
        const body = doc.getElementById('pane-body');
        if (!reader || !body) return;

        inFlight?.abort();
        const request = inFlight = new AbortController();
        if (open) openPane();
        body.setAttribute('aria-busy', 'true');

        try {
            const res = await fetch(resolve(path), { signal: request.signal });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const html = await res.text();
            if (request !== inFlight) return;
            body.innerHTML = html;
            body.scrollTop = 0;
            saveSoon();
        } catch (err) {
            if (err.name === 'AbortError') return;
            const p = doc.createElement('p');
            p.className = 'hint error';
            p.textContent = reader.dataset.error;
            body.replaceChildren(p);
        } finally {
            if (request === inFlight) {
                body.removeAttribute('aria-busy');
                inFlight = null;
            }
        }
    }

    // On a phone, opening the pane adds a history entry for the same address, so Back closes
    // the pane instead of leaving the chapter. Not while restoring: that would cut off the
    // entries ahead of this one.
    let restoringNow = false;

    function openPane() {
        const reader = page();
        if (narrow.matches && !restoringNow && !reader.classList.contains('pane-open') && !history.state?.crPane) {
            saveState();
            history.pushState({ ...(history.state ?? {}), crPane: true }, '');
        }
        reader.classList.add('pane-open');
    }

    function closePane() {
        const reader = page();
        if (!reader?.classList.contains('pane-open')) return;
        if (narrow.matches && history.state?.crPane) { history.back(); return; } // popstate closes it
        reader.classList.remove('pane-open');
        saveSoon();
    }

    function clearSelection() {
        doc.querySelectorAll('.scripture .on').forEach(el => el.classList.remove('on'));
    }

    /** Every element of the verse el is in. Where paragraphs and poetry lines are laid out
        (the BSB), a verse can run across several blocks, one span.verse piece in each. */
    function pieces(el) {
        const verse = el.closest('.verse');
        if (!verse) return [];
        const n = verse.dataset.v;
        return n ? [...doc.querySelectorAll(`.scripture .verse[data-v="${CSS.escape(n)}"]`)] : [verse];
    }

    function selectAnchor(segment, open = true) {
        const reader = page();
        const keys = segment.dataset.a.split(' ');
        const primary = keys[0];
        const parts = pieces(segment);

        clearSelection();
        // Mark every segment of the innermost anchor: an anchor cut by an overlap or a line
        // break still lights up as one phrase.
        parts.forEach(p => p.querySelectorAll('.a').forEach(s => {
            if (s.dataset.a.split(' ').includes(primary)) s.classList.add('on');
        }));
        parts.forEach(p => p.querySelector('.vn')?.classList.add('on'));

        selection = { a: segment.dataset.a };
        return loadPane(`pane/${reader.dataset.tr}/anchors?k=${encodeURIComponent(keys.join(','))}`, open);
    }

    /** quiet: mark only the number, not the whole verse, where the verse has just been flashed
        and a lasting highlight would read as a second, different one. */
    function selectVerse(number, open = true, quiet = false) {
        const reader = page();
        clearSelection();
        number.classList.add('on');
        if (!quiet) pieces(number).forEach(p => p.classList.add('on'));
        selection = quiet ? { v: number.dataset.v, quiet: true } : { v: number.dataset.v };
        const { tr, book, chapter } = reader.dataset;
        return loadPane(`pane/${tr}/verse/${book}/${chapter}/${number.dataset.v}`, open);
    }

    // ---- Cited by: a citing verse opened in place ---------------------------------------

    async function toggleCited(row, force) {
        const box = row.parentElement.querySelector('.cited-preview');
        const open = force ?? row.getAttribute('aria-expanded') !== 'true';
        row.setAttribute('aria-expanded', String(open));
        box.hidden = !open;
        saveSoon();
        if (!open || box.dataset.loaded) return;

        box.setAttribute('aria-busy', 'true');
        try {
            const res = await fetch(resolve(`pane/${page().dataset.tr}/preview?k=${encodeURIComponent(row.dataset.k)}`));
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            box.innerHTML = await res.text();
            box.dataset.loaded = '1';
        } catch {
            const p = doc.createElement('p');
            p.className = 'hint error';
            p.textContent = page()?.dataset.error ?? '';
            box.replaceChildren(p);
        } finally {
            box.removeAttribute('aria-busy');
        }
    }

    // ---- Coming back: the history entry remembers where the reader was -----------------
    //
    // Both panes scroll inside their own containers, so the browser's scroll restoration
    // never sees them, and the reference pane exists only in the page. Their state is kept
    // in history.state (merged, since Blazor keeps its own fields there) and restored when
    // the entry is revisited through Back, Forward or a reload.

    let saveTimer = 0;
    let restoring = false;
    /** The chapter onPage last set up, to tell a re-render of the same page from a new one. */
    let shownPage = null;

    function snapshot() {
        const reader = page();
        const text = doc.getElementById('text-pane');
        const body = doc.getElementById('pane-body');
        const { book, chapter, tr } = reader.dataset;
        return {
            page: `${book}/${chapter}/${tr}`,
            text: text?.scrollTop ?? 0,
            sel: selection,
            paneScroll: body?.scrollTop ?? 0,
            paneOpen: reader.classList.contains('pane-open'),
            open: [...doc.querySelectorAll('.pane-body .cited-row[aria-expanded="true"]')].map(r => r.dataset.k),
        };
    }

    function saveState() {
        clearTimeout(saveTimer);
        saveTimer = 0;
        // While Blazor fetches the next chapter, the address bar already names it but the
        // old page is still on screen: never write the old page's state into the new entry.
        if (!page() || !isShown(chapterOf(new URL(location.href)))) return;
        history.replaceState({ ...(history.state ?? {}), cr: snapshot() }, '');
    }

    function saveSoon() {
        if (!saveTimer) saveTimer = setTimeout(saveState, 150);
    }

    async function restore(state) {
        const reader = page();
        const { book, chapter, tr } = reader.dataset;
        if (state.page !== `${book}/${chapter}/${tr}`) return false;
        restoringNow = true;
        try { return await restoreInto(state); } finally { restoringNow = false; }
    }

    async function restoreInto(state) {
        const text = doc.getElementById('text-pane');
        const setText = () => { if (text) text.scrollTop = state.text; };
        setText();
        doc.fonts?.ready.then(setText); // late web fonts can move the text after the first pass

        const sel = state.sel;
        const target = sel?.a !== undefined
            ? doc.querySelector(`.scripture .a[data-a="${CSS.escape(sel.a)}"]`)
            : sel?.v !== undefined ? doc.querySelector(`.scripture .vn[data-v="${CSS.escape(sel.v)}"]`) : null;
        if (!target) return true;

        await (sel.a !== undefined ? selectAnchor(target, state.paneOpen) : selectVerse(target, state.paneOpen, !!sel.quiet));
        await Promise.all((state.open ?? []).map(k => {
            const row = doc.querySelector(`.pane-body .cited-row[data-k="${CSS.escape(k)}"]`);
            return row ? toggleCited(row, true) : null;
        }));
        const body = doc.getElementById('pane-body');
        if (body) body.scrollTop = state.paneScroll;
        return true;
    }

    // ---- Go to a reference: "Rom 8:28" -------------------------------------------------
    //
    // The chapter heading doubles as an input. It takes a book, a book and chapter, or a book,
    // chapter and verse, and matches only the current translation's language: the book names
    // and abbreviations in the nav, plus the spelling variants each book carries (data-alias).
    // Case, accents, spaces and dots are ignored. Tab completes the book; the completion also
    // shows as ghost text after the cursor, which can be tapped where there is no Tab key.

    const fold = s => s.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
    const keyOf = s => fold(s).replace(/[^\p{L}\p{N}]/gu, '');

    // Book, then chapter, then verse; the verse after any of : . , or a space.
    const REFERENCE = /^([1-3]?)[\s.]*(\p{L}[\p{L}\s.'’-]*?)[\s.]*(?:(\d+)(?:\s*[:.,\s]\s*(\d+))?)?\s*$/u;
    // What is typed before the chapter: the only part Tab completes.
    const BOOK_ONLY = /^\s*[1-3]?[\s.]*[\p{L}\s.'’-]*$/u;

    /** The nav's books for this translation; the nav only changes with a full page load. */
    let books = null;
    function bookTable() {
        books ??= [...doc.querySelectorAll('#nav .book')].map(item => {
            const name = item.querySelector('.book-name').textContent.trim();
            return {
                id: item.dataset.book,
                chapters: Number(item.dataset.chapters),
                name,
                nameKey: keyOf(name),
                abbrKey: keyOf(item.dataset.abbr ?? ''),
                aliasKeys: (item.dataset.alias ?? '').split('|').filter(Boolean).map(keyOf),
            };
        });
        return books;
    }

    /** Books a typed key could mean, best first: exact abbreviation, exact name, exact variant,
        then prefixes in canonical order, variants last so they never displace a real name. */
    function candidates(key) {
        if (!key) return [];
        const all = bookTable();
        const found = new Set();
        const take = test => all.forEach(b => { if (test(b)) found.add(b); });
        take(b => b.abbrKey === key);
        take(b => b.nameKey === key);
        take(b => b.aliasKeys.includes(key));
        take(b => b.abbrKey.startsWith(key) || b.nameKey.startsWith(key));
        take(b => b.aliasKeys.some(a => a.startsWith(key)));
        return [...found];
    }

    /** "Rom 8:28" → { book, chapter, verse }, verse 0 when none was given; null if no book fits. */
    function parseReference(text) {
        const m = REFERENCE.exec(fold(text.trim()));
        if (!m) return null;
        const book = candidates(keyOf(m[1] + m[2]))[0];
        if (!book) return null;

        let chapter = m[3] ? Number(m[3]) : 1;
        let verse = m[4] ? Number(m[4]) : 0;
        // "Jude 5" is verse 5: a single-chapter book has no chapter to name.
        if (book.chapters === 1 && m[3] && !m[4]) { verse = chapter; chapter = 1; }
        chapter = Math.min(Math.max(chapter, 1), book.chapters);
        return { book: book.id, chapter: String(chapter), verse };
    }

    const gotoHead = () => doc.querySelector('.chapter-head');
    const gotoInput = () => doc.querySelector('.chapter-head .goto-input');

    /** While Tab cycles through the matches for what was typed: { list, i }. */
    let cycle = null;

    function openGoto() {
        const head = gotoHead();
        const input = gotoInput();
        if (!head || !input) return;
        head.classList.add('editing');
        input.closest('.goto-box').hidden = false;
        input.value = head.querySelector('.goto').textContent.trim();
        input.removeAttribute('aria-invalid');
        cycle = null;
        input.focus();
        input.select(); // typing replaces the current reference
        updateGhost(input);
    }

    function closeGoto(refocus = false) {
        const head = gotoHead();
        if (!head?.classList.contains('editing')) return;
        head.classList.remove('editing');
        head.querySelector('.goto-box').hidden = true;
        cycle = null;
        if (refocus) head.querySelector('.goto')?.focus();
    }

    /** The book being typed, while the cursor is at the end and no chapter has been started. */
    function typedBook(input) {
        const v = input.value;
        const atEnd = input.selectionStart === v.length && input.selectionEnd === v.length;
        return atEnd && /\S/.test(v) && BOOK_ONLY.test(v) ? v : null;
    }

    /** The best match's name, when what was typed is its beginning: shown as the ghost. */
    function ghostFor(input) {
        const typed = typedBook(input)?.normalize('NFC');
        const best = typed && candidates(keyOf(typed))[0];
        if (!best || best.name.length <= typed.length || !fold(best.name).startsWith(fold(typed))) return null;
        return best;
    }

    function updateGhost(input) {
        const ghost = input.parentElement.querySelector('.goto-ghost');
        const best = ghostFor(input);
        if (!best || input.scrollWidth > input.clientWidth) { ghost.replaceChildren(); return; }
        const typed = doc.createElement('span');
        typed.className = 'ghost-typed';
        typed.textContent = input.value;
        const rest = doc.createElement('span');
        rest.className = 'ghost-rest';
        rest.textContent = best.name.slice(input.value.normalize('NFC').length);
        ghost.replaceChildren(typed, rest);
    }

    function complete(input, book) {
        input.value = `${book.name} `;
        input.setSelectionRange(input.value.length, input.value.length);
        input.removeAttribute('aria-invalid');
        updateGhost(input);
    }

    /** A verse typed on a wide screen, to open in the pane once its chapter is on screen. Only
        the input sets it: citations and shared #v links land on verses without opening the pane. */
    let typedVerse = null;

    function openTypedVerse() {
        const pending = typedVerse;
        typedVerse = null;
        const reader = page();
        if (!pending || !reader || narrow.matches) return;
        const { book, chapter, tr } = reader.dataset;
        if (pending.page !== `${book}/${chapter}/${tr}`) return;
        const number = doc.querySelector(`.scripture .vn[data-v="${pending.verse}"]`);
        if (number) selectVerse(number, true, true);
    }

    function submitGoto(input) {
        const ref = parseReference(input.value);
        if (!ref) { input.setAttribute('aria-invalid', 'true'); return; }

        const reader = page();
        const { tr } = reader.dataset;
        const here = ref.book === reader.dataset.book && ref.chapter === reader.dataset.chapter;
        closeGoto();
        if (here && !ref.verse) {
            doc.getElementById('text-pane')?.scrollTo({ top: 0, behavior: reducedMotion.matches ? 'auto' : 'smooth' });
            return;
        }

        // On a phone the pane would cover the verse just asked for, so there it stays closed.
        typedVerse = ref.verse && !narrow.matches
            ? { page: `${ref.book}/${ref.chapter}/${tr}`, verse: ref.verse }
            : null;

        // Followed as a link, so it takes the same path as any citation: enhanced navigation
        // to another chapter, or the in-page flash for a verse in this one.
        const link = doc.createElement('a');
        link.href = `${ref.book}/${ref.chapter}/${tr}${ref.verse ? `#v${ref.verse}` : ''}`;
        link.hidden = true;
        reader.append(link);
        link.click();
        link.remove();

        // Another chapter opens the pane when it arrives (onPage); this one can do it now.
        if (here) openTypedVerse();
    }

    function onGotoKey(e, input) {
        if (e.key !== 'Tab' && e.key !== 'Shift') cycle = null;

        switch (e.key) {
            case 'Enter':
                e.preventDefault();
                submitGoto(input);
                return;
            case 'Escape':
                e.preventDefault();
                closeGoto(true);
                return;
            case 'ArrowRight':
            case 'End': {
                const best = ghostFor(input);
                if (best) { e.preventDefault(); complete(input, best); }
                return;
            }
            case 'Tab': {
                if (e.altKey || e.ctrlKey || e.metaKey) return;
                if (!cycle) {
                    if (e.shiftKey) return; // leave focus movement alone unless already cycling
                    const typed = typedBook(input);
                    const list = typed ? candidates(keyOf(typed)) : [];
                    if (!list.length) return;
                    cycle = { list, i: -1 };
                }
                e.preventDefault();
                const n = cycle.list.length;
                cycle.i = (cycle.i + (e.shiftKey ? -1 : 1) + n) % n;
                complete(input, cycle.list[cycle.i]);
            }
        }
    }

    // ---- Book nav ---------------------------------------------------------------------

    function buildChapters(item) {
        const template = doc.getElementById('nav').dataset.url;
        const grid = doc.createElement('div');
        grid.className = 'chapters';
        const count = Number(item.dataset.chapters);
        for (let c = 1; c <= count; c++) {
            const a = doc.createElement('a');
            a.href = template.replace('__B__', item.dataset.book).replace('__C__', c);
            a.textContent = c;
            grid.append(a);
        }
        return grid;
    }

    function openBook(item) {
        item.classList.add('open');
        item.querySelector('.book-name').setAttribute('aria-expanded', 'true');
        if (!item.querySelector('.chapters')) item.append(buildChapters(item));
        markChapter();
    }

    function closeBook(item) {
        item.classList.remove('open');
        item.querySelector('.book-name').setAttribute('aria-expanded', 'false');
        item.querySelector('.chapters')?.remove();
    }

    function markChapter() {
        const reader = page();
        if (!reader) return;
        doc.querySelectorAll('#nav .chapters a').forEach(a => {
            const item = a.closest('.book');
            const on = item.dataset.book === reader.dataset.book && a.textContent === reader.dataset.chapter;
            a.classList.toggle('on', on);
            if (on) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
        });
    }

    // The nav is kept across enhanced navigation, so bring it in line with the new page.
    function syncNav() {
        const reader = page();
        const nav = doc.getElementById('nav');
        if (!reader || !nav) return;
        nav.querySelectorAll('.book').forEach(item => {
            if (item.dataset.book === reader.dataset.book) openBook(item);
            else closeBook(item);
        });
        const current = nav.querySelector('.book.open');
        if (current) {
            const list = nav.querySelector('.books');
            const top = current.offsetTop - list.offsetTop;
            if (top < list.scrollTop || top > list.scrollTop + list.clientHeight - 80) list.scrollTop = top - 8;
        }
    }

    function setNavOpen(open) {
        doc.getElementById('nav')?.classList.toggle('nav-open', open);
    }

    // ---- Deep links: #v22-24,26 -------------------------------------------------------

    function versesFromHash() {
        const m = /^#v([\d,-]+)$/.exec(location.hash);
        if (!m) return [];
        const numbers = new Set();
        for (const part of m[1].split(',')) {
            const [a, b = a] = part.split('-').map(Number);
            for (let n = a; n <= b && n - a < 200; n++) numbers.add(n);
        }
        return [...numbers].map(n => doc.getElementById(`v${n}`)).filter(Boolean);
    }

    /** The headings directly above a verse that opens its block, so a linked verse lands
        with its section heading in view; otherwise the verse itself. */
    function leadIn(verse) {
        let block = verse.parentElement;
        if (!block?.classList.contains('ln') || block.firstElementChild !== verse) return verse;
        // A line that opens a poem: the headings are the poem's siblings.
        const poem = block.parentElement;
        if (poem?.classList.contains('poem') && poem.firstElementChild === block) block = poem;
        let top = block;
        for (let h = block.previousElementSibling; h?.classList.contains('sh'); h = h.previousElementSibling) top = h;
        return top;
    }

    function showHashVerses() {
        const verses = versesFromHash();
        const pane = doc.getElementById('text-pane');
        if (!verses.length || !pane) return false;

        const head = pane.querySelector('.chapter-head');
        const top = leadIn(verses[0]).getBoundingClientRect().top - pane.getBoundingClientRect().top
            + pane.scrollTop - (head ? head.offsetHeight : 0) - 16;
        pane.scrollTo({ top, behavior: reducedMotion.matches ? 'auto' : 'smooth' });

        doc.querySelectorAll('.verse.flash').forEach(el => el.classList.remove('flash'));
        void pane.offsetWidth; // restart the animation when the same verses are linked again
        verses.flatMap(pieces).forEach(el => el.classList.add('flash'));
        return true;
    }

    // ---- Theme ------------------------------------------------------------------------

    function applyStoredTheme() {
        try {
            const t = localStorage.getItem('cr-theme');
            if (t) doc.documentElement.dataset.theme = t;
        } catch { /* storage unavailable: follow the system */ }
    }

    function toggleTheme() {
        const root = doc.documentElement;
        const current = root.dataset.theme ?? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
        root.dataset.theme = current === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem('cr-theme', root.dataset.theme); } catch { /* not persisted */ }
    }

    // ---- Page lifecycle ---------------------------------------------------------------

    function onPage(initial) {
        const reader = page();
        inFlight?.abort(); // a late answer for the previous chapter must not land here
        applyStoredTheme();
        if (!reader) return;

        const { book, chapter, tr } = reader.dataset;
        const path = new URL(doc.baseURI).pathname;
        doc.cookie = `cr-last=${book}/${chapter}/${tr}; path=${path}; max-age=31536000; samesite=lax`;

        syncNav();
        setNavOpen(false);
        selection = null;

        // The same chapter arriving again is a return, not a visit: Blazor re-renders the page
        // when Back leaves the pane's own history entry, since both share one address.
        const key = `${book}/${chapter}/${tr}`;
        const samePage = !initial && key === shownPage;
        shownPage = key;

        const revisit = restoring || samePage || (initial && ['back_forward', 'reload']
            .includes(performance.getEntriesByType('navigation')[0]?.type));
        restoring = false;
        const state = history.state?.cr;
        if (revisit && state) {
            restore(state);
            return;
        }
        if (samePage) return; // nothing saved: leave the reader where they are

        if (!showHashVerses() && !initial) {
            const pane = doc.getElementById('text-pane');
            if (pane) pane.scrollTop = 0;
        }
        openTypedVerse();
    }

    const activate = target => {
        const segment = target.closest('.scripture .a');
        if (segment) { selectAnchor(segment); return true; }
        const number = target.closest('.scripture .vn');
        if (number) { selectVerse(number); return true; }
        return false;
    };

    doc.addEventListener('click', e => {
        const t = e.target;
        if (!(t instanceof Element)) return;

        if (activate(t)) return;

        if (t.closest('.chapter-head .goto')) { openGoto(); return; }
        if (t.closest('.goto-ghost .ghost-rest')) {
            const input = gotoInput();
            const best = input && ghostFor(input);
            if (best) complete(input, best);
            input?.focus();
            return;
        }

        const more = t.closest('.pane-body .more');
        if (more) { more.closest('.rt').classList.toggle('open'); return; }

        const row = t.closest('.pane-body .cited-row');
        if (row) { toggleCited(row); return; }

        if (t.closest('.pane-close')) { closePane(); return; }
        if (t.closest('.nav-toggle')) { setNavOpen(!doc.getElementById('nav').classList.contains('nav-open')); return; }
        if (t.closest('.theme-toggle')) { toggleTheme(); return; }

        const bookName = t.closest('#nav .book-name');
        if (bookName) {
            const item = bookName.closest('.book');
            if (item.classList.contains('open')) closeBook(item);
            else {
                doc.querySelectorAll('#nav .book.open').forEach(closeBook);
                openBook(item);
            }
            return;
        }

        const nav = doc.getElementById('nav');
        if (nav?.classList.contains('nav-open') && !nav.contains(t)) setNavOpen(false);
    });

    // Anchors and verse numbers are role="button": Enter and Space activate them.
    // The reference input handles its own keys; "/" opens it from anywhere else.
    doc.addEventListener('keydown', e => {
        const input = e.target instanceof Element ? e.target.closest('.goto-input') : null;
        if (input) { onGotoKey(e, input); return; }

        const typing = e.target instanceof Element && e.target.closest('input, select, textarea, [contenteditable]');
        if (e.key === '/' && !typing && !e.ctrlKey && !e.metaKey && !e.altKey && gotoInput()) {
            e.preventDefault();
            openGoto();
            return;
        }

        // Left and right arrows step a chapter, as the arrow buttons in the header do.
        if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && !typing
            && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
            if (e.repeat) { e.preventDefault(); return; } // holding the key: one chapter, not a run of them
            const step = doc.querySelector(`a.step[rel="${e.key === 'ArrowLeft' ? 'prev' : 'next'}"]`);
            if (step) { e.preventDefault(); step.click(); }
            return;
        }

        if (e.key === 'Escape') {
            closePane();
            setNavOpen(false);
            return;
        }
        if ((e.key === 'Enter' || e.key === ' ') && e.target instanceof Element && activate(e.target)) {
            e.preventDefault();
        }
    });

    doc.addEventListener('input', e => {
        if (!(e.target instanceof Element) || !e.target.matches('.goto-input')) return;
        cycle = null;
        e.target.removeAttribute('aria-invalid');
        updateGhost(e.target);
    });

    // The ghost only shows with the cursor at the end, so follow the cursor as well.
    ['keyup', 'click'].forEach(type => doc.addEventListener(type, e => {
        if (e.target instanceof Element && e.target.matches('.goto-input')) updateGhost(e.target);
    }));

    // Tapping the ghost must not take focus from the input, or a phone drops its keyboard.
    doc.addEventListener('pointerdown', e => {
        if (e.target instanceof Element && e.target.closest('.goto-ghost .ghost-rest')) e.preventDefault();
    });

    doc.addEventListener('focusout', e => {
        if (!(e.target instanceof Element) || !e.target.matches('.goto-input')) return;
        if (!e.target.closest('.goto-box').contains(e.relatedTarget)) closeGoto();
    });

    // Changing translation is a full load: the nav is kept across enhanced navigation,
    // and it is the one thing that must be rebuilt in the new language.
    doc.addEventListener('change', e => {
        const select = e.target;
        if (!(select instanceof HTMLSelectElement) || !select.classList.contains('tr-select')) return;
        const { book, chapter } = page().dataset;
        location.href = resolve(select.dataset.url
            .replace('__B__', book).replace('__C__', chapter).replace('__T__', select.value));
    });

    // Citations into the chapter on screen are handled here, in the capture phase on window,
    // before Blazor's own listener on document intercepts them. The verses flash even when the
    // hash is unchanged, and on a phone the pane moves aside so the flash is visible.
    window.addEventListener('click', e => {
        const link = e.target instanceof Element ? e.target.closest('a[href]') : null;
        if (!link || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || link.target) return;

        const url = new URL(link.href);
        const target = chapterOf(url);
        if (!target) return;

        // Record exactly where the reader is before this entry is left behind.
        saveState();
        if (!isShown(target) || !url.hash) return;

        e.preventDefault();
        e.stopPropagation();
        if (url.hash !== location.hash) history.pushState(null, '', url.href);
        page()?.classList.remove('pane-open');
        showHashVerses();
    }, true);

    // Back from the pane's own entry on a phone: close the pane and nothing else. This runs in
    // the capture phase, before Blazor's popstate listener and the one below, and stops them.
    window.addEventListener('popstate', e => {
        const reader = page();
        if (!narrow.matches || !reader?.classList.contains('pane-open') || history.state?.crPane) return;
        if (!isShown(chapterOf(new URL(location.href)))) return;
        e.stopImmediatePropagation();
        reader.classList.remove('pane-open');
        saveSoon();
    }, true);

    // Back and forward. To another chapter: Blazor loads it, and onPage restores the entry's
    // state when it arrives. Within this chapter (verse links pushed their own entries):
    // restore the entry's state, or flash its verses if it has none yet.
    window.addEventListener('popstate', () => {
        if (!page()) return;
        if (!isShown(chapterOf(new URL(location.href)))) { restoring = true; return; }
        const state = history.state?.cr;
        if (state) restore(state); else showHashVerses();
    });

    // Scrolling either pane updates the entry; scroll events don't bubble, so listen in capture.
    doc.addEventListener('scroll', e => {
        const id = e.target instanceof Element ? e.target.id : '';
        if (id === 'text-pane' || id === 'pane-body') saveSoon();
    }, true);
    window.addEventListener('pagehide', saveState);

    window.addEventListener('hashchange', showHashVerses);

    if (doc.readyState === 'loading') doc.addEventListener('DOMContentLoaded', () => onPage(true));
    else onPage(true);

    window.Blazor?.addEventListener('enhancedload', () => onPage(false));
})();
