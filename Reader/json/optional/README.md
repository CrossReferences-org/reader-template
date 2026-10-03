# Optional data

## S21 (Segond 21)

The S21 text is © Société Biblique de Genève and is not part of the public dataset.
If you hold the text, place it here as `s21_verses.json` and the reader offers S21 on startup.
Without it, the reader runs normally with the other translations.

The file is an array with one record per verse record in `bible_verses.json`, joined on `id`:

```json
[
  { "id": 10, "s21_ch": 1, "s21_vs": 1, "s21_sort": 1, "s21_text": "Au commencement, Dieu créa le ciel et la terre." }
]
```

Records missing from the file simply don't appear in S21. This folder's `*.json` is ignored by git.
