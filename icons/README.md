# Icons

**Tabler Icons** v3.49.0 (https://tabler.io/icons) — MIT License, see `LICENSE.txt` in this folder.
Downloaded from the `@tabler/icons-webfont` npm package (via cdn.jsdelivr.net).

- `tabler-icons-300.ttf` — the outline set at the lighter 1.5 px stroke weight (rounded caps and joins).
- `tabler-icons-filled.ttf` — the filled set, used for the transport buttons (play, pause, stop, previous, next).
  Its font family name was changed from `tabler-icons` to `tabler-fills` (same length, patched in the `name` table)
  so both fonts can be loaded side by side; the glyphs are unchanged.

`build.ps1` embeds both into `SoDaPlayer.exe`; the code points used are listed in `Glyphs` (src/UiKit.cs).
