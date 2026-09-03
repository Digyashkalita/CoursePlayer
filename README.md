# CoursePlayer

An offline Windows desktop app that turns local course folders into a
YouTube-Premium-style learning experience.

Point it at a folder of videos and documents and it becomes a browsable course
with a real player: resume where you left off, autoplay the next lesson, track
what you have finished.

Single user, local only. No login, no cloud, no server, no telemetry — the app
never makes a network request.

## Features

- **Import from a folder.** Scans a directory tree, groups files into sections,
  and probes each video for duration, codec, and resolution.
- **Course library.** A grid of course cards with cover art and a content
  summary, and a course detail page listing every lesson.
- **Cover art.** Video lessons get a thumbnail taken from 10 % into the file;
  PDFs get their first page rendered. Generated once, cached on disk, and shown
  on the home grid, the lesson list, and the player playlist.
- **Video player.** Custom scrubber with a buffered track, 10-second skip,
  playback speed, volume, subtitles from sidecar `.srt` files, fullscreen, a
  collapsible "Up next" playlist, and chrome that hides itself while you watch.
- **Progress tracking.** Watch position is saved continuously and restored on
  reopen; lessons are marked complete at 95 %.
- **Keyboard shortcuts.** Space, F, M, N, P, C, `[` / `]`, arrow keys, Esc.

## Requirements

- Windows 10 or 11, 64-bit
- [.NET 9 desktop runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- FFmpeg **4.4** shared binaries, 64-bit, in `C:\ffmpeg\x64`, including
  `ffmpeg.exe` (used to extract thumbnail frames)

The FFmpeg version matters: the player binds FFmpeg 4.4 sonames
(`avcodec-58` and friends) and will not load a newer build. Override the
location with the `COURSEPLAYER_FFMPEG_DIR` environment variable.

## Build

```powershell
dotnet build CoursePlayer\CoursePlayer.csproj -c Debug
```

The app stores its database, logs, and generated cover art under
`%LOCALAPPDATA%\CoursePlayer`.

## Project status

Built in phases; see [PROGRESS.md](PROGRESS.md) for the current state, the
environment facts worth knowing before touching the code, and the open issues.

## Built with

WPF on .NET 9 with MVVM (`CommunityToolkit.Mvvm`), EF Core + SQLite for local
storage, `FFME.Windows` for playback, `MaterialDesignThemes` and
`MahApps.Metro` for the shell, and Serilog for logs.
