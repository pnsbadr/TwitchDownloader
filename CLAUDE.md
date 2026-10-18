# CLAUDE.md

This repo is a fork of [lay295/TwitchDownloader](https://github.com/lay295/TwitchDownloader) (C#/.NET 10).
We are building a **team web app (Twitch only)** on top of its `TwitchDownloaderCore` library.

## Ground rules

- **Do not modify** `TwitchDownloaderCore`, `TwitchDownloaderCLI`, `TwitchDownloaderWPF` or their test projects.
  All new work goes in new projects so upstream changes merge cleanly. If Core genuinely needs a change,
  stop and discuss it first.
- Store all timestamps in **UTC**. Time zone conversion is display-only.
- **Never log OAuth tokens** (Twitch or app auth) — not in the audit log, app logs, or exception messages.
- Commit after each working step.

## Existing architecture

- `TwitchDownloaderCore` (class library, net10.0) — all the real logic. Each task is an `XxxOptions` class
  (`Options/`) + a worker class with `DownloadAsync(CancellationToken)`:
  - `VideoDownloader` + `VideoDownloadOptions` — VOD download. Fetches the m3u8, downloads parts in parallel
    (`DownloadThreads`, must be > 0), then runs ffmpeg (`-c copy` concat) to produce the mp4.
  - `ClipDownloader`, `ChatDownloader`, `ChatRenderer`, `ChatUpdater`, `TsMerger`.
  - `TwitchHelper` — Twitch GQL calls (e.g. `GetVideoInfo(id)` → `title`, `createdAt` (stream date), `lengthSeconds`).
  - Progress/logging goes through `ITaskProgress` (`Interfaces/`); `StubTaskProgress.Instance` is a no-op.
  - `IdParse.MatchVideoId` parses VOD IDs/URLs; `FilenameService.ReplaceInvalidFilenameChars` sanitizes titles.
- `TwitchDownloaderCLI` — console front end over Core (`Modes/DownloadVideo.cs` is a good reference for
  building a `VideoDownloadOptions`).
- `TwitchDownloaderWPF` — Windows GUI front end over Core.

### Trim semantics (VideoDownloadOptions)
- `TrimBeginningTime` / `TrimEndingTime` are **absolute offsets from the VOD start** (end is not a duration).
  Set `TrimBeginning`/`TrimEnding` to true for them to apply.
- `TrimMode.Safe` cuts on segment boundaries (slightly longer); `TrimMode.Exact` uses ffmpeg `-ss`/`-t`.
- `Quality` like `"1080p60"`, `"720p"`, `"Audio"`; unknown/null falls back to best quality.

### FFmpeg
Core never locates ffmpeg itself — callers pass `FfmpegPath` in the options and Core runs it as a `Process`.
The CLI uses `--ffmpeg-path`, else `ffmpeg(.exe)` in the working dir or on PATH; the WPF app passes `"ffmpeg"`
and auto-downloads `ffmpeg.exe` next to the exe via Xabe.FFmpeg.Downloader.

The web app takes the path from configuration (`Ffmpeg:Path` in `TwitchDownloaderWeb/appsettings.json`).
On the dev machine it is pinned to the **Gyan FFmpeg 8.0.1** WinGet install
(`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg_...\ffmpeg-8.0.1-full_build\bin\ffmpeg.exe`), **not**
the yt-dlp FFmpeg build, which comes first on PATH. Don't rely on PATH lookup; override `Ffmpeg:Path` per
environment (e.g. env var `Ffmpeg__Path`).

## Web app (`TwitchDownloaderWeb`)

ASP.NET Core minimal API (net10.0) referencing Core. Config: `Ffmpeg:Path`, `Downloads:OutputFolder`
(relative to the content root; git-ignored `downloads/`), `Downloads:TempFolder` (blank = Core's default
cache dir), `Downloads:Threads`. Run with `dotnet run --project TwitchDownloaderWeb`.

- `POST /api/vods/download` — body `{ vodId, title?, quality?, startTime?, endTime? }` (times as `"hh:mm:ss"`,
  offsets from VOD start). Exact trim, synchronous, writes `<sanitized title>.mp4` (non-colliding) and returns
  `{ fileName, path, sizeBytes }`. Blank title → VOD's Twitch title. Unknown quality → 400 listing the
  available ones (Core would otherwise silently fall back to best). Code: `Endpoints/VodDownloadEndpoints.cs`.
- `Services/LoggerTaskProgress` adapts Core's `ITaskProgress` to `ILogger`.
- Gotcha: titles are sanitized with Core's `FilenameService`, which swaps `: ? | / \` etc. for full-width
  look-alikes (`：？｜`), so file names contain non-ASCII characters.

## Build

```
dotnet build TwitchDownloaderWPF.sln
```
(The WPF project needs Windows; the web app and Core build cross-platform.)

## Project goals (full plan)

- Login/signup and team signup with invites; a shared team library.
- Users add a Twitch VOD/clip with: custom title (defaults to the VOD's Twitch title if blank), quality,
  trim mode, start/end trim times.
- Each entry auto-records the date added and the VOD's original stream date (UTC).
- Library sortable by date and searchable by title.
- Per-user setting to display times in local computer time or Pacific time (`America/Los_Angeles`, so
  PST/PDT is automatic). Display-only; storage stays UTC.
- Clips under a configurable length limit are downloaded and stored as files; longer ones are stored as
  fields only (VOD ID + trim settings) and generated on demand.
- Audit log of signups, logins, adds, downloads, failures. Never log OAuth tokens.

## Phases

1. **(current)** CLAUDE.md; new ASP.NET Core web project `TwitchDownloaderWeb` referencing Core; one endpoint
   that takes VOD ID, title, quality, start/end trim and produces a trimmed mp4 (Exact trim) named after the title.
   No accounts, DB or UI yet.
2. Later: database + library entries, accounts/teams/invites, UI, length-limit storage policy, audit log,
   time zone display setting.
