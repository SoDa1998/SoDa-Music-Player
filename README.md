# SoDa Player for Windows

![SoDa Player](assets/soda-256.png)

A Windows player built on the same idea as USB Audio Player PRO's exclusive / bit-perfect mode.
It takes the DAC away from the Windows mixer and sends the file's samples to the driver unchanged,
at the file's own sample rate and bit depth.
See [ANALYSIS.md](ANALYSIS.md) for how UAPP does this on Android and how it maps to Windows.

## Features
- **WASAPI Exclusive, event-driven output.** It bypasses the Windows audio engine: no resampling, no mixing, no system sounds.
- **Automatic format negotiation**, like UAPP choosing a USB alt-setting:
  - It tries the file's exact rate, channel count and bit depth first.
  - It then tries lossless zero-padded containers (16 → 24 → 24-in-32 → 32-bit).
  - Only after that does it fall back to another rate or depth.
- A **BIT-PERFECT** badge appears only when the output really is bit-perfect. The *Path* line lists every change the app made to the audio.
- **Gapless playback.** When the next track has a compatible format, it streams through the same exclusive stream. On a format change (for example 44.1 → 96 kHz) the device is re-initialised automatically.
- **Volume modes:**
  - *DAC / device volume*: the hardware endpoint volume, so the stream stays bit-perfect.
    - The slider follows changes made outside the app (the DAC's knob, keyboard volume keys, the Windows mixer). Starting a song never pushes an older level back to the device.
    - For 3 seconds after a stream opens, a sudden change is treated as the DAC resetting its volume on a sample-rate switch, and the app puts the level back.
  - *Software volume*: 32-bit float gain with TPDF dither. It is exactly unity at 100%.
- **20-band equalizer (Shared mode only).** It has its own sidebar page:
  - **Bands:** log-spaced from 25 Hz to 20 kHz, about half an octave apart, ±12 dB each, in 0.5 dB steps. Double-click a band to reset it.
  - **Controls:** a preamp, 12 presets (Flat, Bass boost, Rock, Vocal, …) and a live frequency-response curve.
  - **Matched design:** the filter gains are solved so the level at each band's centre equals its slider, despite the overlap between neighbouring bands.
  - **Processing:** 64-bit filters on the decoded float audio, using about 0.5% of one CPU core.
  - **Exclusive mode:** the engine never creates the EQ stage, so the stream stays bit-perfect. The page is locked there, and offers a **Switch to Shared mode** button.
  - **Saving:** EQ settings are saved, exported and imported with the rest of your settings.
- **Native DSD (DoP).** Settings → Audio output → DSD playback → *Native (DoP)*. It is off by default, and it works in Exclusive mode only.
  - DSF and uncompressed DFF files are read directly (no FFmpeg). The 1-bit stream is sent as DoP: DSD64 at 176.4 kHz, DSD128 at 352.8 kHz, in 24-bit frames with the 0x05 / 0xFA markers.
  - Pauses send DSD silence, and the markers never break across pauses, seeks or gapless track changes.
  - If the DAC refuses the DoP format, SoDa Player converts to PCM and says so in the signal path. Your DAC must support DoP; one that doesn't plays it as noise.
  - Software volume can't scale DSD, so use the DAC's volume. DST-compressed DFF files are converted to PCM.
- **Signal path:** click the status chip (BIT-PERFECT / EXCLUSIVE / SHARED) on Now Playing. It shows every stage from the file to the DAC: source, decoder, processing, volume, output stream, Windows audio engine and device. Each stage is marked green (untouched) or orange (changes the samples), with a verdict at the end.
- **Spectrum visualizer:** 24 log-spaced bars from 40 Hz to 16 kHz with falling peak caps, under "Up next" when there's room. It reads a copy of the decoded audio and never touches the output. It rests during native DSD playback.
- **Sleep timer:** "…" on Now Playing → Sleep timer. Choose 15 min to 2 h, *End of this song*, or *Custom…* (minutes or h:mm; the last custom time is remembered). A moon chip counts down; click it to change or cancel the timer. The volume fades out over the last 12 s, then playback pauses.
- **Folders page:** browse your music folders as they are on disk. Each folder shows its sub-folders above the songs inside it, with Up / Play / Shuffle buttons (Backspace goes up). Right-click a folder to play, shuffle, queue, add to a playlist or open it in Explorer.
- **Shared mode** fallback, selectable output device, buffer size from 5 to 200 ms, and an MMCSS "Pro Audio" thread priority.
- **Every format FFmpeg can decode:** FLAC, WAV/W64/RF64, AIFF, ALAC, MP3, AAC/M4A, M4V/MP4/MOV/MKV audio tracks, Ogg Vorbis, Opus, WMA, APE, WavPack, TTA, DSF/DFF (played as PCM), AC-3/DTS and more.
- **Modern window:** borderless with rounded corners (Windows 11), adjustable window opacity (75–100%, default 97%), and translucent rounded buttons. It keeps native resizing, snapping, minimize and maximize.
- **Inter typeface** (Google Fonts, SIL OFL) in Regular, Medium and SemiBold, embedded in the exe. Nothing needs to be installed, and the app falls back to Segoe UI if loading fails. See [fonts/README.md](fonts/README.md).
- **Tabler Icons** (MIT): rounded outline icons, with solid transport buttons, embedded in the exe. See [icons/README.md](icons/README.md).
- **Accent colour:** Theme default, nine presets (Blue, Purple, Pink, Red, Orange, Amber, Green, Teal, Graphite) or any custom colour. It is adjusted automatically to stay readable in light and dark themes.
- **Themes:** Dark, Light, System (follows Windows' light/dark setting and accent colour, live), or Custom (pick any colour; it becomes the accent and tints the surfaces).
- **Frosted glass** (Settings → Appearance → *Frosted glass*): the whole window shows a blurred picture, with translucent, softly lit panels on top (sidebar, mini player, settings cards).
  - **Background:** the cover of the song that is playing, or your desktop wallpaper (*Glass background*). With nothing to show it falls back to a soft wash of the accent colour.
  - **Transparency:** *Glass transparency* sets how clear the glass is, from frosty and easy to read to clear and vivid. Works with the Dark, Light, System and Custom themes.
  - Secondary text gets a little more contrast on glass, so it stays readable over bright pictures.
- **Sidebar:** drag its right edge to resize it (220–480 px). Collapse it to an icon rail with the header button, the *Compact sidebar* switch, or by dragging it far left. The rail keeps tooltips and a queue button with a count badge.
  - **Smooth motion:** collapsing and expanding glide over about 0.28 s. The icons slide to their place while labels and the logo fade, the selection pill slides to the page you open, the queue section opens and closes smoothly, and dragging the edge follows the display's frame rate.
  - **Why it stays smooth:** the pages beside the sidebar are laid out once, for the width it is heading to, so each animation frame repaints only the sidebar (about 2 ms). The sidebar sits above the pages and uncovers them while it narrows.
- **Playlists:** your own playlists, saved as ordinary `.m3u8` files in `%APPDATA%\SoDaPlayer\playlists` (any other player can open them). Changes are written as you make them.
  - **Page:** the *Playlists* page lists them with a cover, song count and length. Click one to open it. **New playlist** creates an empty one. Right-click a playlist to Play, Shuffle, Add to queue, Rename or Delete it; the page for one also has Rename and Delete buttons. Deleting never touches the songs.
  - **Add songs:** right-click songs, an album, an artist, or the Now Playing artwork and choose **Add to playlist**, then **New playlist…** or an existing one. Songs already in a playlist are skipped.
  - **Save the queue:** right-click the queue header (or a queue row) and choose **Save queue as playlist…**.
  - **Edit:** inside a playlist, right-click songs to **Remove from this playlist**, or a single song to **Move up** or **Move down**.
  - **Songs outside the library:** they are kept in the file and the page says how many aren't in the library; they come back when the songs do.
- **Lyrics:** the lyrics button (or **L**) on Now Playing fades the artwork into a lightly blurred background and shows the lyrics in front. Synced lyrics highlight the current line and follow the song; click a line to jump to it. The app looks in this order:
  1. lyrics embedded in the file;
  2. a matching `.lrc` / `.txt` file next to it;
  3. the local cache;
  4. **LRCLIB** (lrclib.net), a free, open lyrics database with time-synced lyrics and no API key. Last.fm has no lyrics API, and Genius only links to its web pages.

  Lyrics found once are stored on this PC (`%APPDATA%\SoDaPlayer\lyrics`), with their timestamps, for that song's file. Playing the song again loads them from disk, with no download. They stay valid if the file is later edited but its artist, title, album and length are unchanged. When LRCLIB's exact match has only plain text, the app looks for a time-synced version of the same recording (within 2 s). A song that wasn't found is checked again after a week. Online lookup can be turned off in Settings.
- **Search lyrics manually:** choose **Search lyrics…** from the ⋯ menu on Now Playing, or right-click a song. Search LRCLIB by title and artist (leave the artist empty for a free-text search). Results show a SYNCED / PLAIN badge and their length, and the length is green when it matches your song within 2 s, which is the best sign the timing fits. Preview the lyrics, then:
  - *Use in SoDa Player*: kept in the lyrics cache.
  - *Save .lrc file*: written next to the song, and other players read it too.
  - *Embed in file*: written into the song's tags (MP3 USLT, FLAC/Ogg LYRICS, M4A ©lyr). The audio is copied unchanged and checked.
- **Search** on the Albums, Artists and Songs pages. Every word must match, and case and accents are ignored, so "beyonce" finds "Beyoncé".
  - **Songs** match title, artist, album artist, album or file name. **Albums** match title, artist or year. **Artists** match name.
  - **Ctrl+F** jumps to the search box. **Esc** clears it. **Enter** or **↓** moves to the results.
  - **Play all** and **Shuffle** play only the songs that match. In a narrow window the box moves to its own row.
- **Animated album art:** Now Playing loops a moving cover over the static one, whether the music is playing, paused or stopped. The app looks in this order:
  1. a video next to the music named `cover`, `folder`, `front`, `animated`, `animatedcover` or `album` (`.mp4`, `.m4v`, `.mov`, `.webm` or `.mkv`);
  2. the local cache;
  3. **artwork.m8tec.top**, a free API that finds Apple Music's animated artwork by album artist and album title. Apple delivers it as HLS. FFmpeg copies the best H.264 stream into an `.mp4` in the cache without re-encoding (usually 2–15 MB per album). An album with no animated cover is checked again after a week.

  FFmpeg decodes the video at exactly the artwork's on-screen size, and the frames are drawn with GDI. If the album title has an edition suffix such as "(Hi-Res Version)" or "[Deluxe]", the online lookup tries again without it.
- **Choosing animated artwork by hand:** right-click an album, a song or the Now Playing artwork, and choose **Find animated artwork…**. Use it when the automatic match is wrong or missing.
  - **Searching:** the picker lists Apple Music albums for your search and checks each one for animated artwork. You can also paste a `music.apple.com` album link.
  - **Choosing:** click an animated result to download it and preview it in place. A chosen animation wins over a video in the album folder. It can also be saved as `cover.mp4` there.
  - **Turning it off:** **No animation for this album** switches animation off for that album, even with a folder video, until you pick one again.
  - **If a check fails:** a result marked "Couldn't check" is one the service timed out on. Click it to try again.
- **Blurred background:** on Now Playing, the blurred, darkened album art fills the whole window. The sidebar is slightly see-through, so the art shows behind it too. The background is a still image, so it costs nothing per frame.
- **Album art from the internet:** for albums with no embedded picture and no cover image in the folder.
  - **Automatic (Settings → Artwork → *Find missing album art online*):** covers are looked up on the iTunes Search API in the background, one album every 3 seconds at most. A cover is used only when the album title and artist clearly match. Albums with no match are skipped for 30 days.
  - **By hand:** right-click an album, or a song, and choose **Find album art online…**. This searches iTunes and MusicBrainz / Cover Art Archive and shows the covers found. Pick one to use it. It can also be saved as `cover.jpg` in the album folder.
  - **Privacy:** only the artist and album title are sent.
- **Edit metadata:** right-click songs (or an album) and choose **Edit metadata…** to change title, artist, album, album artist, year, track, disc and genre.
  - **Several songs:** fields that differ between them start empty and are kept unless you type something. Title and track number can only be changed one song at a time.
  - **How files are written:** FFmpeg rewrites each file with `-c copy`, so the audio and embedded cover art are copied bit for bit. The result is checked before it replaces the original.
  - **The playing song:** if you edit it, playback stops for the write and then resumes where it was.
  - **Album art:** the editor also shows the embedded album art. **Choose image…** picks a file (JPEG and PNG are embedded as they are; other formats become JPEG). **Find online…** opens the cover picker. **Remove** takes the picture out, and **Keep current** undoes either choice.
    - **Several songs:** the change applies to all of them. Songs that can't hold a picture (Ogg/Opus, WAV) just get their tags.
    - **Afterwards:** the app's cached cover is refreshed. A cover image in the album folder is still shown first, and the editor says so when the folder has one.
  - **Formats:** FLAC, MP3 (written as ID3v2.3), M4A/ALAC/AAC, Ogg and Opus work. DSF/DFF, APE, WavPack and TTA can't be tagged by FFmpeg.
- **Full screen** (button or **F11**, Esc to leave): Now Playing fills the monitor, and the artwork, text and controls scale up (also in large or maximized windows).
- **Multi-select** in song lists: Ctrl+click toggles a song, Shift+click selects a range, Ctrl+A selects all. Right-click to Play / Shuffle selected, Play next, Add to queue, or Remove from library.
- **Saving settings** (Settings → Save & restore):
  - *Save automatically* (on by default) writes changes to `settings.ini` about a second after you make them. Writes are atomic, so a crash can't corrupt the file.
  - *Save* writes immediately and shows when settings were last saved, or that there are unsaved changes.
  - *Export…* and *Import…* move your settings to or from a `.ini` file. Window position isn't included, and an unknown output device falls back to the Windows default.
  - *Reset…* restores the defaults and keeps your library, queue and lyrics cache.
- **Modern Settings and menus:** card-style settings with toggles, segmented buttons, dropdowns and sliders, plus rounded context menus with icons.
- **Now Playing:** large album art over a blurred art backdrop. It has a seek bar and playlist, previous, play/pause, next and stop buttons, plus shuffle, repeat and volume. A chip shows BIT-PERFECT / EXCLUSIVE / SHARED with the source → output format.
- **Library pages:**
  - **Albums:** a grid of album covers. Hover a cover to play it, or click it to open the album page.
  - **Artists:** round artist tiles that open an artist page with all of that artist's songs.
  - **Songs:** every song in the library. Sort by **Name (A–Z / Z–A)** or **Date added (newest / oldest first)**. Right-click a song for Play next, Add to queue, Go to album or artist, Show in File Explorer, or Remove from library.
- **Queue** in the sidebar, with the playing track highlighted. A mini player on every page except Now Playing.
- **Albums are grouped robustly.** Tracks with the same album title are one album when they share an album artist (its first name, so "J Balvin" and "J Balvin, Bad Bunny" match) or an album folder. Disc sub-folders such as `CD1`, `Disc 2` or `[Disc 3] Name` count as one folder. Multi-value artist tags such as "LINKIN PARK;LINKIN PARK" are de-duplicated.
- **Library** of the music folders you add (drag-and-drop works too). Tags and cover art come from the files, or from `cover.jpg` / `folder.jpg`. "Date added" is the time a song entered the library, with file creation time breaking ties. The library, queue and settings persist in `%APPDATA%\SoDaPlayer` (an existing `%APPDATA%\BitPerfectPlayer` folder from before the rename is moved there automatically on first launch).

## Requirements
- Windows 10/11 (64-bit) with .NET Framework 4.8. This is built in, so nothing needs to be installed.
- FFmpeg is **built into** the shareable `SoDaPlayer.exe` (see *Sharing* below). The app looks for `ffmpeg.exe` / `ffprobe.exe` in this order:
  1. next to `SoDaPlayer.exe`, or in an `ffmpeg\` or `ffmpeg\bin\` sub-folder;
  2. the copy bundled inside the exe, unpacked once to `%LOCALAPPDATA%\SoDaPlayer\ffmpeg\<version>`;
  3. `PATH`;
  4. the WinGet links folder.
- For exclusive mode, the device must allow it: **Sound settings → device → Properties → Advanced → "Allow applications to take exclusive control of this device"**. This is on by default.

## Build
```powershell
.\build.ps1             # shareable single file: dist\SoDaPlayer.exe (~74 MB, FFmpeg included)
.\build.ps1 -NoFFmpeg   # small developer build (~4 MB) that uses the FFmpeg installed on this PC
```
This uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`); no SDK or NuGet packages are needed.

The build takes FFmpeg from `PATH`, or from `$env:FFMPEG_DIR`. It compresses `ffmpeg.exe` and `ffprobe.exe` with GZip into `obj\` (only when they change) and embeds them, together with FFmpeg's `LICENSE` and `README.txt`. It also produces `bin\soda-test.exe`, a console test harness.

## Sharing
`dist\SoDaPlayer.exe` is the only file anyone needs. Everything is inside it: the app, fonts, icons, artwork, licences and FFmpeg.
- On first launch it unpacks FFmpeg (~193 MB) to `%LOCALAPPDATA%\SoDaPlayer\ffmpeg\` in about 2 s. Later launches reuse it.
- Settings, library and caches live in `%APPDATA%\SoDaPlayer`. The exe contains none of your data.
- The exe isn't code-signed, so Windows SmartScreen may show "Windows protected your PC" on another PC. *More info → Run anyway* starts it.
- **Licences:** the bundled FFmpeg (gyan.dev essentials build) is licensed under the GNU GPL v3. SoDa Player runs it as a separate program. When you share the exe you are also distributing FFmpeg, so its licence and source pointer must go with it. Both are built in, under **Settings → About → Third-party licences → View**: the full GPL v3 text, the FFmpeg source commit, and the gyan.dev link. Tabler Icons (MIT) and Inter (SIL OFL) notices are included too.
## Usage tips
- Start on **Songs → Add folder** to build your library.
- Under **Settings → Output device**, choose your USB DAC and keep **Mode = Exclusive — bit-perfect**.
- Keep **Volume control = DAC / device volume**. If the device has no hardware volume, choose *Software volume* and use 100% for bit-perfect output.
- While the app plays in exclusive mode, other apps cannot use that device. UAPP behaves the same way.
- Keyboard: Space plays or pauses, Ctrl+O adds files, Ctrl+←/→ goes to the previous or next track, Enter plays the selected song, Ctrl+A selects all songs, L shows lyrics, F11 toggles full screen, and Esc leaves full screen or goes back from an album or artist page.
- Set `BPP_DATA_DIR` to a folder to run with a separate library and settings (handy for testing).

## Self-test harness
```
soda-test devices                                   list output devices (+ ids)
soda-test probe <file>                              show detected format and exclusive candidates
soda-test dump  <file> <out.raw|-> <container> <valid>   run decoder+converter, print MD5 of device-bound bytes
soda-test play  <file> <seconds> [exclusive|shared] [bufferMs] [deviceNameSubstring] [next files...]
soda-test stress <file> <deviceNameSubstring> [shared]   pause/resume (0.3–12 s) and seek cycles; reports any engine error
soda-test eq [preset] [rate]                       test tones through the EQ: measured vs designed gain, flat = untouched, CPU cost
```
Pausing keeps the WASAPI stream running and feeds the DAC digital silence. Some USB Audio drivers (the Fosi K5 Pro among them)
stop signalling after an exclusive-mode `Stop()`/`Start()`, which used to end playback with "the audio device stopped
requesting data". If a stream does stall or the device resets, the engine reopens it at the same position (up to 3 times).
`dump` was used to check bit-exactness against FFmpeg's reference decode. All of these produced identical MD5s:
FLAC 24/96 → 24 and 24-in-32, WAV 24/192 → 24-in-32, WAV 16/44.1 → 16/24/24-in-32, ALAC → 24, and mono FLAC → duplicated stereo.

## Source layout
| File | Purpose |
|------|---------|
| `src/CoreAudio.cs` | COM interop for MMDevice, IAudioClient, IAudioRenderClient and IAudioEndpointVolume |
| `src/Formats.cs` | WAVEFORMATEXTENSIBLE builder, exclusive-format negotiation order, and sample converter (integer pass-through or float+dither) |
| `src/Decoding.cs` | FFmpeg locator, ffprobe metadata, and the streaming decoder (child process → ring buffer) |
| `src/PlaybackEngine.cs` | Render thread: device open and negotiation, event loop, gapless hand-over, re-init on format change |
| `src/Library.cs` | Library model: songs with date added, album and artist grouping, song sort orders, persistence |
| `src/ArtCache.cs` | Album art from folder images or embedded pictures (via FFmpeg), cached on disk and in memory |
| `src/UiKit.cs` | Theme (including loading the embedded Inter fonts), rounded translucent buttons and sliders, scrolling canvas, dark menus |
| `assets/` | App icon: `soda.ico` (16–256 px, embedded as the exe/window icon) and `soda-256.png` (drawn in the sidebar and About) |
| `fonts/` | Inter Regular / Medium / SemiBold TTFs, embedded by `build.ps1` |
| `src/Views.cs` | Sidebar, title bar, Now Playing, mini player, song list, album and artist grids, page headers |
| `src/SettingsView.cs` | Card-style Settings page and its painted widgets (toggle, segmented, dropdown, slider, swatch, folder list) |
| `src/Lyrics.cs` | Lyrics lookup (embedded tags → .lrc → cache → LRCLIB) and the LRC parser |
| `src/AnimatedArt.cs` | Animated cover lookup (cover.mp4 → cache → artwork.m8tec.top / Apple HLS) and the looping frame player (FFmpeg → GDI DIB) |
| `src/OnlineArt.cs` | Missing album art from iTunes (automatic, rate-limited, strict matching) and iTunes + MusicBrainz / Cover Art Archive search for the picker |
| `src/Tags.cs` | Reading tags with ffprobe and writing them with an FFmpeg `-c copy` remux plus a safe file swap |
| `src/Playlists.cs` | Playlist model and store: one `.m3u8` file per playlist, written on every change |
| `src/Dialogs.cs` | Themed dialogs: the album art picker and the metadata editor |
| `icons/` | Tabler Icons fonts (outline + filled), embedded by `build.ps1` |
| `src/Equalizer.cs` | 20-band EQ: settings and presets, matched peaking-biquad design, and the audio-thread processor |
| `src/EqualizerView.cs` | Equalizer page widgets: band sliders with the response curve, and the status badge |
| `src/MainForm.cs` | Borderless window, navigation, queue and transport, library management, settings persistence |

## Known limitations
- Native DSD is DoP only (no ASIO native DSD), and DST-compressed DFF files are converted to PCM.
- No MQA. EQ is Shared-mode only, by design. Exclusive mode stays bit-perfect.
- The window is slightly see-through as a whole (WinForms has no true acrylic blur). The blur behind Now Playing is drawn from the album art.
- If a device accepts no candidate format, the app reports an error and you can switch to Shared mode. It does not switch automatically.
