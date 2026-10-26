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
- `Downloads:ExactTrimPreset` (default `veryfast`) / `Downloads:ExactTrimCrf` (default 18) — libx264 settings for Exact trims (see Jobs and files).

### Data (EF Core 10 + SQLite, `Data/`)
- `LibraryEntries`: title, VOD ID, channel, quality (resolved name, null = best), trim mode, start/end
  (end null = end of VOD), clip/VOD length, `StreamDateUtc` (Twitch `createdAt`), `AddedAtUtc`, thumbnail,
  `Status` (Queued/Downloading/Ready/Failed/ReferenceOnly/VodUnavailable), stored file path/size, generation
  fields for on-demand files (`GenerationStatus`, path, size, `GeneratedExpiresAtUtc`), `AddedByUserId`
  (string, no FK yet — link to users in phase 3).
- `Channels` (`SavedChannel`): saved team channels for the VOD browser: login (unique, lower-case), display name,
  Twitch user ID, `WatchMutedAudio` (for the planned muted-audio rescue), `AddedAtUtc`, `AddedByUserId`.
- `AudioRescues` (`VodAudioRescue`, key = VOD ID) + `RescuedAudioSegments` (unique VodId+Key): see Muted audio rescue.
  `LibraryEntries.AudioRestoredSeconds` = muted seconds replaced in the entry's last file; `AudioMutedSeconds` = muted seconds
  in it with no clean copy saved (only known when the VOD has a rescue; audited `audio.not_restored`).
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
- **Exact trims don't use Core's Exact mode.** Core cuts with `-ss` + `-c copy` on the concat input, and copied video can only
  start on a keyframe (Twitch: every ~2 s), so audio started on time while the first frame stayed frozen up to ~2 s.
  `ExactTrimService` has Core download in Safe mode, then re-encodes the cut (libx264 + AAC 192k, `+faststart`), finding the
  cut inside the Safe file from the playlist's segment offsets. If the playlist can't be read it falls back to Core's Exact.
  Safe entries are still plain stream copy. (The phase 1 dev endpoint still uses Core's Exact.)
- `VodInfoService` (Twitch lookups + shared validation), `VodDownloadRunner` (builds `VideoDownloadOptions`),
  `LoggerTaskProgress` (Core `ITaskProgress` → `ILogger`), `TrimTime` (parses `h:mm:ss`/`m:ss`/seconds with
  unbounded hours; TimeSpan's own format caps hours at 23).

### Muted audio rescue (`Services/AudioRescue/`)
Twitch mutes segments (`123-muted.ts`) and keeps the clean copy (`123.ts`) for a limited time (Core assumes 24 h and
only tries to unmute VODs <= 24 h old, silently keeping muted audio on 403). We save clean audio and splice it back:
- `AudioRescueService` (BackgroundService, `AudioRescue:*` settings): every `PollMinutes` it checks saved channels with
  `WatchMutedAudio` for VODs in the unmute window; `POST /api/vods/{id}/rescue` starts one by hand. It downloads **every**
  segment of the audio-only rendition as raw `.ts` (clean URL for muted ones; 403/404 = unrecoverable) into
  `downloads/rescue/{vodId}/{key}.ts`, re-checks every `LiveRecheckMinutes` while the playlist has no `#EXT-X-ENDLIST`,
  then `PruneAfterHours` (48) after the stream ends keeps only segments that are muted in the playlist by then.
  The cleanup service never touches `rescue/`. Statuses: Grabbing / WaitingToPrune / Pruned / Failed.
- **Real-Twitch finding (2026-10-20):** a rescue started ~10 h after a stream ended got 403 for *every* muted segment's
  clean copy (237/237), and Core's own unmute failed too. Don't rely on the "24 h unmute window"; the rescue only helps
  if it runs while the stream is live, before Twitch mutes. The 24 h window/badge is still Core's assumption.
- `AudioRestoreService`, called by `DownloadWorker` when an entry's trim overlaps muted segments with saved audio:
  Core downloads in **Safe** mode, then `AudioSplicer` mixes the clean audio in (video `-c:v copy`, audio re-encoded AAC
  192k, muted ranges gated to silence), then Exact entries are cut by `ExactTrimService` (re-encode). Any failure falls
  back to a normal download (audited `audio.restore_failed`). Scratch files go in `downloads/work/` (cleared at startup).
- **Placement must use the segments' PTS, not just #EXTINF sums** (those were ~16 ms off in testing): Core concatenates
  with `duration` directives, so a segment's audio sits at (playlist offset - first segment's offset) +
  (audio PTS - segment start PTS). `RestorePlanner` does this, reading the first 256 KB of the download-quality
  segments (`TsTimestamps`, a small PES parser) and the PTS stored with each rescued segment.
- `VodPlaylist` parses segments with Core's `M3U8.Parse` and mirrors Core's trim rule (`GetTrimRange`).
  `IVodPlaylistSource` exists so tests can fake Twitch.
- HttpClient logging is set to Warning in `appsettings.json` so playlist/segment URLs (which can carry signed tokens)
  never reach the logs.
- Tests: `TwitchDownloaderWeb.Tests` (`dotnet test --project TwitchDownloaderWeb.Tests`). `AudioSpliceSyncTests` builds a
  fake VOD with ffmpeg (beeps at known times, two muted segments) and checks restored beeps against an unmuted download
  (<= 25 ms; measured <= 2 ms). Needs ffmpeg on PATH or `FFMPEG_PATH`, else it is skipped. `AudioRescueServiceTests`
  runs grab, live re-check and prune against a fake Twitch.

### API
| Endpoint | Notes |
|---|---|
| `GET /api/vods/info?vod=` | Title, length, stream date, thumbnail, qualities, `restricted`, `maxStoredClipSeconds` |
| `POST /api/entries` | `{ vodId, title?, quality?, trimMode?, startTime?, endTime? }` → 201. Validates VOD, times, quality (unknown → 400 listing available) |
| `GET /api/entries?search=&sort=dateAdded\|streamDate&order=desc\|asc&limit=&from=&to=&dateField=dateAdded\|streamDate` | Search is a LIKE substring match (case-insensitive for ASCII only). `from` (inclusive) / `to` (exclusive) are ISO 8601 instants **with an offset** (bare dates/times → 400) and filter on `dateField` (default dateAdded) |
| `GET /api/entries/{id}` | |
| `PATCH /api/entries/{id}` | `{ title }` — audited as `entry.renamed` |
| `PUT /api/entries/{id}/trim` | `{ startTime, endTime }` (blank end = end of VOD) — re-cuts the entry: cancels a running job (waits up to 20 s, else 409), deletes its files, re-applies the stored-vs-reference rule and re-queues a stored download. VodUnavailable → 410. Audited `entry.retrimmed` |
| `DELETE /api/entries/{id}` | Removes entry + stored/generated files — audited. Phase 3: restrict who can delete |
| `POST /api/entries/{id}/generate` | Reference-only: queue on-demand file (202) or 200 if ready. Failed stored entry: retry. VodUnavailable → 410 |
| `GET /api/entries/{id}/file` | Streams the file (range requests supported); 409 if not ready; 410 if missing on disk (entry then becomes retryable). `?preview=true` (the vertical editor's player) isn't audited as a download |
| `GET /api/channels` / `POST /api/channels` `{ login }` / `DELETE /api/channels/{id}` | Saved channels. POST checks the login exists on Twitch (404) and isn't saved (409); add/remove audited |
| `GET /api/channels/{login}/vods?type=archive\|highlight\|upload\|all&cursor=&limit=` | Any channel's VODs, newest first, via Core's `GetGqlVideos`. `nextCursor` = last edge's cursor; `unmuteWindowOpen` = ended < 24 h ago. 502 if Twitch is unreachable |
| `GET /api/vods/{id}/rescue` / `POST /api/vods/{id}/rescue` | Audio rescue status (404 if none) / start one now (202; a Failed one is retried; others returned as is) |
| `GET /api/entries/{id}/shortform` / `POST /api/entries/{id}/shortform` | Vertical (9:16) renders: `{ status: None\|Rendering\|Failed, progress, error, layout, renders: [{ id, createdAtUtc, expiresAtUtc, sizeBytes, layout, fileUrl, thumbnailUrl }] }` (newest first) / start a render (202) with `{ mode: single\|split, top: {x,y,width,height}, bottom?, splitRatio?, trimStart?, trimEnd? }` (fractions of the source video). 409 if the entry has no file |
| `GET /api/entries/{id}/shortform/{renderId}/file[?inline=true]` / `.../thumbnail` / `DELETE .../{renderId}` | A render's mp4 (`<title> (vertical).mp4`, audited `file.served`; `inline` = the page's preview player, not audited), its jpg thumbnail, delete it. 404 if gone/expired |
| `POST /api/vods/download` | Phase 1 dev endpoint: synchronous, writes `<title>.mp4` straight into `downloads/`, not part of the library |

`canDownload` in entry responses checks the generated file's expiry itself, since cleanup runs periodically.

### Web page
- Time zone toggle (Local / Pacific `America/Los_Angeles`) saved in `localStorage` key `clipLibrary.timeZone`
  (per browser until per-user settings exist in phase 3). Formatting only — the API always returns UTC (`...Z`).
- `Intl.DateTimeFormat` throws if `dateStyle`/`timeStyle` are combined with `timeZoneName`; format parts explicitly.
- The list polls every 3 s while any entry is queued/downloading/generating.
- Static files are served with `Cache-Control: no-cache` (no build step, no versioned names), so a normal reload always picks up a new `app.js`.
- Calendar (plain JS, no library): month view of clip counts per day, by stream date or date added (saved in
  `localStorage` key `clipLibrary.calendarField`). **Days are computed in the shown time zone**: the page converts
  that zone's midnights to UTC (`zonedMidnightUtc`, via `Intl` parts) and sends them as `from`/`to`, so the
  calendar counts and the day-filtered list use the same server-side filter. Counts come from the month's
  entries (limit 1000) grouped client-side. Clicking a day filters the list; Clear or clicking it again removes it.
- The calendar is hidden by default behind a **Calendar** button in the search/filter row and opens above that
  row; open/closed is saved in `localStorage` key `clipLibrary.calendarOpen`. Counts aren't fetched while closed.
  A day filter survives closing the calendar and is then shown as a chip ("Oct 12 ✕") next to the search box.
  The selected day itself is not persisted across reloads.
- **Last 24h** button (left of Calendar): rolling `from = now − 24h` filter on the date chosen in the
  **sort dropdown** next to the search box (Date added / Stream date), not the calendar's switch; recomputed on every load. It and a calendar day filter are mutually exclusive.
  Not persisted. There is no "jump to today" button in the calendar (removed on request).

- **VOD browser** (top of the Add panel): saved-channels dropdown + free-text channel (login or twitch.tv link), type
  toggle, Show VODs / Load more. Clicking a card fills the VOD field and the title (only replacing a title the page
  filled itself), then runs Get Info. Last channel and type are saved in `localStorage` keys `clipLibrary.browseChannel`
  / `clipLibrary.browseType`. Cards use `formatDate`, so they follow the time zone toggle. Cards show the rescue
  status and a **Save audio now** button (unmute window open, no rescue yet) or **Retry saving audio** (failed).
  Library entries with restored audio show an "Audio restored" badge, and "Still muted" when muted parts had no clean copy;
  VOD cards show "Muted parts not saved" when a rescue got 403s for muted segments.
- **Watching VODs** (no server part): the official Twitch embed (`player.twitch.tv/js/embed/v1.js`, loaded lazily) streams
  the VOD straight from Twitch. `parent` is `location.hostname`, so open the page as `localhost` (Twitch may reject a bare IP).
  **Watch** next to Get Info / on VOD cards opens an inline player with **Set start here / Set end here** (fills the trim
  fields from the estimated position below, hundredths of a second) and **Play clip** (seeks to start, pauses at end). The review dialog
  starts at the clip start and pauses at its end (not shown for VodUnavailable). Closing a player
  removes the iframe so playback stops. Sub-only VODs play only if the browser is logged in to Twitch with access.
  **Review** on a library entry (and automatically right after Add to library) opens the clip review dialog: the
  player plus a trim bar spanning the clip ± a margin (25% of its length, 5–60 s, widened when a handle leaves it), with
  draggable handles (arrow keys nudge 0.1 s, Shift 1 s), a playhead, click-to-seek, ±1 s / ±0.1 s buttons, Set to playhead,
  Play clip / Play last 3s, and **Save new cut** (`PUT /trim`; closes the dialog on success, result shown above the library). Unsaved changes ask before closing.
  The embed's `getCurrentTime()` is a cache refreshed about once a second (and stale right after a seek), so `VodPlayer.position()`
  estimates the live offset (last refresh or seek target + time played since, via PLAYING/PAUSE/SEEK events; refreshes are caught on the iframe's `message` events) and a 40 ms tick
  pauses previews at the end. Using the raw value made Set end land up to ~1 s early and previews stop up to ~1 s late.

### Vertical (9:16) export (`Services/Shortform/`)
- Like Twitch's portrait clip editor: **Make vertical** on an entry with a file opens a dialog with the landscape video,
  one box ("One box") or two boxes ("Two boxes": pink top over cyan bottom) you drag/resize, and a live canvas preview of
  the portrait frame; the pill in the preview sets the top/bottom split (20–80 %). Each box keeps the shape of the slot it
  fills, so nothing is stretched. The player has a preview-only volume slider/mute (default 30 %, saved in `localStorage` key
  `clipLibrary.previewVolume`); renders always keep the clip's audio as is.
- **Renders list** under the editor (shared with the team): every render is kept with its layout and a thumbnail; each row
  has Download, Preview (plays it in the portrait panel), "Use these settings" and Delete. A "Matches the editor" badge marks
  the render made from exactly the boxes and trim on screen, so a stale file isn't mistaken for the new one.
- **Trim**: a bar under the player with start/end handles (arrow keys ±0.1 s, Shift ±1 s), "Start here"/"End here" at the
  playhead, and Reset. The preview loops over the trimmed part. Sent as `trimStart`/`trimEnd` (seconds from the start of the
  entry's file, null = start/end; min 1 s). The server uses input `-ss`/`-t` (frame-accurate since video is re-encoded) and
  re-encodes audio to AAC 192k only when trimmed (copied otherwise). The trim end isn't capped at `ClipLengthSeconds`
  because Safe-mode files run longer.
- Rendering is server-side (ffmpeg on the machine running the app); the browser only plays the video and draws the preview.
- `ShortformService` renders with ffmpeg: crop by fractions of `iw`/`ih` (no ffprobe), scale to 1080 wide, `vstack`,
  libx264 veryfast CRF 20, audio copied, to `downloads/shortform/{entryId}/{renderId}.mp4` (+ `.json` layout/created time,
  `.jpg` thumbnail; the json is written last, so a render is listed only when complete). Render IDs are UTC timestamps and
  are checked to be alphanumeric before touching paths. Numbers are written with the invariant culture (a decimal comma
  would break the filter). Newest `MaxRendersPerEntry` (5) kept for `Downloads:GeneratedFileHours`; the cleanup service
  deletes older/expired ones, leftovers, and folders of deleted entries. One render at a time; job state is in memory
  (an interrupted render is just gone). Deleting the entry deletes its folder. The editor reopens with the newest render's layout.
  Audited `shortform.rendered` / `shortform.failed`. No chat overlay yet.

### Gotchas
- Core silently falls back to the best quality when the requested one doesn't exist — always validate first.
- Titles are sanitized with Core's `FilenameService`, which swaps `: ? | / \` etc. for full-width look-alikes
  (`：？｜`), so file names contain non-ASCII characters (Git Bash globbing chokes on some; use PowerShell).
- Twitch deletes VODs after a while (typically 7–60 days). Reference-only entries then become `VodUnavailable`
  when generation is attempted; stored entries keep their file.
- Restricted (subscriber-only) VODs are rejected until OAuth support exists.
- Core's `GetGqlVideos` / `GetUserIds` paste the login, type and cursor straight into the GQL query string; the web app
  validates all three first (`ChannelService`). Video cursors aren't plain base64, so the cursor check only rejects quotes,
  backslashes and control characters. The WPF Mass Downloader pages with the *first* edge's cursor; we use
  the last (to verify against real data). GQL video lists have no live/recording flag.

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
