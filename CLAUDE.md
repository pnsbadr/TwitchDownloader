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

ASP.NET Core minimal API (net10.0) referencing Core, plus a static page in `wwwroot/` (plain HTML/CSS/JS,
no build step). Run with `dotnet run --project TwitchDownloaderWeb` → http://localhost:5254.

### Configuration (`appsettings.json`)
- `Database:Path` — SQLite file, default `App_Data/clipco.db` (git-ignored).
- `Ffmpeg:Path` — see FFmpeg above.
- `Downloads:OutputFolder` (default `downloads`, git-ignored), `Downloads:TempFolder` (blank = Core's default
  cache dir), `Downloads:Threads` (per job), `Downloads:MaxConcurrentJobs` (default 1).
- `Downloads:MaxStoredClipSeconds` (default 180) — clips **at or under** this are downloaded and stored;
  longer ones are reference-only. Enforced on the server from the trim times; the page only previews it.
- `Downloads:GeneratedFileHours` (default 72) — on-demand files for reference-only entries are deleted after this.

### Data (EF Core 10 + SQLite, `Data/`)
- `LibraryEntries`: title, VOD ID, channel, quality (resolved name, null = best), trim mode, start/end
  (end null = end of VOD), clip/VOD length, `StreamDateUtc` (Twitch `createdAt`), `AddedAtUtc`, thumbnail,
  `Status` (Queued/Downloading/Ready/Failed/ReferenceOnly/VodUnavailable), stored file path/size, generation
  fields for on-demand files (`GenerationStatus`, path, size, `GeneratedExpiresAtUtc`), `AddedByUserId`
  (string, no FK yet — link to users in phase 3).
- `AuditEvents`: timestamp, action (`AuditActions`), entry ID (no FK so rows outlive deleted entries),
  user ID (null until phase 3), details. Write via `db.Audit(...)` so it commits with the change; **never
  put tokens in details**.
- A converter marks every `DateTime` as UTC when read back (SQLite has no date type) — keep all times UTC.
- Migrations: `cd TwitchDownloaderWeb && dotnet tool restore && dotnet ef migrations add <Name> -o Data/Migrations`.
  They are applied automatically at startup.

### Jobs and files (`Services/`)
- `DownloadQueue` (in-memory channel) + `DownloadWorker` (BackgroundService). The DB status is the source of
  truth: on startup, entries left Queued/Downloading (or generation Queued/Generating) are re-queued and an
  `download.interrupted` audit event is written.
- Files are named by entry ID: stored → `downloads/library/{id}.mp4`, on-demand → `downloads/generated/{id}.mp4`.
  DB paths are relative to `OutputFolder`. The title is only used as the download file name.
- `LibraryCleanupService` (every 10 min + at startup): deletes expired generated files and sweeps orphaned
  `{id}.mp4` files in those two folders (skips running jobs and files < 10 min old). It never touches other files
  in `downloads/` (e.g. output of the phase 1 dev endpoint).
- Deleting an entry cancels its running job. On Windows a file still open by ffmpeg can't be deleted by the
  request; the cancelled job and then the sweep remove it.
- `VodInfoService` (Twitch lookups + shared validation), `VodDownloadRunner` (builds `VideoDownloadOptions`),
  `LoggerTaskProgress` (Core `ITaskProgress` → `ILogger`), `TrimTime` (parses `h:mm:ss`/`m:ss`/seconds with
  unbounded hours; TimeSpan's own format caps hours at 23).

### API
| Endpoint | Notes |
|---|---|
| `GET /api/vods/info?vod=` | Title, length, stream date, thumbnail, qualities, `restricted`, `maxStoredClipSeconds` |
| `POST /api/entries` | `{ vodId, title?, quality?, trimMode?, startTime?, endTime? }` → 201. Validates VOD, times, quality (unknown → 400 listing available) |
| `GET /api/entries?search=&sort=dateAdded\|streamDate&order=desc\|asc&limit=&from=&to=&dateField=dateAdded\|streamDate` | Search is a LIKE substring match (case-insensitive for ASCII only). `from` (inclusive) / `to` (exclusive) are ISO 8601 instants **with an offset** (bare dates/times → 400) and filter on `dateField` (default dateAdded) |
| `GET /api/entries/{id}` | |
| `PATCH /api/entries/{id}` | `{ title }` — audited as `entry.renamed` |
| `DELETE /api/entries/{id}` | Removes entry + stored/generated files — audited. Phase 3: restrict who can delete |
| `POST /api/entries/{id}/generate` | Reference-only: queue on-demand file (202) or 200 if ready. Failed stored entry: retry. VodUnavailable → 410 |
| `GET /api/entries/{id}/file` | Streams the file (range requests supported); 409 if not ready; 410 if missing on disk (entry then becomes retryable) |
| `POST /api/vods/download` | Phase 1 dev endpoint: synchronous, writes `<title>.mp4` straight into `downloads/`, not part of the library |

`canDownload` in entry responses checks the generated file's expiry itself, since cleanup runs periodically.

### Web page
- Time zone toggle (Local / Pacific `America/Los_Angeles`) saved in `localStorage` key `clipLibrary.timeZone`
  (per browser until per-user settings exist in phase 3). Formatting only — the API always returns UTC (`...Z`).
- `Intl.DateTimeFormat` throws if `dateStyle`/`timeStyle` are combined with `timeZoneName`; format parts explicitly.
- The list polls every 3 s while any entry is queued/downloading/generating.
- Calendar (plain JS, no library): month view of clip counts per day, by stream date or date added (saved in
  `localStorage` key `clipLibrary.calendarField`). **Days are computed in the shown time zone**: the page converts
  that zone's midnights to UTC (`zonedMidnightUtc`, via `Intl` parts) and sends them as `from`/`to`, so the
  calendar counts and the day-filtered list use the same server-side filter. Counts come from the month's
  entries (limit 1000) grouped client-side. Clicking a day filters the list; Clear or clicking it again removes it.
- The calendar is hidden by default behind a **Calendar** button in the search/filter row and opens above that
  row; open/closed is saved in `localStorage` key `clipLibrary.calendarOpen`. Counts aren't fetched while closed.
  A day filter survives closing the calendar and is then shown as a chip ("Oct 12 ✕") next to the search box.
  The selected day itself is not persisted across reloads.

### Gotchas
- Core silently falls back to the best quality when the requested one doesn't exist — always validate first.
- Titles are sanitized with Core's `FilenameService`, which swaps `: ? | / \` etc. for full-width look-alikes
  (`：？｜`), so file names contain non-ASCII characters (Git Bash globbing chokes on some; use PowerShell).
- Twitch deletes VODs after a while (typically 7–60 days). Reference-only entries then become `VodUnavailable`
  when generation is attempted; stored entries keep their file.
- Restricted (subscriber-only) VODs are rejected until OAuth support exists.

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

1. **Done.** CLAUDE.md; `TwitchDownloaderWeb` project referencing Core; `POST /api/vods/download` producing a
   trimmed mp4 (Exact trim) named after the title.
2. **Done.** SQLite library + audit log; stored vs reference-only rule with a background job queue; entry API
   (add/list/get/rename/delete/generate/file); on-demand files expiring after 72 h; web page with search, sort,
   status, download, rename/delete and the Local/Pacific toggle (saved in the browser).
3. **Next:** login/signup, teams with invites, shared team library; link `AddedByUserId` / `AuditEvents.UserId`;
   restrict delete; per-user time zone setting (replacing localStorage); audit signups/logins. Never log tokens.
