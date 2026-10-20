# Fonts

`Inter-Regular.ttf`, `Inter-Medium.ttf` and `Inter-SemiBold.ttf` are the static TrueType instances of
**Inter** (weights 400 / 500 / 600) as served by Google Fonts (fonts.gstatic.com, Inter v20).

- Designer: Rasmus Andersson — https://rsms.me/inter/
- License: SIL Open Font License 1.1 — https://openfontlicense.org (full text: https://github.com/google/fonts/blob/main/ofl/inter/OFL.txt)

`build.ps1` embeds every `*.ttf` in this folder into `SoDaPlayer.exe` as `BitPerfectPlayer.Fonts.<file name>`,
and `Theme.LoadEmbeddedFonts()` (src/UiKit.cs) registers them at startup, so nothing needs to be installed.
If you redistribute the app, include the OFL text alongside it.
