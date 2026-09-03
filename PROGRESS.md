# CoursePlayer — Project Journal

An offline Windows desktop app that turns local course folders into a
YouTube-Premium-style learning experience. Single user, local only: no login, no
cloud, no server, no telemetry.

This file is the durable memory for the project. Update it at the end of every
phase and whenever a hard-won environment fact is discovered.

---

## Status

| Phase | Scope | State |
|-------|-------|-------|
| 1 | Foundation — project skeleton, DI, database, theming, shell window | Complete |
| 2 | Ingestion — folder scan, import wizard, metadata probe | Complete |
| 3 | Home & Navigation — course grid, course detail, navigation service | Complete |
| 4 | Video Player — full YouTube-style player chrome | Complete (one known upstream crash, see below) |
| 5 | Thumbnails — cover art for courses, lessons, and playlist rows | Complete |
| — | Post-Phase-5 fixes — card clipping, default theme, title-bar colour | Complete |
| 6 | Documents (PDF/DOCX viewers) | Not started |
| 7 | Polish | Not started |

Working agreement: build one phase at a time and stop for the user's explicit
"continue" before starting the next.

---

## Architecture

- .NET 9.0-windows, WPF, C# 13, MVVM.
- `Platforms x64` / `PlatformTarget x64` / `RuntimeIdentifier win-x64`,
  `SelfContained false`. The x64 pin exists because the FFmpeg shared binaries
  are 64-bit.
- `CommunityToolkit.Mvvm` source generators for observable properties and
  commands.
- `Microsoft.Extensions.DependencyInjection` composed in `App.xaml.cs`.
- EF Core 9 + SQLite for the local database; Serilog file sink for logs.
- `MaterialDesignThemes` + `MahApps.Metro` for the shell chrome.
- `FFME.Windows` (`Unosquare.FFME.MediaElement`) for video playback.

### Layout

```
CoursePlayer/
  App.xaml.cs           DI container, Serilog setup, global exception handlers
  Controls/             reusable custom controls
  Converters/           value converters (BoolToVisibility, PathToImage, ...)
  Data/                 CoursePlayerDbContext + Migrations
  Resources/            Styles.xaml, brushes, theme resources
  Services/             navigation, playback, database, theming, ingestion,
                        thumbnails
  ViewModels/           one view model per view
  Views/                MainWindow + page views
```

---

## Environment facts

These were established by probing at runtime and cost real time to discover.
Do not re-derive them.

### FFmpeg / FFME

- `FFME.Windows 4.4.350` binds FFmpeg **4.4** sonames (`avcodec-58` and
  friends). A newer FFmpeg will not load.
- `C:\ffmpeg\x64` holds FFmpeg 4.4.1 x64 shared DLLs, including
  `postproc-55.dll`, **plus the `ffmpeg.exe`, `ffplay.exe` and `ffprobe.exe`
  command-line tools** — which is what makes shelling out for thumbnail frames
  possible. Override the location with the `COURSEPLAYER_FFMPEG_DIR`
  environment variable.
- Upgrading FFME to a build that binds `avcodec-63` was considered and
  explicitly rejected.
- The `MediaElement` renders only while it is parented in the live visual tree.
  A single shared instance is owned by `AssetPlaybackService` and re-parented
  into whichever view needs it.

FFME API surface actually confirmed by reflection:

- Settable: `Position`, `Volume`, `SpeedRatio`, `IsMuted`, `ScrubbingEnabled`,
  `LoadedBehavior` / `UnloadedBehavior` (`MediaPlaybackState`), `Stretch`,
  `VerticalSyncEnabled`.
- Read-only: `NaturalDuration` (`TimeSpan?`), `IsSeekable`, `HasSubtitles`,
  `HasVideo`, `NaturalVideoWidth` / `NaturalVideoHeight`, `BufferingProgress`,
  `CanPause`, `IsOpen`, `IsPlaying`, `RendererOptions`.
- Methods returning awaitables: `Open(Uri)`, `Play()`, `Pause()`, `Stop()`,
  `Close()`, `Seek(TimeSpan)`.
- Events used: `MediaOpening` (set `e.Options.SubtitlesSource`,
  `e.Options.IsAudioDisabled`, `e.Options.IsVideoDisabled`), `MediaOpened`,
  `MediaEnded` (plain `EventHandler`), `MediaFailed` (`e.ErrorException`),
  `RenderingSubtitles` (`e.Cancel = true` hides subtitles live),
  `MessageLogged` (mirrors the FFME engine log — invaluable for diagnosis).
- `RendererOptions`: `UseLegacyAudioOut`, `AudioDisableSync`, `VideoImageType`
  (`WriteableBitmap` | `InteropBitmap`), `VideoRefreshRateLimit`.
- `Open()` closes any current media itself. Do **not** queue an explicit
  `Close()` first — that puts two commands in flight on one container and FFME
  logs `Direct Command 'Close' not accepted`.

### MaterialDesignThemes 5.3.2

Style keys that exist: `MaterialDesignHeadline6TextBlock`,
`MaterialDesignBody1TextBlock`, `MaterialDesignBody2TextBlock`,
`MaterialDesignIconButton`, `MaterialDesignFlatButton`,
`MaterialDesignRaisedButton`, `MaterialDesignOutlinedButton`,
`MaterialDesignCircularProgressBar`, `MaterialDesignToolButton`,
`MaterialDesignIconForegroundButton`.

Style keys that do **not** exist: `MaterialDesignSlider`,
`MaterialDesignDiscreteSlider`. Slider styling is hand-rolled in
`Resources/Styles.xaml`.

`PackIconKind` names verified present: `AlertCircleOutline`, `ArrowLeft`,
`Rewind10`, `FastForward10`, `SkipPrevious`, `SkipNext`, `PlaylistPlay`,
`ChevronRight`, `ClosedCaption`, `ClosedCaptionOutline`, `Fullscreen`,
`FullscreenExit`, `CheckCircle`, `Play`, `Pause`, `VolumeHigh`, `VolumeMedium`,
`VolumeOff`, `PlayCircleOutline`, `FilePdfBox`, `FileWordBox`,
`FileDocumentOutline`, `FileOutline`, `FolderMultipleOutline`, `HeartOutline`.

`Replay10` and `Forward10` do **not** exist — use `Rewind10` / `FastForward10`.

### PdfiumViewer.Net.WPF 3.0.4 (+ bblanchon.PDFium.Win32 154.0.8021)

The API differs from the older `PdfiumViewer` package that most samples use.
Confirmed by reflection — there is no `PdfiumViewer.PdfDocument`, no
`document.PageSizes`, and no `document.Render(...)`:

- `PdfiumViewer.Core.PdfDocument.Load(string path)` / `.Load(Stream)`.
  Properties: `PageCount`, `Pages` (`IReadOnlyList<PdfPage>`), `Bookmarks`,
  `Selections`.
- `PdfiumViewer.Core.PdfPage`: `double Width`, `double Height`, `Size Size`, and
  `Image Render(int width, int height, float dpiX, float dpiY, PdfRotation rotate, PdfRenderFlags flags)`.
- `PdfRenderFlags` lives in `PdfiumViewer.Enums`, but **`PdfRotation` lives in
  the root `PdfiumViewer` namespace** — write it as
  `PdfiumViewer.PdfRotation.Rotate0` or the compiler reports `CS0103`.
- `System.Drawing` (`Bitmap`, `Graphics`, `InterpolationMode`, `ImageFormat`) is
  available in this WPF app and is used for letterboxing and JPEG encoding.

### App resource keys

- Brushes: `App.Brush.{Accent, AccentDim, Background, Divider, Hover, Surface,
  SurfaceRaised, TextDisabled, TextPrimary, TextSecondary, Transparent,
  Warning}`.
- Text styles: `App.Text.{Body, Caption, PageTitle, Subtitle2}`.
- Other styles: `App.Sidebar.NavItem`, `App.DropZone.Border`,
  `App.Player.{Scrubber, VolumeSlider, IconButton, PlaylistItem}`.
- Converters: `BoolToVisibility` (accepts `ConverterParameter=Invert`),
  `InverseBoolean`, `NullOrEmptyToVisibility` (also accepts `Invert`),
  `PathToImage`.

### Database

- Location: `%LOCALAPPDATA%\CoursePlayer\courseplayer.db`.
- Tables: `Courses`, `Assets`, `Progresses`. The `DbSet` is `Progresses`, not
  `Progress` — querying `Progress` fails with `no such table`.
- `Assets` columns: `Id`, `CourseId`, `Title`, `FilePath`, `Type`, `OrderIndex`,
  `Duration`, `Codec`, `Resolution`, `IsOnline`, `Section`. There is no `Width`
  or `Height` column; resolution is a single string. There is deliberately no
  `ThumbnailPath` column — asset covers are addressed by id convention.
- `AssetType`: `Unknown = 0, Video = 1, Pdf = 2, Docx = 3, Text = 4`. There is no
  `AssetType.Document`.
- Generated content lives under `%LOCALAPPDATA%\CoursePlayer\Assets\`:
  `Thumbnails\{assetId}\cover.jpg` and `CourseThumbnails\{courseId}.jpg`. Both
  directories are created by `IAppPaths.EnsureCreated()`.

### Test data

Course 1 = "Creator Vault" at
`D:\mega_downloads\Digital_Product_Mastery_Bundle_SLENDERMAN_BBHF\Creator Vault`,
25 assets in one section ("8 videos · 17 documents").

- Asset Id 1 does not exist.
- Videos are Ids 2–9. Id 2 = `1-Watch this first.mp4` (2:11); Id 9 = 4:24.
- Ids 10+ are documents. Id 12 is a PDF that FFME correctly refuses with
  `Error -1094995529: Invalid data found when processing input`.

### PowerShell host quirks (Windows PowerShell 5.1)

- The ternary `? :` operator is unsupported — use `if` / `else`.
- `"$dll: text"` is a parse error; break the variable out or use `-f`.
- Here-string content cannot start on the `@"` header line.
- `Get-ChildItem -Filter` takes exactly one pattern.
- `Add-Type -Path` against the app's own DLLs throws
  `ReflectionTypeLoadException`.
- Not installed: `sqlite3.exe`, `dumpbin.exe`, `vswhere.exe`, `gh`.
  `dotnet-dump` was installed as a global tool during Phase 4.
- Passing a very large here-string in one call fails with `spawn ENAMETOOLONG`.
  Write big files in chunks of roughly 8 KB with
  `[System.IO.File]::WriteAllText` followed by `AppendAllText`.

---

## Build, run, verify

```powershell
# Always stop the running app first: MSBuild cannot overwrite a locked exe
# (MSB3027 / MSB3021).
Get-Process CoursePlayer -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet build "D:\vs_code\CoursePlayer\CoursePlayer.csproj" -c Debug --verbosity quiet

$env:COURSEPLAYER_FFMPEG_DIR = "C:\ffmpeg\x64"
Start-Process "D:\vs_code\CoursePlayer\bin\Debug\net9.0-windows\win-x64\CoursePlayer.exe"
```

WPF also compiles a `*_wpftmp.csproj`, so every XAML or C# error is reported
twice. As of the end of Phase 5 the build is warning-free.

### Verification method

There is no screenshot capability in this environment, so runtime behaviour is
verified two ways:

1. **The log.** `%LOCALAPPDATA%\CoursePlayer\logs\courseplayer-<yyyyMMdd>.log`,
   read from the last `CoursePlayer starting` line to EOF.
2. **UI Automation.** `uia.ps1` (in the repo root) dot-sources a set of helpers:
   `Start-App`, `Get-AppWindow`, `Open-FirstVideo`, `Get-Elements`,
   `Get-Clock`, `Get-Texts`, `Get-BottomBarButtons`, `Invoke-El`,
   `Invoke-Where`, `Get-Sliders`, `Get-SliderValue`, `Set-SliderValue`,
   `Send-Keys`, `Get-LogTail`.

Visual results are confirmed verbally by the user.

---

## Phase 4 — Video Player

### Delivered

- `Views/VideoPlayerView.xaml` — two-column layout: video stage plus a 320 px
  collapsible playlist panel. Gradient top bar (back button, asset title, course
  title, resolution badge) and gradient bottom chrome.
- Custom scrubber: a buffered `ProgressBar` behind a `Slider`, with drag and
  click-to-seek.
- Control row: previous, rewind 10 s, play/pause, forward 10 s, next, mute,
  volume slider, clock, speed selector, subtitles toggle, playlist toggle,
  fullscreen.
- Auto-hide chrome after 3 s of inactivity, revived by mouse movement.
- Keyboard shortcuts: Space, F, M, N, P, C, `[` / `]` for speed, arrow keys for
  seek and volume, Esc to leave fullscreen.
- Progress: `WatchedSeconds` auto-saved every 5 s, resume on reopen,
  auto-complete at 95 %, autoplay the next video.
- `Services/AssetPlaybackService.cs` owns the single shared `MediaElement` and
  exposes it through `IAssetPlaybackService`.
- `ViewModels/VideoPlayerViewModel.cs` switches lessons **in place** via
  `LoadAssetAsync` rather than re-navigating.

### Design decisions worth remembering

- **Lesson switching happens in place.** Re-navigating to the player for each
  lesson caused a runaway autoplay chain (assets 3→9 in about seven seconds),
  because FFME raises `MediaEnded` while a container is being torn down and each
  raise triggered another navigation.
- **`MediaEnded` is only honoured** when the position is within 3 s of the
  duration *and* at least 5 s of playback has elapsed. Guard flags
  `_isSwitchingAsset` and `_isLeaving` suppress re-entrant loads.
- **Autoplay is deferred through the dispatcher** at `Background` priority so
  the next `Open()` never runs inside FFME's own `MediaEnded` callback.
- **The clock must not look like user input.** `_isClockWritingPosition` gates
  the timer's writes to `PositionSeconds`, so `OnPositionSecondsChanged` can
  treat any other change as a real scrub and seek.
- `PlaylistRow` must be `internal`, not `private`: a `private` nested type used
  in a `public` nested type's constructor trips `error CS0051`.
- Never use the `write` tool on `.xaml` or `.xaml.cs` files — it has appended a
  literal `</content>` line. Use
  `[System.IO.File]::WriteAllText(path, $content, (New-Object System.Text.UTF8Encoding($false)))`
  with a single-quoted here-string.

### Verified working

- Player page renders with top bar, scrubber, nine bottom-bar buttons, volume
  slider, clock, speed selector, and the 25-item "Up next" playlist.
- Transport: clock advances; forward and rewind 10 s move it correctly; pause
  holds; resume continues.
- Scrubber seeking by value: 50 % of a 2:11 file lands at 1:09, 20 % lands at
  0:29.
- Volume slider set to 35 reads back 35.
- `Progresses` rows are written with sane `WatchedSeconds`, and `Completed`
  flips at the 95 % threshold.
- Autoplay advances exactly one lesson at the true end of a file.

### Open: intermittent process fail-fast (0xc0000409)

Under **rapid** lesson switching the process dies instantly with no managed
exception and no log entry. WER reports `BEX64`, exception code `c0000409`
(`STACK_BUFFER_OVERRUN` / fail-fast), exception data `0xa`, fault module
`StackHash_ffbe`. Crash dumps land in `%LOCALAPPDATA%\CrashDumps`.

Reproduction (roughly 2 in 2 trials, dying after 7–14 switches): press Next
every 1.5 s, seeking to 60 % before every other switch. See `harsh.ps1`,
`isolate.ps1`, `trials.ps1`, `repro.ps1`.

Ruled out so far:

| Suspect | Test | Result |
|---------|------|--------|
| Audio renderer | `e.Options.IsAudioDisabled = true` | still dies |
| Video renderer | `e.Options.IsVideoDisabled = true` | still dies |
| DirectSound COM teardown | `RendererOptions.UseLegacyAudioOut = true` | still dies |
| FFME vertical-sync P/Invoke | `VerticalSyncEnabled = false` | still dies |
| Double command in flight | removed the explicit `Close()` before `Open()` | fixed a real bug, crash persists |
| Idle playback | 75 s with no interaction | stable |
| Moderate switching | 12 switches at 3 s dwell, twice | stable |
| Seeking | 15 switches at 1.5 s dwell with **no** seeks | still dies (1 of 2) |
| Delegate lifetime | `static readonly object` holders in `AssetPlaybackService` | no effect — reverted |
| CoursePlayer itself | standalone probe + forced compacting `GC.Collect` between opens | **crashes identically** |

**Conclusion: this is an upstream FFME/FFmpeg defect, not a CoursePlayer bug.**
A bare `MediaElement` in `temp_ffme_probe`, with no CoursePlayer code involved,
reproduces the exact WER signature (`P8=c0000409`, `P9=…0a`) once a compacting
`GC.Collect(2, Forced, blocking, compacting: true)` runs between opens. A lighter
probe run survived 25 opens, which is why earlier probe runs looked clean.

The mechanism is consistent with a native callback delegate being relocated or
collected while FFmpeg still holds its function pointer: reflection shows FFME
stores several as plain fields —
`FFInterop.FFmpegLogCallback` (`av_log_set_callback_callback`),
`HardwareAccelerator.<GetFormatCallback>k__BackingField`
(`AVCodecContext_get_format`), `DataComponentSet.<OnDataPacketReceived>`,
`MediaComponentSet.<OnPacketQueueChanged>`, and
`MediaComponent.DecodePacketFunction`. Holding `object` references from our own
code cannot help, because the delegates we would need to pin live inside FFME.

Practical impact is low: the fault needs sub-2-second lesson switching, which no
real user session produces. Normal use has been stable in every test since the
dispatcher-deferred autoplay fix. Fixing it properly means patching or replacing
FFME; upgrading to an `avcodec-63` build was explicitly rejected.

Reproduction: `. D:\vs_code\harsh.ps1; Invoke-HarshTrials -Count 2 -Switches 15 -Dwell 1.5 -Seek`.

---

## Phase 5 — Thumbnails

### Delivered

- `Services/ThumbnailService.cs` — generates 320×180 JPEG covers.
  - Videos: shells out to `ffmpeg.exe` (present in `C:\ffmpeg\x64` alongside the
    shared DLLs) seeking to 10 % of the duration, falling back to 5 s when the
    duration is unknown and retrying with no `-ss` for very short clips.
  - PDFs: renders page 1 through PDFium and letterboxes it onto white.
  - Both are scaled with `force_original_aspect_ratio=decrease` and padded, so a
    cover never stretches.
- `Converters/PathToImageConverter.cs` (`PathToImage`) — loads a frozen
  `BitmapImage` with `BitmapCacheOption.OnLoad` so the file handle is released
  and a future "Clear Cache" can delete the JPEG.
- Home cards gained a 280×157 cover strip; course detail rows gained a 96×54
  cover with a duration badge; the player playlist gained a 72×40 cover. All
  three fall back to the existing type icon when no cover exists.
- `ImportCoordinator` generates covers right after metadata enrichment, so a
  freshly imported course already has artwork.

### Design decisions worth remembering

- **No schema change.** Covers are addressed by convention:
  `Assets\Thumbnails\{assetId}\cover.jpg` and
  `Assets\CourseThumbnails\{courseId}.jpg`. `IAppPaths` has exposed these
  directories since Phase 1, so no EF migration was needed. `Course.ThumbnailPath`
  still exists and wins when the user supplies their own image.
- **Home generates one cover per course, not all of them.**
  `EnsureCourseThumbnailAsync` stops at the first asset that rasterises;
  `GenerateForCourseAsync` (the full sweep) runs only when a course is actually
  opened. Without that split, launching the app rasterised every lesson of every
  course.
- **Generation is serialised through one `SemaphoreSlim`**, always off the UI
  thread, always cancellable, and never blocks page load. A 25-asset course would
  otherwise spawn 25 concurrent `ffmpeg` processes.
- `CourseDetailViewModel` implements `INavigatedFromAware` so leaving the page
  cancels the sweep instead of letting ffmpeg grind on in the background.
- Covers are cached: a restart re-renders nothing (verified by comparing
  `LastWriteTime` across runs).

### Verified working

- Cold cache, Home only: 1 course cover written, 1 asset cover written.
- Opening the course: 22 asset covers written (8 videos + 14 PDFs), all
  320×180. The three assets with no cover are Ids 13, 16 and 20 — plain-text
  files (`Type = 4`), which have nothing to rasterise.
- Asset 12 — the PDF that FFME refuses with `Invalid data found` — renders
  correctly through the PDFium path.
- Luma sampling confirms real imagery rather than blank frames: videos average
  22–40 with a spread of 10–40, PDF pages average ~250 on white with a non-zero
  spread.
- UIA element counts: 1 `Image` on Home, 22 in the detail view, 13 visible
  72×40 covers in the player playlist.
- Restart with a warm cache rewrites 0 files and still renders 22 covers.
- Clean log across the whole flow: no `ERR`, no `FTL`, no non-FFME `WRN`.

---

## Post-Phase-5 fixes (three user observations)

Three defects reported after Phase 5 and fixed before starting Phase 6.

### 1. Broken course thumbnail on Home

Two independent causes, both fixed.

**Layout (the visible break).** The card was a chromeless `Button` whose
`Template` was overridden to a bare `ContentPresenter` — but overriding
`Template` does not escape the rest of the implicit style. MahApps'
`Controls.xaml` supplies an implicit `Style` for `Button` that sets
**`Height = 32`**, so the whole 293-px card was clipped to a 32-px strip: only
the top ~16 rows of the 157-px cover ever painted, and the `materialDesign:Card`
background (`#2F2F34`) never appeared at all. Confirmed by loading the same
dictionary set in a throwaway probe app and walking
`app.TryFindResource(typeof(Button))` → `BasedOn` chain.
Fix: `Style="{x:Null}"` on the card button (plus explicit `Background`,
`BorderThickness`, `Padding`, `VerticalAlignment`), which opts out of the
implicit style entirely. UIA now reports the card button as `296x293` instead of
`296x32`, all 157 cover rows paint, and `#2F2F34` is present.

**Frame selection (the poor cover).** A single seek at 10 % of the duration
landed on a near-black intro on the test course. `ExtractVideoFrameAsync` now
probes the duration with `ffprobe` when the DB has none (every row in the test
data has `NULL` duration), then samples five candidates at
`0.12 / 0.30 / 0.45 / 0.62 / 0.80` of the running time in **one** ffmpeg
invocation (repeat `-ss T -i FILE` per candidate, then `-map N:v -frames:v 1`
per output) and scores them. Five candidates cost the same wall time as one,
because process startup dominates.

Scoring, after two iterations:

- Detail = standard deviation of **live** samples only (luma > 12), measured on a
  32×18 reduction. Letterbox and pillarbox bars are excluded, otherwise the
  bar-to-picture edge alone scores as enormous contrast.
- Coverage = fraction of live samples. A candidate below **0.6** is pushed down
  by 1000. This is the important guard: the first version picked a frame that was
  a narrow bright window pillarboxed on black (coverage 0.34, detail 75) which
  looked like a rendering fault on the card. The runner-up (coverage 0.83,
  detail 32) is a real full-frame shot.
- There is deliberately **no lower bound on mean luma**. A lesson recorded
  against a dark IDE is legitimately dark; penalising that hands the cover to
  whichever frame flashed a white slide.
- A mean above 232 (blown-out white slide) is also pushed down by 1000.
- Every penalty is a subtraction, never a rejection, so a bad set still yields a
  cover — any frame beats no cover.

Candidates are written to a `candidates` subfolder of the asset's own cache
directory and the winner is copied over `cover.jpg`; the `finally` block deletes
the folder (verified: 0 leftover `candidates` directories after a full sweep).

### 2. No default theme on restart

`ThemeService.Initialize()` now resolves the saved id, then a new
`DefaultPaletteId = "graphite"` constant, then the `Graphite` palette object —
so a missing or unknown `settings.json` still lands on a deliberate default
instead of whatever happened to be first. `Resources/Brushes.xaml` was rewritten
to the literal Graphite values so the **first painted frame** already matches the
default palette; a comment marks them as needing to stay in sync with
`ThemeService.Graphite`.

`Brushes.xaml` also gained the two keys that were referenced but never defined:
`App.Color.Primary` / `App.Brush.Primary` (`#FF26262A`) and
`App.Color.Selected` / `App.Brush.Selected` (`#FF323C3C`), used by
`MainWindow.xaml` lines 83 and 146.

### 3. Red title bar with a themed body

MahApps caption colours are **per-window properties**, not resource lookups, so
retinting brushes never reached them. `IThemeService` gained
`ApplyWindowChrome(Window)`, and a private `ApplyChrome(MetroWindow, ThemePalette)`
sets `WindowTitleBrush` / `NonActiveWindowTitleBrush` to the palette's `Chrome`,
`TitleForeground` / `OverrideDefaultWindowCommandsBrush` to `TextPrimary`, and
`GlowBrush` / `NonActiveGlowBrush` to `Divider`. `ApplyPalette` loops
`app.Windows` so a live theme switch repaints open windows;
`App.OnStartup` calls it once before `window.Show()`, and
`ImportWizardService` (now an instance service with `IThemeService` injected)
calls it before `ShowDialog()`.

`App.xaml` also moved from `Dark.Red.xaml` to `Dark.Steel.xaml` and
`BundledTheme BaseTheme="Dark" PrimaryColor="BlueGrey" SecondaryColor="Teal"`, so
even the pre-`Initialize()` frame is not red.

`ThemePalette` gained computed `Chrome => Surface` and
`Selected => Mix(SurfaceRaised, Accent, 0.35)`.

**Design decision:** the title bar is deliberately the chrome colour (= `Surface`),
not the accent. A coloured caption band made the window look like a different app
from its body; the accent belongs on interactive controls.

### Verified working

- Card button UIA rect `296x293`; all 157 cover rows painted; `#2F2F34` present.
- Cold cache: 1 asset cover + 1 course cover on Home; opening the course wrote
  22 covers (8 videos + 14 PDFs) with 0 leftover `candidates` folders. All 8
  96×54 lesson thumbnails paint content in the detail view.
- The selected cover for `1-Watch this first.mp4` moved from mean 22 / detail 10
  (near-black) to mean 19 / coverage 0.81 / detail 13.6 — a real full-frame shot.
- Pixel samples on the live window: title bar left/middle/right and both
  caption-button areas all `#26262A` (Graphite `Surface`); sidebar body and
  content background `#1E1E20` (Graphite `Background`). No red anywhere.
- Settings palette cards render their mini-app previews: `#5E8B7E`, `#4C7267`,
  `#D8A657`, `#2F2F34`, `#26262A`, `#1C2029`, `#0F0F0F` all present.
- Startup log: `Applied theme Graphite.` Build clean, 0 warnings, 0 errors.

### Environment facts learned

- **MahApps `Controls.xaml` sets an implicit `Button` style with `Height = 32`
  and `Padding = 16,4,16,4`.** Any control that hosts tall content inside a
  `Button` needs `Style="{x:Null}"`; overriding only `Template` is not enough.
- Reflecting over `MahApps.Metro.dll` / `MaterialDesignColors.dll` with
  `Assembly.LoadFrom` + `GetTypes()` throws `ReflectionTypeLoadException`.
  Working alternatives: read `lib\netcoreapp3.1\MahApps.Metro.xml` with
  `Select-String`, enumerate BAML names via
  `GetManifestResourceStream("MahApps.Metro.g.resources")` +
  `System.Resources.ResourceReader`, or build a throwaway console app that merges
  the real dictionaries and inspects `TryFindResource` at runtime.
- `BundledTheme SecondaryColor="BlueGrey"` is invalid (`FormatException`);
  secondary requires an accent swatch. A bad value throws inside
  `App.InitializeComponent()`, so the Serilog log shows a *clean* startup —
  diagnose it with `Start-Process -PassThru -RedirectStandardError` and read the
  stderr file.
- ffmpeg's `metadata=print:file=` rejects Windows paths (the drive colon parses
  as a filter separator). For image analysis use
  `-vf scale=WxH -pix_fmt gray -f rawvideo` and compute statistics in the host.

---

## Housekeeping

Deleted at the end of Phase 5: `temp_ffme_probe/`, `temp_mdix_probe/`,
`dump*.txt`, `probe_out*.txt`, `probe_err*.txt`, `cp_stdout.txt`,
`cp_stderr.txt`, `_tmp_cover.jpg`, three stale `.bak` files, and a stray
`Views/CourseDetailViewModel.cs`.

Kept deliberately: `uia.ps1` (the verification harness), the crash-reproduction
scripts, and `temp_sqlite_update/` (the DB inspection and progress-reset helper:
`cd D:\vs_code\temp_sqlite_update; dotnet run --verbosity quiet -- reset`).
