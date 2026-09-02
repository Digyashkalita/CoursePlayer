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
| 4 | Video Player — full YouTube-style player chrome | In progress |
| 5 | Thumbnails | Not started |
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
  Converters/           value converters (BoolToVisibility, InverseBoolean, ...)
  Data/                 CoursePlayerDbContext + Migrations
  Resources/            Styles.xaml, brushes, theme resources
  Services/             navigation, playback, database, theming, ingestion
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
  `postproc-55.dll`. Override the location with the `COURSEPLAYER_FFMPEG_DIR`
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
`FileDocumentOutline`, `FileOutline`.

`Replay10` and `Forward10` do **not** exist — use `Rewind10` / `FastForward10`.

### App resource keys

- Brushes: `App.Brush.{Accent, AccentDim, Background, Divider, Hover, Surface,
  SurfaceRaised, TextDisabled, TextPrimary, TextSecondary, Transparent,
  Warning}`.
- Text styles: `App.Text.{Body, Caption, PageTitle, Subtitle2}`.
- Other styles: `App.Sidebar.NavItem`, `App.DropZone.Border`,
  `App.Player.{Scrubber, VolumeSlider, IconButton, PlaylistItem}`.
- Converters: `BoolToVisibility` (accepts `ConverterParameter=Invert`),
  `InverseBoolean`, `NullOrEmptyToVisibility` (also accepts `Invert`).

### Database

- Location: `%LOCALAPPDATA%\CoursePlayer\courseplayer.db`.
- Tables: `Courses`, `Assets`, `Progresses`. The `DbSet` is `Progresses`, not
  `Progress` — querying `Progress` fails with `no such table`.
- `Assets` columns: `Id`, `CourseId`, `Title`, `FilePath`, `Type`, `OrderIndex`,
  `Duration`, `Codec`, `Resolution`, `IsOnline`, `Section`. There is no `Width`
  or `Height` column; resolution is a single string.

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
twice. One pre-existing warning is expected:
`CourseDetailViewModel.cs(83,24): warning CS8625`.

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
| FFME itself | standalone probe: 30 opens + seeks on a bare `MediaElement` | **survives cleanly** |
| Double command in flight | removed the explicit `Close()` before `Open()` | fixed a real bug, crash persists |
| Idle playback | 75 s with no interaction | stable |
| Moderate switching | 12 switches at 3 s dwell, twice | stable |

The standalone probe surviving is the important datum: it points at how
CoursePlayer hosts and drives the element — re-parenting, UIA-driven input, or
view-model teardown — rather than at FFME's decoding.

Normal single-switch use has been stable in every test since the
dispatcher-deferred autoplay fix.

Next candidates to investigate: `MediaElement` re-parenting in
`VideoPlayerView.AttachMedia` / `DetachMedia`, and whether UI Automation tree
reads alone can trigger the fault.

---

## Housekeeping

Delete when Phase 4 closes: `temp_sqlite_update/`, `temp_ffme_probe/`,
`temp_mdix_probe/`, `dump*.txt`, `probe_out.txt`, `probe_err.txt`,
`cp_stdout.txt`, `cp_stderr.txt`. All are git-ignored.

Kept deliberately: `uia.ps1` and the crash-reproduction scripts, until the
fail-fast is closed out.
