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
- `Downloads:MaxStoredClipSeconds` (default 180) — clips **at or under** this are downloaded and stored, **only for teams the app Owner
  turned stored clips on for** (`Teams.StoreClips`, off by default, badrfly 2026-11-18; Admin → Teams "Store clips",
  `PUT /api/admin/teams/{id}/features { storeClips }`); otherwise every clip is reference-only, like segments. Longer ones are always
  reference-only. Enforced on the server from the trim times (`EntryEndpoints.IsStoredLength`, on add and re-cut; existing files stay).
  The page shows short references like any clip: only a reference **over** the limit gets the "Long" tag / translucent bar
  (`me.maxStoredClipSeconds`), and clips stay capped at the limit on the timeline.
- `Downloads:GeneratedFileHours` (default 72) — on-demand files for reference-only entries are deleted after this.
- `ErrorReports:KeepDays` (default 90) / `ErrorReports:MaxReports` (default 20 000) — how long and how many error reports are kept (see `ErrorReports`).
- `Downloads:ExactTrimPreset` (default `veryfast`) / `Downloads:ExactTrimCrf` (default 18) — libx264 settings for Exact trims (see Jobs and files). `Downloads:SmartCut` (default true): re-encode only the edges of Exact cuts (see Smart cuts).

### Data (EF Core 10 + SQLite, `Data/`)
- `LibraryEntries`: title, VOD ID, channel, quality (resolved name, null = best), trim mode, start/end
  (end null = end of VOD), clip/VOD length, `StreamDateUtc` (Twitch `createdAt`), `AddedAtUtc`, thumbnail,
  `Status` (Queued/Downloading/Ready/Failed/ReferenceOnly/VodUnavailable), stored file path/size, generation
  fields for on-demand files (`GenerationStatus`, path, size, `GeneratedExpiresAtUtc`), `AddedByUserId`
  (string, no FK yet — link to users in phase 3).
- `LibraryEntries.Kind` (`Vod`/`Clip`, migration default `Vod`) + `ClipSlug`: see Twitch clips. For clips `VodId` is the source
  VOD (0 if Twitch didn't say) and `StartTime`/`EndTime` the clip's place in it (whole seconds).
- `LibraryEntries.Color` (badrfly 2026-11-20, migration `ClipColors`): a clip's color, one of `VodMarker.Colors`; null = the clip violet
  ("violet" is stored as null). `POST /api/entries` and `PATCH /api/entries/{id}` take `color` (PATCH: a blank title with a color keeps the
  title; unknown color → 400); entry and timeline clip JSON carry it; copies keep it. Drawn by `clipColor` in `ui/colors.js`.
- `Channels` (`SavedChannel`): the team's **assigned channels** (see Team channels): login (unique per team, lower-case), display name,
  Twitch user ID, `WatchMutedAudio` (= automatic audio saving for this channel) and `AllowSaveAudio` (= Save audio now allowed), both set
  **only by the app Owner** (badrfly 2026-11-18; off for new channels; migration `ChannelAudioAndStoredClips` copied the old team setting),
  `AddedAtUtc`, `AddedByUserId`.
- `AudioRescues` (`VodAudioRescue`, key = VOD ID) + `RescuedAudioSegments` (unique VodId+Key): see Muted audio rescue.
  `LibraryEntries.AudioRestoredSeconds` = muted seconds replaced in the entry's last file; `AudioMutedSeconds` = muted seconds
  in it with no clean copy saved (only known when the VOD has a rescue; audited `audio.not_restored`).
- `AuditEvents`: timestamp, action (`AuditActions`), entry ID (no FK so rows outlive deleted entries),
  user ID (null until phase 3), details. Write via `db.Audit(...)` so it commits with the change; **never
  put tokens in details**.
- `VodMarkers` (`VodMarker`): team markers on a VOD's player timeline: TeamId (team query filter, cascade), VodId, Seconds, `EndSeconds`
  (null = a moment; set = a **segment**, ≥ 0.5 s long), Label (≤ 200, may be blank), Color (`VodMarker.Colors`: yellow, blue, pink, orange,
  red, lime, teal, violet, gray; same order as `MARKER_COLORS` in `ui/colors.js`, add new ones at the end), CreatedByUserId,
  CreatedAtUtc. Audited `marker.added` / `marker.changed` / `marker.deleted`.
- `LibraryEntries.FollowsMarkers` / `MadeFromMarkers`: the old **live markers entry** (badrfly 2026-11-10: the first marker on a live VOD made a
  reference-only entry ±30 s around the markers that moved with every marker change). **Removed** (badrfly 2026-11-20: segments mark ranges and
  the clip tool makes clips on purpose): `LiveMarkerEntryService.MarkersChangedAsync` makes and moves nothing, marker responses' `liveEntry`
  is always null. Entries it made stay; the columns stay but no longer act.
- `TimelineRows` (`TimelineRow`) + `MemberVods` (badrfly, 2026-11-15, frontend rework): **rows** are a member's own lanes on one VOD's
  timeline (team query filter, cascade with the team): `VodId`, `UserId` (owner), `Kind` (`Video` = segments and moments, `Clip` = clips),
  `Position` (order among the owner's rows on the VOD), optional `Name` (≤ 60); at most 20 per member per VOD. Rows don't carry over to other
  VODs. `VodMarkers.RowId` / `LibraryEntries.RowId` (FK, SetNull) put each item on a row **of its creator** (never anyone else's; clips on
  clip rows, segments/moments on video rows). `SaveChanges` fills a missing `RowId` with the creator's first row of the right kind on the
  VOD, making it if needed (so the clip tool, markers and the live markers entry need no changes). The first time a member with `AddEntries`
  opens a VOD's timeline (`GET /timeline`, not in support view) they get one video and one clip row (`MemberVods` remembers it, so a deleted
  row doesn't come back). Migrations `TimelineRows` + `TimelineRowsBackfill` put existing markers on their creator's row and entries on
  their adder's (entries from before accounts: the team owner's) row. Deleting a row deletes its segments/moments (audited `row.deleted`);
  a clip row that still holds clips → 409 `code: "row-has-clips"`. Hide/solo/lock are page-only.
  **Synced rows** (badrfly 2026-11-20, migration `SyncedRows`): a row of another streamer's VOD shown on one VOD's timeline. It is a real row
  of its own VOD (`VodId` = theirs, so their VOD's timeline shows it as a plain row) with `ShownOnVodId` = the VOD it's shown on and
  `SyncBaseSeconds` (shown VOD's createdAt − its VOD's createdAt). `POST /api/vods/{id}/rows { kind, syncedVodId }` makes one (both VODs open
  to the team, read from Twitch; name defaults to their channel). `GET /timeline` adds them (after the member's own rows) with their markers
  and clips; rows carry `vodId`, `shownOnVodId` and `shift` (= `SyncBaseSeconds` + the pair's `VodSyncOffsets` correction:
  their second = this VOD's second + shift); clips carry `vodId`. Items on them are items of their own VOD in its seconds. First rows
  (`EnsureFirstRows`) and `DefaultRowAsync` ignore synced rows.
- `Videos` (`Video`) + `VideoSegments` (badrfly, 2026-11-15, `VideoEndpoints`): a **video** is one or more segments of its member (since 2026-11-20 from
  **any** of their video rows and VODs, synced rows included; each VOD must be open to the team), linked in order with a title (team query
  filter; `RowId` FK SetNull, the row it was made on; segments carry `vodId` / `rowId`, exports cut each from its own VOD
  (`ExportVideoPart.VodId`); the card's thumbnail is the middle segment's VOD; links cascade with the video and with the marker). Owner = the row's
  member; edit own (`AddEntries`) or anyone's (`EditAnyEntries`), delete own or anyone's (`DeleteAnyEntries`); others copy it
  (`POST /api/videos/{id}/copy`: segments copied onto the caller's row, linked the same way). It **follows its segments**: lengths come from the
  markers live; deleting a segment unlinks it and a video with none left is deleted (`VideoEndpoints.DeleteEmptyAsync`, after marker/row
  deletes, audited `video.deleted`); a linked segment can't become a moment (409 `code: "segment-in-video"`); it may move rows of its VOD. Deleting a video
  keeps its segments. Channel name, stream date and thumbnail are read from Twitch when it's made. Audited `video.added`/`video.changed`.
  **Videos across streams** (badrfly 2026-11-20): a video may link segments of VODs that don't overlap (a story started yesterday, finished
  today). `GET /api/videos?vodIds=` and `/api/vods/work` count a video on every VOD it plays from; a video with a segment on any hidden VOD
  is hidden whole (query filter). **Deleting a segment that's in videos asks** (everywhere: Player 1's Delete, deleting rows): `DELETE
  /api/markers/{id}?videos=unlink|keep` and `DELETE /api/rows/{id}?videos=…` (default unlink = as before). `keep` sets
  `VodMarkers.RemovedAtUtc` (migration `KeptSegments`): the segment leaves its timeline, lists, copies and segment exports but stays in
  its videos (video DTO segments carry `removed`); `POST /api/markers/{id}/restore` puts it back (the page's undo). It may stay in a
  video's `PUT` (already linked) but can't be linked anew, and is deleted for good once no video links it (`VideoEndpoints.DeleteEmptyAsync`).
  A video with no segments left is still deleted.
- **Copies** (`CopyEndpoints`, badrfly, 2026-11-15): others' segments/moments are editable only with `EditAnyEntries` (as before), clips with
  `EditAnyEntries`; otherwise members copy them onto their own row (`AddEntries`): `POST /api/markers/{id}/copy`, `POST /api/entries/{id}/copy`
  (a Ready stored clip's file is copied, otherwise it downloads again; Twitch clip entries aren't copied) and `POST /api/entries/{id}/copy-as-segment`
  (Alt+drag of a clip onto a video row: a segment at exactly its times, labeled with its title). Copies remember their source
  (`VodMarkers.CopiedFromMarkerId` / `CopiedFromEntryId`, `LibraryEntries.CopiedFromEntryId`, no FK). Members with `EditAnyEntries` choose
  "Edit original" or "Make a copy" on the page.
- **Cut-out parts** (`CutOuts`, badrfly, 2026-11-15): ranges removed from a segment (`VodMarkers.Cuts`), a VOD clip (`LibraryEntries.Cuts`),
  or a segment in one video only (`VideoSegments.Cuts`, on top of the segment's own), stored as JSON in **VOD seconds** (null = none). Always read
  through `CutOuts.Normalize` against the item's current range (clipped, merged, slivers under 0.5 s cut too), so a trim just drops what falls
  outside; endpoints also store them normalized. Requests take `cuts: [{ start, end }]` (marker POST/PUT, `POST /api/entries`, `PUT /trim`;
  on PUT null keeps them, [] removes them); something must be left (400). Moments have none. **Lengths count what's left**: `ClipLengthSeconds`,
  `keptSeconds` on markers, video `lengthSeconds`, the segment-download time limit, but **not the stored-clip limit**, which counts the whole
  range (badrfly 2026-11-16, reversing 2026-11-15). **Files made from them skip them**: `KeptPartsCutter` cuts each kept part with `IVodCutter`
  (Exact, saved audio) and joins them with `SegmentJoiner` (exports and segment downloads; a segment download item copies the cuts when picked).
  **An entry's own file (stored or generated) holds its whole range** since 2026-11-16, so changing only its cut-out parts (`PUT /trim`, apply,
  revert) keeps the file and downloads nothing (`EntryEndpoints.RecutAsync`; audit says "same file"); the page skips them in preview.
  `LibraryEntries.FileCuts` = what an older file leaves out (migration `ItemEdits` set it from `Cuts`; such a file is made again on the next
  cut change); entry and timeline JSON have `fileHasCuts` / `fileCuts`. Renders work from the entry's file. Copies keep them.
- `ItemEdits` + `ItemApplies` (`Data/ItemEdit.cs`, `ItemEditEndpoints`, badrfly 2026-11-16, spec `rework/player2-edits-spec.md` in project files):
  **saved edits** of the old Player 2 (the page no longer makes them; each becomes a project at startup, see Saved edits under Editor), reference-only versions of a clip (`EntryId`), segment (`MarkerId`) or video (`VideoId`), FK cascade with the item,
  team + hidden-VOD filter. An edit keeps what it removes from the item's **full** range (trimmed ends included) in VOD seconds: `Cuts`, or for a
  video `Parts` `[{ markerId, cuts }]` (a segment may be cut out whole; a video edit cuts on top of the segments' own cut-out parts), plus an
  optional `Layout` JSON for the vertical editor. Cuts touching the item's start/end are stored open-ended (`ItemEdits.ForStoring`: 0 /
  `OpenEnd`), so a longer item keeps the edit's ends and a shorter one cuts it down to fit. Many per item (max 50 per member); applying one
  changes no other. Edit own (`AddEntries`), delete own or anyone's (`DeleteAnyEntries`), copy anyone's. **Apply** sets the item's cut-out
  parts (`LibraryEntries.Cuts`, `VodMarkers.Cuts`, `VideoSegments.Cuts`) and records an `ItemApplies` row (cuts before/after; the oldest row
  and the newest 19 kept); without permission to change the item it's copied onto the caller's row first (`CopyEndpoints.CopyEntryAsync` /
  `CopyMarkerAsync`, `VideoEndpoints.CopyVideoAsync`) with the edit. **Revert** `previous` = the newest row's before, `original` = the oldest's.
  Audited `edit.added` / `edit.deleted` / `edit.applied` / `edit.reverted` (the last two count as "edited"); PUTs aren't audited (the page
  saves as you work). Twitch clips and moments have none (409). **Exports** take `kind: "edit"` (`editId`; `ExportItems.EditId`, migration
  `ExportItemEdits`): made as the edit's item (Clip / ClipVertical / Segment / Video) with the edit's cuts or parts copied; file name = the
  edit's title, else the item's. A clip edit's vertical is a render made for that edit; a plain clip's vertical only renders made without one.
  **Clips are cut from their stored file** when it holds the whole range at the item's quality and was cut Exact (`ExportWorker.CutFromFileAsync`:
  each kept part re-encoded like Exact trims, then joined), for edits and plain clips with cut-out parts alike. **Vertical renders of an edit**:
  `POST /shortform` with `editId` (body or query; must be an edit of that clip): the layout is cut down to what the edit keeps in file seconds
  (`ShortformLayout.KeepOnly`, `ItemEditEndpoints.ClipEditFilePartsAsync`), the render's json keeps `editId`, renders list `editId`, the state
  `renderingEditId`; a render without `editId` is cut down the same way to what the clip's own cut-out parts keep (`ItemEditEndpoints.ClipOwnFileParts`, since the file holds the whole clip); the newest 5 are kept per edit (and for the clip itself), and "layout" in `GET /shortform` is the newest clip render's.
- `Projects` (`EditProject`) + `ProjectVods` (`Data/EditProject.cs`, `ProjectEndpoints`, badrfly 2026-11-16, plan: Claude Doc "Vertical editor:
  multi-track timeline plan"): **projects** for the multi-track Player 2 (Player 1 = overview for marking; Player 2 = a real video editor whose media
  are references into Twitch VODs). `Content` = one JSON document (`ProjectContent`): `tracks` (top to bottom; `id`, `name`, `hidden`, `muted`,
  `locked`; ≤ 20) and `pieces` (≤ 200: `id`, `trackId`, `at` = seconds on the project's clock from 0:00, `vodId`, `start`/`end` = VOD seconds
  (≥ 0.5 s), `source: { kind: clip|segment|video|vod|edit, id }` (label only: pieces are **copies**, nothing follows the original), `label`,
  `color`, `layout: { mode, top, bottom?, splitRatio?, keys?: [{ time (VOD seconds), top, bottom? }] }`). Pieces on one track never overlap;
  where tracks overlap the higher one plays and renders, lower ones are covered and silent; gaps are black. Nothing ends after 3 h. The page
  saves the whole content (`PUT`, unaudited) with the `revision` it last saw: a newer one → 409 `code: "project-changed"` with the project.
  **The VOD is the project** (badrfly 2026-11-17, thread "Make each VOD the project"): the page never says "project"; a project is an **edit**
  that belongs to one VOD (`Projects.VodId`) and to the clip (`EntryId`) or video (`VideoId`) it's an edit of (FKs SetNull: deleting the clip
  leaves its edits on the VOD). `POST /api/projects` takes `entryId` / `videoId` / `vodId` (else read from the first piece). Pieces may come
  from **other VODs the team can open** since "Sync with" (badrfly 2026-11-20, see below; it used to be 400); the edit still belongs to its own VOD. Copies keep the parent. `ProjectParents.AssignAsync` (startup,
  after `SavedEditConversion`) places older projects once: a converted saved edit under its item, else under what its first piece was copied
  from (a segment or VOD range: on the VOD itself). A VOD counts as worked on once something is on its timeline: `GET /api/vods/work`.
  `ProjectVods` (rebuilt on save) feed the hidden-VOD filter (a project using any hidden VOD is hidden whole) and the revoke search; every
  VOD newly added to a project must be open to the team (403 `channel-not-assigned`). Copies are new work (`CopiedFromProjectId`).
  **Horizontal or vertical** (badrfly 2026-11-17, thread "Separate the vertical editor from Player 2"): `Projects.Vertical` (POST `vertical`,
  fixed once made, copies keep it, `GET /api/projects?vertical=`). A horizontal edit (Player 2) keeps **no layouts** (dropped on save) and
  exports landscape; a vertical edit opens in the vertical editor, keeps boxes/keyframes and exports 9:16 (export `horizontal`/`vertical`
  flags are ignored for projects: the kind decides). Migration `VerticalEdits` made every project with a layout vertical.
  **Teammates' horizontal edits are open to the whole team** (badrfly 2026-11-17: anyone can open a clip someone cut and make their own
  vertical edit from it): listed/opened with `ViewLibrary`, copied with `AddEntries`, exported with `GenerateFiles`, changed in place with
  `EditAnyEntries`, deleted with `DeleteAnyEntries` (`ProjectEndpoints.CanSee` etc.; `editedBy` filters and `/api/vods/work` count them too).
  **Vertical edits follow `VerticalAccess`** (badrfly 2026-11-17): own ones need `AddEntries`; a teammate's are listed/opened only with
  `ViewTeamVerticals` (bit 19) or `DownloadTeamVerticals` (bit 18, includes seeing), else 404 / left out of lists; exported (`kind: "project"`,
  400 otherwise) with `DownloadTeamVerticals`; changed or copied with seeing + `EditAnyEntries`; deleted with seeing + `DeleteAnyEntries`.
  DTOs carry `canCopy` / `canDownload`. The same rule covers 9:16 renders (`/shortform`: list filtered, inline play = see, download = Download,
  delete with `DeleteRenders` only of ones they see; renders with no `createdByUserId` count as a teammate's). Support view sees everything. Audited `project.added` / `project.deleted`.
  **Edits follow Player 1** (badrfly 2026-11-17, `ProjectSourceSync`, hooked into `LibraryDbContext.SaveChangesAsync`): when a VOD clip's or
  segment's kept range changes (trim, cut-out parts, P1 undo, apply/revert, live markers entry), every team edit with pieces whose `source` is
  it (a segment's videos too: pieces of the video within the segment's old range, minus the video's own cut-out parts) follows: removed parts
  leave the pieces (split around a cut; later pieces on the same row slide left, gaps the member left stay), parts taken back grow the piece
  ending/starting at that old end, and an inner part (a cut taken back) only re-joins two pieces still next to each other. A range moved
  with nothing in common is ignored; results that wouldn't validate are skipped. `Revision` goes up. The rules go to the page in the
  `X-Source-Sync` header (core.js `api` fires `clipco:source-sync`); `ui/project.js` `applySourceRule` mirrors the server, and
  `ProjectStore.followSources` applies it to the open edit (or loaded original) and every undo/redo step, so a Player 2 undo never brings
  back what Player 1 took out. Player 2's own changes stay undoable only in Player 2.
  **Undo across reloads**: `ProjectStore` keeps the open edit's undo/redo steps in `sessionStorage` `clipco.p2.history` (this tab) with the
  revision they belong to; reopening that edit at that revision (or the next, when a save was sent as the page closed) restores them. A
  loaded original changed just before closing is saved as a new edit while closing: `restoreLastLoaded` finds it (`ProjectStore.pendingDraft`).
  **Rendering** (`Services/Projects/`): `ProjectTimeline.Flatten` = what plays, in order: the highest visible track's piece at every moment,
  else a gap (stretches under 0.1 s join the one before). `ProjectRenderer` cuts each stretch from its VOD with `IVodCutter` (Exact, saved
  audio; the export's quality where a VOD has it), encodes every stretch to one format (`OutputFormat`: 9:16 = 1080x1920 with the piece's
  layout via `ShortformLayout.BuildFilterGraph`, keyframe times moved to the stretch's start, no layout = a centered 9:16 box; landscape =
  the quality's height (1080 for best), letterboxed; fps from the quality, 60 for best; AAC 48 kHz stereo; muted track = silence), makes gaps
  black with silence, and joins them with `SegmentJoiner` (stream copy). Exports take `kind: "project"` (`projectId`, `horizontal`/`vertical`,
  `quality`, `separate`: one file per piece it plays, gaps left out, in `downloads/exports/{id}/{position}/` and served as a .zip named
  "01 label.mp4"…, `ExportItems.Separate`, migration `ExportSeparate`, badrfly 2026-11-20; the Export tab's "Separate files"): items `Project` / `ProjectVertical` (`ExportItems.ProjectId`, content copied into `Parts`), progress = stretches done.
- `ExportBatches` + `ExportItems` (cascade): **exports** (badrfly, 2026-11-15, the Export tab's list; `ExportEndpoints`, `ExportWorker`). One
  `POST /api/exports` (`GenerateFiles`) with items of kind `vod` (a VOD range dropped on the list: **a one-off, never an entry**), `clip`
  (horizontal and/or vertical: two items), `video` (a `.zip` of one file per segment, or `joined` into one file, which needs the team's
  `CombineSegmentDownloads`, 403 `combine-not-enabled`) or `segment`; each with its own `quality` and `fileName`. What an item is made of is
  copied at request time (ranges, cut-out parts, a video's segments in `Parts` JSON, the vertical `RenderId`). The worker (one export at a
  time, separate from the other queues) makes items in list order with `KeptPartsCutter`; a **stored clip at its own quality is copied**
  (no download), so is a vertical render; one failing item doesn't stop the rest. Progress: `partsDone/partsTotal` per item, `itemsDone`
  per export (the page polls). **Step progress** (badrfly 2026-11-17: a 3 h export sat at "Making files" with a silent console for an hour):
  the worker makes a `JobProgress` per item the ambient one (`JobProgress.Use`, AsyncLocal; in memory, `IdJobQueue.ProgressOf`), Core's
  download (`LoggerTaskProgress`, steps in plain words) and every `FfmpegRunner.RunAsync` given a `step:` report into it (ffmpeg's stderr
  `time=`/`speed=` vs `expectedSeconds` or the first input's `Duration:`), and log start / every 5 % (or minute) with speed and time left / done.
  Items carry `progress: { step, percent, etaSeconds }` while Running; the export card shows it. Give any new long ffmpeg run a `step:`. Files in `downloads/exports/{id}/{position}.mp4` (a .zip video: folder `{position}/`, zipped when served);
  they expire after `Downloads:GeneratedFileHours`, cleaned like segment downloads; disk counts for the member. Limits: `Downloads:MaxExportItems`
  (50), `MaxExportSeconds` (3 h, verticals not counted), `MaxActiveExportsPerTeam` (3). `GET /api/exports` = the caller's own. Twitch clip
  entries aren't exported (their card's download still works). Audited `export.requested` ("generated") / `export.ready` / `export.failed` /
  `export.deleted` / `export.expired`. `ExportQueue` and `SegmentExportQueue` share `IdJobQueue`.
- `SegmentExports` + `SegmentExportItems` (cascade): **segment downloads** (badrfly, 2026-11-14). Members pick team segments on a card's strip/chips
  and download them: **one file per segment** (default, every team) or also **joined into one file** (`Combined`, only for teams where the app
  Owner turned on `Teams.CombineSegmentDownloads`, off by default; `PUT /api/admin/teams/{id}/features`, `RequireStaffOwner`). Segments **never
  become library entries**. Each item copies the segment's label/range at request time (`MarkerId` has no FK). `EntryId` = the card it was picked on
  (title + quality for the files; no FK). Team query filter; `TeamId`/`CreatedByUserId` filled by `SaveChanges`. Audited `segments.requested`
  (counts as "generated" activity) / `segments.ready` / `segments.failed` / `segments.deleted` / `segments.expired`, `team.features_changed`.
- `ErrorReports` (`ErrorReport`, badrfly 2026-11-18): **bugs, errors and crashes** for the Admin page's **Reports** tab (staff permission
  `ViewErrorReports`, bit 8; the Owner always). Kinds: `ServerError` (any Error log line during a request, crashes included: ASP.NET's
  exception handler logs them), `JobError` (an Error log line from a background job), `PageError` (a page's script error, unhandled promise
  rejection, or app script that didn't load) and `BugReport` ("Report a problem" in the editor's account menu). Each keeps who (user, team,
  staff; **names copied** so they outlive the account), when, where (`Area`: "GET /api/entries/{id}", the job's class, or the page path),
  error type, message, details (stack/log line), the person's note, status, URL (never the fragment: link tokens), browser + system
  (`UserAgentInfo`) and user agent, VOD/entry/export/segment download IDs, the page's context JSON, app version (git commit from the
  assembly's informational version) and a `Fingerprint` (kind + type + where + message with numbers/IDs/quoted text taken out) that groups
  repeats (bug reports never group). `ErrorReportLoggerProvider` (an `ILoggerProvider`) turns Error/Critical log lines into reports
  (skipping EF Core's and the writer's own: no loops); for jobs it reads `EntryId` / `VodId` / `BatchId` / `ExportId` from the log line's
  values and `ErrorReportWriter` (BackgroundService, batches) attributes the report to the entry's adder or the export's creator. So **log
  job failures with those named values**. At most 120 a minute (`ErrorReportSink`; the rest counted in one report). Everything is passed
  through `ReportScrubber.Clean` (token=/sig=/password=…, OAuth/Bearer headers, Twitch CDN/usher URLs cut to host + file name, JWT-like
  strings): **never put tokens in a log line anyway**. Pages: `wwwroot/reports.js` (loaded first on every page) sends crashes (≤ 15 per page
  load, the same one once a minute; `ResizeObserver` noise and cross-origin "Script error." skipped) and bug reports to
  `POST /api/auth/reports` (open, rate limited 30 per 10 min per IP, so crashes before login count) with context: page, screen, time zone,
  what the editor has open (`clipcoReports.setContext` in `ui/editor.js`: screen, active player, Player 1's VOD and position, Player 2's edit,
  row focus, player kind) and the last 30 breadcrumbs (`api` calls with status and time from `core.js` / `account.js`, console errors,
  screen changes). Audited `reports.downloaded` / `reports.deleted`.
- `TeamStatuses` + `TeamTags` + `EntryTags` / `VideoTags` (`Data/TeamStatus.cs`, `Data/ItemStatuses.cs`, `StatusEndpoints`, badrfly 2026-11-20, step 4 of Large libraries, migration `TeamStatuses`): a team's own **statuses** for clips and videos (`LibraryEntries.TeamStatusId` / `Videos.TeamStatusId`, FK SetNull; at most one per item): name (≤ 40), color (`VodMarker.Colors`), `Position`, `SetWhen` (`StatusTrigger`: ByHand / NewItem / EditMade (a horizontal edit made) / VerticalEditMade / Exported; at most one status per trigger) and `HideFromLists`. Made on the Team page's Statuses card by the team owner or `ManageStatuses` (bit 20); at most 12. A team gets To do / Editing / Ready / Posted the first time they're listed (`ItemStatuses.ListAsync`, once: `Teams.StatusesMadeAtUtc`). Set-by-itself statuses only move an item **forward** (`ItemStatuses.AdvanceAsync`, from `ProjectEndpoints` create/copy and `ExportEndpoints`); new entries/videos get the NewItem one in `FillOwnership` (copies too); items from before have none. Deleting a status moves its items to `?moveTo=` or none. **Tags**: lower case, no '#', ≤ 30, ≤ 10 per item, made the first time a member uses one (unique per team); members set status and tags with `AddEntries`. Audited `status.changed` / `tags.changed` (count as "edited") and `statuses.changed`.
- `MemberMarks` (`Data/MemberMark.cs`, `MarkEndpoints`, badrfly 2026-11-20, step 5 of Large libraries, migration `MemberMarks`): a member's **own** stars and recently opened items, per team (unique TeamId+UserId+Kind+ItemId; `Kind` Clip / Video / Vod, `ItemId` = entry / video / VOD ID, no FK, so marks of deleted items are left out when read): `StarredAtUtc`, `OpenedAtUtc` (the newest `MaxRecent` = 50 per kind are kept). Nobody else sees them; viewing the library is enough to set them. Not audited.
- A converter marks every `DateTime` as UTC when read back (SQLite has no date type) — keep all times UTC.
- SQLite runs in **WAL mode** (set at startup, stored in the file): copy `clipco.db` together with `clipco.db-wal`/`-shm`, or stop the app first.
  `Projects.Revision` is a concurrency token (two saves of one revision: the second gets 409 `project-changed`).
- Migrations: `cd TwitchDownloaderWeb && dotnet tool restore && dotnet ef migrations add <Name> -o Data/Migrations`.
  They are applied automatically at startup.

### Accounts and teams (`Services/Accounts/`, phase 3)
Plan and badrfly's decisions: Claude Doc "Logins, teams and permissions plan". Built in steps: 1 = logins, teams, links, scoping;
2 = roles and permissions, activity, team audio setting, per-user time zone, password change/reset; 3 = app moderators, view as support, staff tools.
- **Two separate structures** (badrfly's call): team users (`Users`, `Teams`, `TeamMembers`) and app staff (`StaffAccounts`: the app Owner
  and later app moderators). Separate tables, cookies (`clipco.user` / `clipco.staff`, HttpOnly, SameSite=Strict, 30 days sliding) and
  login pages (`login.html` / `admin.html`). A staff login is never a team login or the other way round. badrfly has one of each.
- **One account, many teams**: `TeamMembers` (unique TeamId+UserId). The page's team switcher sets the `clipco.team` cookie
  (`POST /api/auth/team`); without it the first team joined is used.
- **Team channels** (badrfly, 2026-11-15, for the frontend rework): members can no longer add channels, VOD links or Twitch clip links.
  **A new team is locked to one channel** (badrfly 2026-11-19): a team registration link needs exactly one Twitch channel (`channel`,
  checked on Twitch when the link is made, 400 when blank or more than one; stored in `AccountLinks.ChannelsJson`, added when it's used)
  and suggests a name after it: "{display name}'s Team" (`AdminEndpoints.DefaultTeamName`, stored in `AccountLinks.TeamName`; staff
  don't pick it). The join page shows it grayed out with a ✏️ button; changing it there (`teamName` on accept, different from the
  suggestion) counts as the team's rename. **Teams rename themselves once every 14 days** (`AccountRules.TeamRenameDays`,
  `Teams.NameChangedAtUtc`; keeping the suggestion doesn't start the wait): the team owner on the Team page (`PUT /api/team/name`, 409
  `code: "rename-too-soon"` with `nextRenameAtUtc`); the app Owner any time (`PUT /api/admin/teams/{id}/name`, `RequireStaffOwner`, leaves
  the team's wait alone). Audited `team.renamed`. **Staff see teams as "Channel (team name)"** (`TeamLabels`: the first channel still
  assigned, else the first; `label` on admin team DTOs, and the team names in admin users/activity/app log). Older links without a
  suggestion still take the typed name. Owner links carry no channels (400). **Only the app Owner gives a team more channels**, on the Admin page's Teams tab (`POST /api/admin/teams/{id}/channels`,
  `RequireStaffOwner`, also "Assign again"); `StaffPermission.ManageTeamChannels` ("Remove team channels") only takes them away.
  Audited `channel.added`/`channel.removed`. `TeamChannelAccess` decides which VODs a team may open,
  **strictly**: the VOD's channel (GQL owner, cached 12 h; matched by Twitch user ID, then login) must be assigned; work already on a VOD
  doesn't open it. Exception (badrfly 2026-11-16): a VOD **Twitch no longer has** stays open when the team's entries/videos on it name one of
  its assigned channels (`ChannelName`, login or display name), so stored clips stay exportable after the VOD is deleted (an export
  then uses the stored file's quality whatever was asked; `ExportWorker` cuts its kept parts out of the stored file when the VOD is gone,
  for any Exact file, mapping past an older file's `FileCuts` with `ExportWorker.FileParts`; Safe files can't be, their offset needs the playlist). Removing a channel takes a mode (badrfly): `?mode=revoke` deletes the row (every VOD closes now, work included) **and hides
  the team's work on its VODs** (badrfly 2026-11-15, "Hide it all": `RevokedChannels` + `HiddenVods`, the VODs the team had entries, markers,
  rows, videos or exports on, found per VOD via Twitch (`TeamChannelAccess.GetVodChannelAsync`; a VOD gone from Twitch goes by the channel
  name on its entries/videos; Twitch unreachable → 502, nothing changes). The team query filters on entries, markers, rows, videos, segment
  downloads and exports leave out hidden VODs (an export with any hidden item is hidden whole; old Twitch clips with `VodId` 0 by
  `ChannelName`), so lists drop them and by-ID calls 404; background jobs still see them. Nothing is deleted: assigning the channel again
  (POST or a link, `AdminEndpoints.Unhide`) deletes the `RevokedChannels` row and its `HiddenVods`. `GET /api/admin/teams` lists
  `revokedChannels` (Admin page: "Revoked" with an Assign again button)) and
  `?mode=future` sets `SavedChannel.AccessEndedAtUtc` (no new VODs and gone from the team's lists and audio watching; VODs the team has
  entries or markers on stay open). Assigning it again (POST or a link) clears it. Every team endpoint taking a VOD ID from the
  client declares `.RequireChannelVod()` (route `{vodId}`) or checks in the handler (`/api/vods/info`, `POST /api/entries`, channel VOD
  lists); refused → 403 `code: "channel-not-assigned"`. Answers are cached 30 s per team+VOD; a channel change clears them
  (`TeamChannelAccess.Forget`). `TeamPermission.ManageChannels` is no longer used (kept so the bit isn't reused, not in the catalog).
  A VOD of a channel the Owner took away (revoked, or "Stop new VODs" on a VOD with no work) is refused with the same code plus
  `revoked: true` ("Your team's access to this channel was revoked.", `VodAccess.Revoked`). **The page asks `GET /api/vods/{id}/access`
  (204 or the refusal) before every player load** (`MediaPlayer.open` → `checkVodAccess` in `ui/media.js`; `loadInPlayer2` too) and never
  falls back to the Twitch embed on that code (the embed doesn't ask the server): the player empties to its drop hint with that note
  (`unloadPlayer1` / `refusePlayer2` in `ui/editor.js`, badrfly 2026-11-20). `Forget` also raises `TeamChannelsChanged(teamId)`, which
  `/api/live/events` sends as `channels` `{ logins }` (core.js `clipco:channels`): the library re-reads its channels and lists, and
  the editor unloads a loaded VOD/edit the team can no longer open, without a refresh.
  Twitch clip links → 403 `code: "clip-links-disabled"`; clip entries added before stay.
- **Every `/api` request needs a team login** except `/api/auth/*`, `/api/links/*` and `/api/admin/*` (staff endpoints: see Staff below). `TeamScope` middleware (after `UseAuthentication`) checks membership, stores a `TeamContext`
  (bind it as an endpoint parameter) and fills the scoped `RequestActor`.
- **Team scoping is a global query filter** on `LibraryEntries` and `Channels` (`TeamId == actor team`, active whenever `RequestActor.IsRequest`;
  a request without a resolved team sees nothing). Background jobs have no request, so they see all teams. `SaveChanges` fills
  `TeamId`/`AddedByUserId` on new entries/channels and `UserId`/`TeamId`/`StaffId` on audit events from the actor (entry events from
  background jobs get their entry's team). Use `IgnoreQueryFilters()` only in staff code. Saved audio (`AudioRescues`) stays shared per VOD.
- **Roles and permissions** (`Data/TeamPermissions.cs`): the team owner makes roles (`TeamRoles`, unique name per team) from the
  `TeamPermission` flags (stored as a bit mask: never renumber, add at the end) and gives each member one (`TeamMembers.RoleId`, null =
  no access). The owner has `All`. Members always join with no role; invite links never carry one (badrfly: security). A member with
  no permissions gets 403 `code: "no-access"` everywhere ("Waiting for access" on the page).
- **Every team endpoint declares its permission** with `.RequireTeam(TeamPermission.X)` (any one of the flags) or `.RequireTeamOwner()`;
  `TeamScope` checks it and **refuses endpoints that declare nothing** (fail closed), with 403 `code: "forbidden"`. Own/any pairs
  (EditOwn/AnyEntries, DeleteOwn/AnyEntries) are checked in the handler with `TeamContext.CanOnOwnOrAny(entry.AddedByUserId, ...)`.
  Viewing (`ViewLibrary`) covers playback, VOD browsing, info lookups, files and renders; the phase 1 dev endpoint is owner-only.
- **Guard rails** (so a "Moderator" role can't take over): members can only create/edit roles and give roles whose permissions they hold
  themselves, never change their own role or the owner, and only manage (role, remove, reset) members whose role is within theirs.
  Deleting a role leaves its members with no role.
- **Activity** (`ViewActivity`): `GET /api/team/activity?days=` counts each member's audited actions in the team (added, edited,
  deleted, downloaded, generated, vertical) plus entries added and disk used now by their entries' files and renders;
  `GET /api/team/audit?userId=&before=` is the team log. `shortform.requested`/`shortform.deleted` are audited by the endpoint (the
  render job doesn't know the user).
- **Audio saving is the app Owner's call, per team channel** (badrfly 2026-11-18; it used to be the team setting `Teams.AutoSaveAudio`,
  now unused, and `ManageAudioSettings`, kept but out of the catalog). Admin → Teams → Channels has "Save audio now" (`AllowSaveAudio`)
  and "Save automatically" (`WatchMutedAudio`) per channel (`PUT /api/admin/teams/{id}/channels/{channelId}/audio { saveAudio?, autoSaveAudio? }`,
  `RequireStaffOwner`, audited `channel.audio_changed`). A channel is watched when its row has automatic saving on (enabled team); rescues not
  started by hand run only while such a row exists (`AudioRescueService.AutoWatchedLogins`, across all teams). `POST /api/vods/{id}/rescue`
  needs the `SaveAudio` permission **and** the VOD's channel row allowing it (either flag; `TeamChannelAccess.FindVodChannelAsync`), else 403
  `code: "audio-not-allowed"`; the page hides the button (`/api/channels` `saveAudio`). The Team page only shows what the Owner set
  (`GET /api/team/settings`). `AudioRescue:Enabled` is the app-wide kill switch (default true).
- **Staff** (`Data/StaffPermissions.cs`, `StaffScope`): the Owner (`IsOwner`) has everything and alone manages moderators
  (`/api/admin/staff*`: invite links `StaffInvite`, permissions, disable, `StaffPasswordReset` links; never the Owner account). Moderators
  join with **no permissions** (same rule as team invites) and get `StaffPermission` flags (bit mask on `StaffAccounts.Permissions`: never
  renumber). `StaffScope` is middleware (after `UseTeamScope`; parameters are bound before endpoint filters run) that checks the staff
  cookie and each `/api/admin` endpoint's `.RequireStaff(...)` / `.RequireStaffOwner()` / `.RequireAnyStaff()`; open ones are
  `.AllowWithoutStaff()`; anything else is refused (fail closed). Bind `StaffContext` as a parameter. Staff tools: teams (disk, last active,
  members' activity), users (teams, role, activity, disk across teams), disable users/teams (disabling a user also rotates the stamp), password
  reset links for any user (including multi-team), app log, own password change.
- **View as support** (`SupportView`, permission `ViewAsSupport`, the Owner turns it on per moderator): `POST /api/admin/support { teamId }`
  sets the `clipco.support` cookie (12 h). While it's there, `TeamScope` acts as that team **before** looking at the user login, with only
  `ViewLibrary | ViewActivity`, `UserId` 0 and `TeamContext.SupportStaffId` set, and refuses anything but GET/HEAD (`code: "read-only"`).
  The staff login and permission are re-checked every request (`code: "support-ended"` when gone). `/api/auth/me` returns `support` and
  the library/Team pages show a banner with Exit (`DELETE /api/admin/support`, works without a staff login). Start/end are audited; events in
  a visit carry `StaffId` and no user, so they never count as member activity, and the **team log leaves out staff events** (`StaffId != null`).
- `ActivityReport` computes per team+user counts, entries added and disk use for both the Team page and the Admin page.
- Per-user time zone (`Users.TimeZone`, `PUT /api/auth/settings`), still cached in `localStorage` for the other pages. `POST /api/auth/password`
  (current password needed) and `PasswordReset` links (24 h, `ResetPasswords`) set a new security stamp, which signs out other sessions.
  A team can only reset members who are in no other team (the account is shared; staff handle the rest, step 3).
- **One-time links** (`AccountLinks`): `TeamRegistration` (staff; new team, opener = owner), `TeamOwnership` (staff; opener becomes owner of
  an existing team: claiming "Main team", hand-over, recovery), `TeamInvite` (team owner). 7 days, revocable, claimed with a conditional
  update so they can't be used twice. Only the token's SHA-256 is stored; the token is in the URL **fragment** and sent in request bodies
  only, so it never reaches server logs. Team users' links are `/#join=token` (the start screen shows the form; `LinkUrls.Build`), staff
  links `/join.html#token`; older team links sent as `/join.html#…` forward to `/#join=…`. Opening a link while logged in offers "Continue as …".
- **Owner setup**: `admin.html` while no staff account exists, only from loopback without `X-Forwarded-For` (open the page as
  `http://localhost:5254/admin.html` on the machine running the app). Set it up before exposing the app.
- The accounts migration put all existing entries/channels/entry audit events in team 1 **"Main team"** with no owner; the Owner makes an
  owner link for it on the Admin page.
- Passwords: ASP.NET Core `PasswordHasher` (PBKDF2), 10–200 chars. 5 wrong passwords lock an account for 15 min; login/link endpoints are
  rate limited per IP (`Auth:LoginAttemptsPerMinute`, default 10). Changing `SecurityStamp` signs out every session of the account.
  `LastActiveAtUtc` is updated at most every 2 min. Never audit or log passwords, link tokens or cookies.
- Tests: `AccountsTests` runs the real pipeline with `WebApplicationFactory<Program>` (temp DB).

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
- **Smart cuts** (badrfly 2026-11-20, app-wide: `Services/SmartCut.cs`, `Downloads:SmartCut`, default true): every Exact cut goes through
  `ExactTrimService.TrimAsync`, which re-encodes only the **edges**: from the cut's first frame to the first keyframe (head) and from the
  last keyframe to the end (tail), libx264 with the Exact preset/CRF; the middle is **copied** (segment muxer split at the two keyframes).
  Keyframes and frame times come from a stream-copy `framecrc` probe of ±12 s around each edge (no ffprobe). Parts are MPEG-TS (settings
  in every keyframe, so copied and re-encoded video can sit in one mp4) joined by the concat demuxer with `duration` lines; audio is
  re-encoded once from the first frame (so sync never depends on the parts). The cut starts on the first frame at or after the start.
  Cuts under 20 s, non-H.264 video (e.g. HEVC renditions), a middle under 8 s or a middle of the wrong length re-encode the whole cut
  (`ReencodeAsync`, the old path); a failure logs a warning and does the same. Files joined later by `SegmentJoiner` still stream-copy.
  Tests: `SmartCutTests` (flashes + beeps in head, middle and tail; every frame vs the source by PSNR; joins). Checked to play in Chromium.
- `VodInfoService` (Twitch lookups + shared validation), `VodDownloadRunner` (builds `VideoDownloadOptions`),
  `LoggerTaskProgress` (Core `ITaskProgress` → `ILogger`), `TrimTime` (parses `h:mm:ss`/`m:ss`/seconds with
  unbounded hours; TimeSpan's own format caps hours at 23).

### Segment downloads (`Services/Segments/`)
- `SegmentExportQueue` + `SegmentExportWorker` (BackgroundService, one export at a time, separate from `DownloadWorker`). Each segment is cut by
  `IVodCutter` (`VodCutService`, the same path library entries use: saved clean audio swapped into muted parts, Exact re-encode) into
  `downloads/segments/{exportId}/{position}.mp4`; positions follow start time. One failing segment doesn't stop the others. Combined: if all
  segments worked, `SegmentJoiner` joins them with ffmpeg's concat demuxer (`-c copy`, the files share codec settings; re-encodes if copy fails)
  into `combined.mp4`; if any failed, the files that worked stay and `Error` says it wasn't joined. Interrupted exports are re-queued at startup and
  keep segments already made. Files expire after `Downloads:GeneratedFileHours`; `LibraryCleanupService` deletes expired exports (row + folder)
  and orphaned folders in `segments/`. Disk use counts for the team and the member who started it (`ActivityReport`).
- Limits (`Downloads:`): `MaxSegmentsPerDownload` (50), `MaxSegmentDownloadSeconds` (3 h, all segments together), `MaxActiveSegmentDownloadsPerTeam` (3).
  A segment ending after what a live VOD has streamed is refused (400). `IVodQualitySource` (VodInfoService) checks quality/restricted; faked in tests.
- Page: the old library page picked segments on its cards for this; that page was removed in frontend rework step 10 and the editor's
  Export tab sends segments and videos through `/api/exports` instead. The endpoints stay (no page uses them now). The Admin page's Teams
  tab has "Allow joined segments" (Owner only), which also gates joined videos in exports.

### Live stream updates (`Services/Live/`, badrfly 2026-11-18)
Pages hear the moment a team's channel starts or stops streaming, without a refresh and **without checking on a timer**.
- `EventSubWatcher` (BackgroundService): Twitch **EventSub over WebSocket** (`wss://eventsub.wss.twitch.tv/ws`, no public address needed, so
  it works on a PC and in a desktop wrapper) with `stream.online` + `stream.offline` for every channel assigned to an enabled team (once each,
  most-shared first). Twitch allows subscriptions costing **10 per Twitch sign-in** (each costs 1): 5 channels; the rest are "not watched"
  and the page looks for itself as before. Handles keepalive (no message in keepalive + 10 s = lost), `session_reconnect` (new socket
  first, subscriptions carry over), revocation, backoff (5 s doubling to 5 min). Events are lost while disconnected, so **after every
  (re)connect one Helix "Get Streams" call** brings the state up to date. A channel change (`TeamChannelAccess.Forget` raises
  `ChannelsChanged`; team disable calls it too) or a sign-in change restarts the session with the new set.
- `TwitchUserAuth`: WebSocket subscriptions need a **user** access token from a registered Twitch app. The Owner enters the app's Client ID
  (Public client, no secret; `TwitchConnections` row 1, else `Twitch:ClientId`, which holds badrfly's app "clipco local": Client IDs aren't secret) and signs in with the **device code flow** on the Admin
  page's Twitch tab (code typed at twitch.tv/activate; `Twitch:Scopes`, default none). The refresh token (single-use, expires after 30 days
  unused) is stored encrypted with data protection; access tokens stay in memory. A refused refresh sets `Problem` ("connect again").
  Audited `twitch.connected` / `twitch.disconnected`. **Never log or audit the tokens.**
- `LiveStreamHub`: in-memory state per login + fan-out; `Apply` only passes on real changes (repeats and the catch-up agreeing are dropped).
  A webhook transport (shared public server) would call `Apply` too; `EventSubMessages.ReadStreamChange` reads both message shapes.
  `Changed` → the audio rescue looks for the new VOD ~30 s after a watched channel goes live (`AudioRescueService.DiscoverSoon`).
- `GET /api/live/events` (`ViewLibrary`): server-sent events. `hello` `{ watching, channels: [{ login, live, startedAtUtc, watched }] }` on
  connect, then `stream` `{ login, live, startedAtUtc, atUtc }` for the team's own channels (re-read per event), `: ping` every 25 s.
  Page: `core.js` `startLiveEvents` (closed while the tab is hidden: HTTP/1.1 allows ~6 connections per server; the hello on reopening
  catches up) fires `clipco:live-hello` / `clipco:stream`; `library.js` re-reads the VOD list's first page (now, +20 s, +90 s: Twitch lists a
  new VOD and settles the length a little later), `checkLive` skips the lookup when the server says the channel is offline; `editor.js`
  re-reads a live VOD open in Player 1 (and Player 2's live VOD lengths) when it ends.
- Admin → Twitch (Owner only): `GET /api/admin/twitch`, `PUT .../client-id` `{ clientId }`, `POST .../connect` (starts the device code),
  `DELETE` (revokes and forgets the sign-in). The tab asks the server every 3 s only while a code is waiting.

### Muted audio rescue (`Services/AudioRescue/`)
Twitch mutes segments (`123-muted.ts`) and keeps the clean copy (`123.ts`) for a limited time (Core assumes 24 h and
only tries to unmute VODs <= 24 h old, silently keeping muted audio on 403). We save clean audio and splice it back:
- **Automatic saving is off by default, per channel, set by the app Owner** (it filled the dev disk, ~4.8 GB in 5 days for 2 channels;
  see Accounts: `SavedChannel.WatchMutedAudio` / `AllowSaveAudio`); `AudioRescue:Enabled` (default true) is only the app-wide kill switch.
  Without a channel row that has it on, a channel isn't watched and rescues not started by hand pause (no grab, no prune; VOD cards say
  "Audio saving paused"). The **Save audio now** button still works (user's call, 2026-11-02; permission `SaveAudio`): `POST` sets
  `ManuallyRequested`, which runs that rescue through grab and prune, and resumes it if it was paused. Saved audio is kept and still used for restores.
- `AudioRescueService` (BackgroundService, `AudioRescue:*` settings): every `PollMinutes` it checks saved channels with
  `WatchMutedAudio` for VODs in the unmute window; `POST /api/vods/{id}/rescue` starts one by hand. It downloads **every**
  segment of the audio-only rendition as raw `.ts` (clean URL for muted ones; 403/404 = unrecoverable) into
  `downloads/rescue/{vodId}/{key}.ts`, re-checks every `LiveRecheckMinutes` while the playlist has no `#EXT-X-ENDLIST`,
  then `PruneAfterHours` (48) after the stream ends keeps only segments that are muted in the playlist by then.
  The cleanup service never touches `rescue/`. Statuses: Grabbing / WaitingToPrune / Pruned / Failed / VodGone.
- Kept audio (Pruned/Failed rescues) is checked every `VodCheckHours` (12) via GQL video info (`IVodLookup`, not the playlist:
  sub-only VODs have no public playlist but still exist). Once the VOD has been missing for `VodGoneGraceDays` (14;
  `VodMissingSinceUtc`, reset when it reappears, since an unpublished VOD looks deleted and can be republished) its files and rows are deleted
  (status VodGone, audited `audio.rescue_removed`), even if library entries use the VOD (user's call, 2026-10-26: without the
  VOD nothing can use it; stored files already hold their audio). Runs even when `Enabled` is off. Prune no longer deletes everything when the playlist is missing.
  No VodGone badge on purpose: Twitch's VOD list drops deleted VODs, so their card disappears from the browser anyway.
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
| `GET /api/vods/info?vod=` | Title, length, stream date, thumbnail, qualities, `restricted`, `maxStoredClipSeconds`, `isLive` (the page shows "● LIVE" instead of date and length) |
| `POST /api/entries` | `{ vodId, title?, quality?, trimMode?, startTime?, endTime?, rowId? }` → 201 (`rowId`: one of the caller's clip rows on that VOD). VOD of the team's channels only; clip links → 403 `clip-links-disabled`. Validates VOD, times, quality (unknown → 400 listing available) |
| `GET /api/entries?search=&sort=dateAdded\|streamDate\|title\|length\|madeBy&order=desc\|asc&days=&limit=&from=&to=&dateField=dateAdded\|streamDate&vodId=&madeBy=&editedBy=&channels=&paged=&cursor=&minSeconds=&maxSeconds=` | `minSeconds`/`maxSeconds` = kept length (`ClipLengthSeconds`; the search box's `>1m`). `paged=true` answers `{ items, nextCursor, total }` (keyset paging: `cursor` = a page's `nextCursor`, "ticks_id" of the last row's sort date and ID, `PageCursor`; null = last page); without it the plain list as before. `channels` = comma-separated team channel logins (matched on `ChannelName`, login or display name; the channel bar), `madeBy` = user IDs that added it (comma-separated), `editedBy` = has an edit (project) of it by one of those users (teammates' vertical edits only count with `VerticalAccess`). `sort=title|length|madeBy` (the Details view, badrfly 2026-11-21) page by offset (`nextCursor` "o_200"). `days=<IANA time zone>` adds `days: [{ day, count }]` to a paged answer: matches per day of that zone on `dateField`, without `from`/`to` (the calendar). Items carry `editedBy` (usernames, `LibraryListing.EditorsAsync`). Search is a LIKE substring match (case-insensitive for ASCII only). `from` (inclusive) / `to` (exclusive) are ISO 8601 instants **with an offset** (bare dates/times → 400) and filter on `dateField` (default dateAdded) |
| `GET /api/entries/{id}` | |
| `PATCH /api/entries/{id}` | `{ title }` — audited as `entry.renamed` |
| `PUT /api/entries/{id}/trim` | `{ startTime, endTime }` (blank end = end of VOD; a time past the stored VOD length re-reads it from Twitch, for VODs that were live) — re-cuts the entry: cancels a running job (waits up to 20 s, else 409), deletes its files, re-applies the stored-vs-reference rule and re-queues a stored download. VodUnavailable → 410. Audited `entry.retrimmed` |
| `DELETE /api/entries/{id}` | Removes entry + stored/generated files — audited. Phase 3: restrict who can delete |
| `POST /api/entries/{id}/generate` | Reference-only: queue on-demand file (202) or 200 if ready. Failed stored entry: retry. VodUnavailable → 410 |
| `GET /api/entries/{id}/file` | Streams the file (range requests supported); 409 if not ready; 410 if missing on disk (entry then becomes retryable). `?preview=true` (the vertical editor's player) isn't audited as a download |
| `GET /api/channels` / `GET /api/channels/cards` | The team's assigned channels (members can't add or remove them; see Team channels), `isPrimary` = the team's channel (`TeamLabels.Primary`) / each one's Twitch picture and live state `[{ login, profileImageUrl, live, liveSinceUtc }]` (`IChannelCardSource`) |
| `GET /api/channels/{login}/vods?type=archive\|highlight\|upload\|all&cursor=&limit=` | One of the team's channels' VODs (else 403 `channel-not-assigned`), newest first, via Core's `GetGqlVideos`. Highlights carry `sourceStartUtc`: when their broadcast started, read from the playlist folder `{hash}_{login}_{streamId}_{epoch}` (`Services/Playback/HighlightSources`, cached 7 days; null when it can't be told). The page asks only for `archive` and `highlight` (uploads aren't shown since 2026-11-21). `nextCursor` = last edge's cursor; `unmuteWindowOpen` = ended < 24 h ago. 502 if Twitch is unreachable |
| `GET /api/vods/{id}/rescue` / `POST /api/vods/{id}/rescue` | Audio rescue status (404 if none) / start one now (202; a Failed one is retried; one paused by `AudioRescue:Enabled` being off is resumed; others returned as is) |
| `GET /api/entries/{id}/shortform` / `POST /api/entries/{id}/shortform` | Vertical (9:16) renders: `{ status: None\|Rendering\|Failed, progress, error, layout, renders: [{ id, createdAtUtc, expiresAtUtc, sizeBytes, layout, fileUrl, thumbnailUrl }] }` (newest first) / start a render (202) with `{ mode: single\|split, top: {x,y,width,height}, bottom?, splitRatio?, trimStart?, trimEnd?, parts?, keys? }` (fractions of the source video; `parts: [{ start, end, mode, top, bottom?, splitRatio?, keys? }]` in file seconds, in order, no overlap, each ≥ 0.5 s, at most 40: rendered one by one and joined, the plain fields are then set from them). 409 if the entry has no file |
| `GET /api/entries/{id}/shortform/{renderId}/file[?inline=true]` / `.../thumbnail` / `DELETE .../{renderId}` | A render's mp4 (`<title> (vertical).mp4`, audited `file.served`; `inline` = the page's preview player, not audited), its jpg thumbnail, delete it. 404 if gone/expired |
| `GET /api/vods/{id}/play.m3u8[?segments=proxy]` / `.../play/{n}.m3u8` / `.../play/{n}/{segment}` | HLS for the page's player (see Watching VODs), plus `.../play/{n}/clean/{key}.ts` (muted segment with saved audio swapped in). 403 sub-only, 404 gone, 502 Twitch unreachable. Playlists `no-store` |
| `POST /api/auth/login` / `logout` / `GET /api/auth/me` / `POST /api/auth/team` `{ teamId }` | Team user session; `me` = `{ id, username, teams: [{ id, name, isOwner }], currentTeamId, permissions, timeZone, support, combineSegmentDownloads, maxStoredClipSeconds }` |
| `GET /api/auth/teams` / `GET /api/auth/teams/{id}/channel` / `GET /api/auth/app` | Start screen: the member's team cards `[{ id, name, isOwner, isCurrent, hasAccess, canOpenTeamPage, channel: { login, displayName }, teamLastActiveUtc, myLastActiveUtc }]`; one card's channel from Twitch `{ profileImageUrl, live, liveSinceUtc, lastLiveUtc }` (members only, else 404); app name and corner links `{ name, links: { x, discord } }` (no login; set on Admin → App, `GET,PUT /api/admin/app`) |
| `POST /api/links/inspect` `{ token }` / `POST /api/links/accept` `{ token, newAccount, username?, password?, teamName? }` | One-time links (see Accounts). Blank username with a session = join as the logged-in user |
| `GET /api/team` / `DELETE /api/team/members/{userId}` / `PUT /api/team/members/{userId}/role` `{ roleId }` / `POST /api/team/members/{userId}/password-reset` | Team page: members, roles, permission catalog, `myPermissions`, `canRename` / `nextRenameAtUtc` / `renameDays`. `PUT /api/team/name` `{ name }` (team owner, once every 14 days) |
| `POST /api/team/roles` `{ name, permissions: [keys] }` / `PUT,DELETE /api/team/roles/{id}` | Roles (guard rails above) |
| `GET,POST /api/team/invites` / `DELETE /api/team/invites/{id}` | Invite links. A link URL is only in the POST response |
| `GET /api/team/activity?days=` / `GET /api/team/audit?userId=&before=&limit=` / `GET /api/team/settings` | Activity, team log, what the app Owner set: `{ storeClips, maxStoredClipSeconds, channels: [{ login, displayName, saveAudio, autoSaveAudio }], autoSaveAllowedByApp }` (read-only) |
| `PUT /api/auth/settings` `{ timeZone: local\|pacific }` / `POST /api/auth/password` `{ currentPassword, newPassword }` | Own account |
| `GET,POST /api/admin/setup` / `POST /api/admin/login`, `logout`, `password` / `GET /api/admin/me` / `GET,POST /api/admin/links` `{ kind: registration\|ownership, teamId?, note? }` / `DELETE /api/admin/links/{id}` | Staff API (each endpoint's staff permission: see Staff) |
| `GET /api/admin/teams` / `GET /api/admin/teams/{id}?days=` / `PUT /api/admin/teams/{id}/disabled` `{ disabled }` | Teams with disk use and last active; members' activity |
| `GET /api/admin/users?search=` / `GET /api/admin/users/{id}?days=` / `PUT .../disabled` / `POST .../password-reset` | Users across teams |
| `GET /api/admin/audit?teamId=&userId=&staffId=&before=&limit=` / `POST,DELETE /api/admin/support` | App log; view as support |
| `GET /api/admin/staff` / `POST /api/admin/staff/invites` / `PUT /api/admin/staff/{id}/permissions` `{ permissions }` / `PUT .../disabled` / `POST .../password-reset` | Moderators (Owner only) |
| `GET /api/timeline/entries?vodIds=1,2` | The team's library entries on up to 200 VODs: `[{ id, vodId, title, kind, start, end, referenceOnly }]` (card strips) |
| `GET /api/videos?vodIds=&userId=&editedBy=&channels=&search=&from=&to=&dateField=&sort=&order=&days=&limit=&paged=&cursor=` (`paged` as for entries, newest first; `search` matches the title or a segment's label; `from`/`to` = made (or `dateField=streamDate`), ISO with an offset; `userId` / `editedBy` comma-separated; `sort`, `order` and `days` as for entries, length = the segments' whole ranges) / `GET /api/videos/{id}` / `POST /api/vods/{id}/videos` `{ title, markerIds, cuts? }` / `PUT /api/videos/{id}` `{ title?, markerIds?, cuts?: [{ markerId, cuts }] }` / `DELETE /api/videos/{id}` / `POST /api/videos/{id}/copy` `{ rowId? }` | Videos: `{ id, vodId, rowId, userId, createdBy, title, channelName, streamDateUtc, thumbnailUrl, lengthSeconds, segments: [{ markerId, position, startSeconds, endSeconds, label, color, cuts, videoCuts, keptSeconds }], copiedFromVideoId, createdAtUtc, updatedAtUtc, canEdit, canDelete }`. `markerIds` = the whole list in play order. The VOD's timeline includes `videos` |
| `POST /api/markers/{id}/copy` / `POST /api/entries/{id}/copy` / `POST /api/entries/{id}/copy-as-segment`, body `{ rowId? }` | Copies onto one of the caller's rows (see Copies) → 201 with the new marker/entry (`copiedFrom...` set) |
| `POST /api/vods/{id}/rows` `{ kind: video\|clip, name? }` / `PUT /api/rows/{id}` `{ name?, position? }` / `DELETE /api/rows/{id}` / `PUT /api/entries/{id}/row` `{ rowId }` | Rows (see `TimelineRows`). Add/change own rows (`AddEntries`); delete own or anyone's (`DeleteAnyEntries`); a clip moves only to another clip row of its adder on the same VOD (Edit own/any) |
| `GET /api/vods/{id}/timeline` / `POST /api/vods/{id}/markers` `{ seconds, endSeconds?, label?, color?, rowId? }` / `PUT /api/markers/{id}` (same body, whole marker) / `DELETE /api/markers/{id}?videos=unlink\|keep` / `POST /api/markers/{id}/restore` / `GET /api/markers?vodIds=1,2` | Player timeline: `{ clips: [{ id, title, kind, start, end, referenceOnly, followsMarkers, canEdit, rowId, addedByUserId, createdBy }], maxStoredClipSeconds, markers: [{ id, vodId, seconds, endSeconds, label, color, createdBy, createdAtUtc, canDelete, canEdit, rowId }], rows: [{ id, userId, username, kind, position, name, canEdit }] }` (rows: the caller's first, then by member) (team's own); `/api/markers` lists markers of up to 200 VODs (library and VOD cards). Entries carry `startSeconds`/`endSeconds` for placing them (and `followsMarkers`). Add 201 (`AddEntries`; `liveEntry: { entryId, created }` when it made or moved the live markers entry, else null); `restore: true` (the page's undo of a delete) lets a member with `DeleteAnyEntries` name a teammate's video row: the marker is made again as theirs; change own (`AddEntries`) or anyone's (`EditAnyEntries`); delete own (`AddEntries`) or anyone's (`DeleteAnyEntries`), else 403 |
| `GET /api/edits?entryId=\|markerId=\|videoId=` / `GET /api/edits/{id}` / `POST /api/edits` `{ entryId \| markerId \| videoId, title?, cuts? \| parts?, layout? }` / `PUT /api/edits/{id}` (same, null keeps; `layout: {}` removes it) / `DELETE /api/edits/{id}` / `POST /api/edits/{id}/copy` `{ title? }` | Saved edits (see `ItemEdits`). List = `{ edits (newest first), applies: [{ id, editId, editTitle, userId, username, appliedAtUtc }], canApply, applyMakesCopy, canRevert }`; edit = `{ id, kind: clip\|segment\|video, entryId, markerId, videoId, vodId, title, createdByUserId, createdBy, createdAtUtc, updatedAtUtc, cuts, parts, layout, keptSeconds, sourceStart, sourceEnd, sourceParts: [{ markerId, label, start, end, cuts }], canEdit, canDelete }` |
| `POST /api/edits/{id}/apply` `{ rowId? }` / `POST /api/entries\|markers\|videos/{id}/revert` `{ to: previous\|original }` | Apply → `{ kind, item, copied, edit }` (`item` as its own endpoints return it; the copy when `copied`). Revert → 200 with the item; nothing to undo → 409 `code: "nothing-to-undo"`; an edit or revert that would leave nothing of the item as it is now → 409 |
| `GET /api/vods/{id}/sync?channel=&start=&end=` | Sync with: the team channel's past broadcasts recording during that range of the VOD (by stream time): `{ login, displayName, rangeStartUtc, rangeEndUtc, coveredSeconds, matches: [{ vodId, title, createdAtUtc, lengthSeconds, isLive, restricted, start, end, offset, lined }], before, after }` (a pair's saved line-up applied). 400 `same-channel`, 403 not assigned, 404 `vod-gone`, 502 Twitch unreachable |
| `GET /api/vods/{id}/waves?block=` | Sound waves: `{ block, start, rate, levels }` (60 s from `block` × 60; levels = base64 bytes 0–255, `rate` a second; empty past the end). 404 `no-audio` when the audio can't be read |
| `POST /api/vods/{id}/sync/{otherVodId}/audio` `{ at, otherAt, seconds? }` / `PUT /api/vods/{id}/sync/{otherVodId}` `{ at, otherAt }` | Line two VODs up by sound (→ `{ trusted, shift, otherAt, correlation, confidence, reason }`, saved when trusted) / by hand; `AddEntries` |
| `GET /api/vods/matches?q=` | The library's search box: what of the team's work has `q` in its title or label (clip titles, segment and moment labels, video titles): `{ vodIds (newest work first, ≤ 200), clips, videos, segments, moments }` |
| `GET /api/team/statuses` / `POST /api/team/statuses` `{ name, color?, setWhen?, hideFromLists? }` / `PUT /api/team/statuses/order` `{ ids }` / `PUT,DELETE /api/team/statuses/{id}[?moveTo=]` / `DELETE /api/team/tags/{id}` / `PUT /api/entries\|videos/{id}/status` `{ statusId }` / `PUT /api/entries\|videos/{id}/tags` `{ tags }` | Statuses and tags (see `TeamStatuses`): list = `{ statuses: [{ id, name, color, position, setWhen, hideFromLists, clips, videos }], tags: [{ id, name, clips, videos }], noStatusClips, noStatusVideos, canManage, canSet }` (`ViewLibrary`). `/api/entries`, `/api/videos` and `/api/vods/matches` take `status=` (id or `none`) and `tag=`; without `status` items whose status hides from lists are left out. Entry and video JSON carry `teamStatusId` and `tags` |
| `GET /api/my/marks` / `PUT,DELETE /api/my/stars/{clip\|video\|vod}/{id}` / `POST /api/my/opened` `{ kind, id }` | The caller's stars and Recent (see `MemberMarks`): `{ starred: { clips, videos, vods }, recent: { … } }` (IDs, newest first). `/api/entries` and `/api/videos` take `mark=starred\|recent`; `/api/vods/matches` takes `mark=` too (VODs with such clips/videos, the marked VODs themselves first) |
| `GET /api/vods/work?vodIds=&limit=` | The team's work per VOD: `[{ vodId, clips, videos, segments, moments, edits, channelName, streamDateUtc, thumbnailUrl, lastWorkUtc }]`, newest work first; VODs with nothing on their timeline are left out |
| `GET /api/projects?userId=&vodId=&vodIds=&entryId=&videoId=&limit=` / `GET /api/projects/{id}` / `POST /api/projects` `{ title?, content?, entryId?, videoId?, vodId? }` / `PUT /api/projects/{id}` `{ title?, content?, revision? }` / `DELETE /api/projects/{id}` / `POST /api/projects/{id}/copy` `{ title? }` | Projects (see `Projects`): `{ id, userId, createdBy, title, revision, lengthSeconds, pieceCount, vodId, entryId, videoId, vodIds, copiedFromProjectId, createdAtUtc, updatedAtUtc, canEdit, canDelete, content }` (`content` null in lists) |
| `POST /api/vods/{id}/segment-downloads` `{ markerIds, combined?, quality?, entryId? }` | Segment download (202; `GenerateFiles`). `combined` without the team feature → 403 `code: "combine-not-enabled"`. Markers must be the team's segments on that VOD (400) |
| `GET /api/segment-downloads?vodIds=` / `GET .../{id}` / `.../{id}/files/{position}` / `.../{id}/combined` / `.../{id}/zip` / `DELETE .../{id}` | `{ id, vodId, entryId, title, quality, combined, status: Queued\|Running\|Ready\|Failed, error, expiresAtUtc, combinedFileUrl, zipUrl, canDelete, items: [{ position, label, startSeconds, endSeconds, status, error, fileSizeBytes, audioRestoredSeconds, audioMutedSeconds, fileUrl }] }`; files audited `file.served`. Delete own (`GenerateFiles`) or anyone's (`DeleteAnyEntries`) |
| `POST /api/exports` `{ items: [{ kind: vod\|clip\|video\|segment\|edit\|project, editId?, projectId?, fileName?, quality?, vodId, start, end \| entryId, horizontal, vertical, renderId? \| videoId, joined \| markerId }] }` | Export (202): `{ id, status, error, itemsDone, itemsTotal, canDelete, items: [{ position, kind: VodRange\|Clip\|ClipVertical\|Video\|Segment, fileName, quality, vodId, startSeconds, endSeconds, cuts, entryId, markerId, videoId, renderId, joined, lengthSeconds, status, error, partsDone, partsTotal, fileSizeBytes, fileUrl }] }`. Vertical without a render → 409 `no-vertical` |
| `GET /api/exports` / `GET .../{id}` / `.../{id}/items/{position}/file` / `DELETE .../{id}` | The caller's exports; a file (`<fileName>.mp4`, `(vertical).mp4`, a video's `.zip`; audited `file.served`). Delete own (`GenerateFiles`) or anyone's (`DeleteAnyEntries`) |
| `PUT /api/admin/teams/{id}/features` `{ combineSegmentDownloads?, storeClips? }` | App Owner only: per-team features (joined segments, stored clips). `PUT /api/admin/teams/{id}/name` `{ name }`: App Owner renames a team any time |
| `PUT /api/admin/teams/{id}/channels/{channelId}/audio` `{ saveAudio?, autoSaveAudio? }` | App Owner only: audio saving per team channel |
| `GET,POST /api/admin/teams/{id}/channels` `{ login }` / `DELETE .../channels/{channelId}?mode=revoke\|future` | A team's channels (`accessEndedAtUtc` set = existing work only). POST = Owner only; DELETE `ManageTeamChannels`; GET also `ViewTeamsAndUsers`. POST checks Twitch (404) and duplicates (409). Registration links take `channel` (required, one); `GET /api/admin/teams` lists each team's `channels` and `label` |
| `POST /api/auth/reports` `{ kind: error\|bug, message?, errorType?, details?, note?, url?, context?, vodId?, entryId?, visitId? }` | A page's crash or a bug report (201 `{ id }`; see `ErrorReports`). No login needed; rate limited |
| `GET /api/admin/reports?kind=&userId=&teamId=&staffId=&area=&errorType=&browser=&os=&appVersion=&vodId=&fingerprint=&search=&from=&to=&sort=time\|user\|team\|kind\|area\|type\|browser\|os\|version&order=&offset=&limit=` / `GET .../groups` (same filters; sort `count\|people\|last\|first`) / `GET .../{id}` / `GET .../download?format=csv\|json` (+ filters, ≤ 20 000) / `DELETE .../{id}` / `DELETE /api/admin/reports?` (+ filters) | Reports tab (`ViewErrorReports`). `userId=0` / `teamId=0` = none; `errorType=-` = none. List = `{ total, items, facets: { kinds, users, teams, areas, errorTypes, browsers, systems, appVersions } }` (facets: `{ value, label, count }` over the time range only); one report adds `details`, `context`, `userAgent` |
| `GET /api/live/events` | Live stream updates (server-sent events; see Live stream updates) |
| `GET /api/admin/twitch` / `PUT /api/admin/twitch/client-id` / `POST /api/admin/twitch/connect` / `DELETE /api/admin/twitch` | Twitch sign-in for live stream updates (Owner only) |
| `POST /api/vods/download` | Phase 1 dev endpoint: synchronous, writes `<title>.mp4` straight into `downloads/`, not part of the library |

`canDownload` in entry responses checks the generated file's expiry itself, since cleanup runs periodically.

### Web pages
The old library page (`app.js`, `player-bar.js`, `app.css`) was removed in frontend rework step 10; the editor below is the main page
(`index.html`; `editor.html` only redirects to `/`, keeping `?demo`). Its old features live on in the editor (git history has the old code).
- **Keyboard shortcuts live in `wwwroot/keybinds.js`** (`KEYBINDS`: id, scope, keys, description; `KEYBIND_SCOPES`: the scopes with a title
  and where they work; badrfly 2026-11-10). Check it before adding a shortcut and add the new one there first: the page logs a console error
  when two bindings in one scope share a key, and the **shortcuts help** (`ui/shortcuts.js`: `?`, the keyboard icon in the top bar, or the
  account menu) is built from it, so mouse bindings and keys handled elsewhere are listed too.
- **Start screen** (`ui/start.js` + `ui/start.css`, badrfly 2026-11-19): the app opens on it, over the editor with the lights off and
  **nothing loaded** (the editor's lists, players and timeline are only set up once a team is picked: `enterTeam` in `ui/editor.js`). Top:
  app name (`GET /api/auth/app`), clock (user's time zone), profile button; bottom: username left, X / Discord icons right (hidden when
  blank). The app Owner sets the name and both links on the Admin page's **App** tab (`GET,PUT /api/admin/app` `{ name, x, discord }`,
  `RequireStaffOwner`, `AppSettings` row 1, audited `app.settings_changed`; "https://" added when missing, only http(s) links); until it's
  saved they come from `App:Name` / `App:Links:X` / `App:Links:Discord`. The card holds the **login form** (logged out; `login.html` only redirects to
  `/`), a **link's form** (`/#join=token`, `ui/join-form.js`, shared with `join.html`) or the member's **team cards** (`GET /api/auth/teams`:
  the team's channel (`TeamLabels.Primary`), team name, "Team …" = the last change any member made to its work, "You …" = the member's own
  (audited work changes in `AuthEndpoints.WorkChanges` + Player 2 saves), "Waiting for access", a Team page button when allowed), in a
  carousel (< >, arrow keys). Each card's channel picture and **live / last live** come separately (`GET /api/auth/teams/{id}/channel`,
  `IChannelCardSource` = `ChannelCardService`: Twitch picture, the live watcher's state, else the newest broadcast VOD (recording = live; its
  end = last live), cached 5 min); a live channel's picture has a blinking red ring. Picking a team opens the editor on it; the editor's team
  pill brings the screen back (Esc closes it; picking another team reloads straight into it via `sessionStorage` `clipco.start.enter`).
  Staff support views skip it. The start screen's **profile menu** only has the username, Account settings, Report a problem and Log out
  (the Team page is on the team cards); the editor's account menu keeps Team page and Keyboard shortcuts too. A 401 while working goes back to `/`.
- Account pages (`login.html` (redirect), `join.html`, `account.html`, `team.html` + `team.js`, `admin.html` + `admin.js`, helpers in `account.js`) are
  plain HTML forms (no Shoelace) styled by `ui/theme.css` + `ui/pages.css` (same gray theme and fonts as the editor; back arrow to the editor).
  Not logged in → `/` (the start screen's login form). `team.html` = members/roles/activity/log/invites/audio setting (`ACTION_LABELS` in team.js
  names audit actions; add new team-visible ones there), `join.html` = opening any link (staff links go to `admin.html` after),
  `admin.html` = staff (tabs Teams/Users/Links/Moderators/Twitch/App/Reports/App log, shown by permission; tab saved in `localStorage` key `clipAdmin.tab`).
  Pages hide buttons the user's permissions (`me.permissions`) don't allow.
- Time zone (Local / Pacific `America/Los_Angeles`) is set on the Account page, saved per user on the server and cached in `localStorage` key
  `clipLibrary.timeZone`. Formatting only — the API always returns UTC (`...Z`). Days for date filters are computed in the shown time zone
  (`ui/dates.js`: the zone's midnights converted to UTC and sent as `from`/`to`).
- `Intl.DateTimeFormat` throws if `dateStyle`/`timeStyle` are combined with `timeZoneName`; format parts explicitly.
- Static files are served with `Cache-Control: no-cache` (no build step, no versioned names), so a normal reload always picks up new scripts.
- **Watching VODs** (`ui/media.js`, `Services/Playback/`): VODs play in the app's own `<video>` player with hls.js
  (`cdn.jsdelivr.net/npm/hls.js@1.6.16`, loaded lazily). The server gets the access token + master playlist like Core's downloader
  (`VodPlaybackService`, master cached 10 min) and serves `GET /api/vods/{id}/play.m3u8`: landscape video renditions only (no audio-only,
  no portrait), pointing at our `/play/{n}.m3u8`, which is Twitch's media playlist with segment URLs rewritten. Segments load straight
  from Twitch's CDN; if a segment fails before the first frame (e.g. CORS: the CDN refuses `localhost`), the page retries at once with
  `?segments=proxy` (segments through `/play/{n}/{segment}`, only file names in the rendition's folder) and remembers that for 24 h
  (`localStorage` `clipLibrary.segmentMode`). Tokens never reach the browser or the logs.
  The video's `currentTime` is the offset into the VOD (hls.js maps the playlist start to 0). **Fallback:** if the app player can't play a
  VOD (403 sub-only, 404, hls.js missing, unsupported codec, no first frame in 20 s direct / 45 s through the server) the official Twitch embed
  (`player.twitch.tv/js/embed/v1.js`) is used (choice saved in `localStorage` key `clipLibrary.playerMode`, `app`/`twitch`; if the chosen one
  fails the other is tried; both players have the gear menu to switch and say why they fell back). Volume is saved in `clipLibrary.playerVolume`. The embed needs `parent` = `location.hostname`, so open the page
  as `localhost` (Twitch may reject a bare IP); sub-only VODs play there only if the browser is logged in to Twitch with access. With the
  embed, `getCurrentTime()` is a cache refreshed about once a second, so `EmbedBackend.position()` estimates the live offset (last refresh or
  seek target + time played since).
  **Clean audio in the player** (`PlaybackAudioSwap`): the media playlist rewrite points every `{key}-muted.ts` that has saved
  clean audio (`RescuedAudioSegments` with a file) at `/play/{n}/clean/{key}.ts`, in either segment mode. That endpoint fetches the
  muted segment, and ffmpeg swaps its audio for the saved one with `-copyts -mpegts_copyts 1` (video and audio copied, so both keep
  their PTS; the audio-only rendition shares the stream's clock, so it lines up as in RestorePlanner). If the saved audio's sample
  rate/channels differ from the segment's (`AdtsFormat`), audio is re-encoded to match (~21 ms AAC priming). Results are cached in
  `downloads/work/play/{vodId}/{n}/{key}.ts` for 2 h (work/ is cleared at startup); any failure serves the muted segment. The master
  sends `X-Clean-Audio-Segments` (count). fMP4 (`.mp4`) segments aren't swapped. The timeline shows Twitch's muted parts in red and parts
  with saved clean audio in green (from hls.js fragments: `-muted.ts` vs `/clean/` URLs).

### Editor (frontend rework, `index.html` + `wwwroot/ui/`)
Plan and badrfly's decisions: Claude Doc "Frontend rework plan" (10 steps; backend items B1–B7 are built in a separate thread). Built step by
step next to the old page, which step 10 removed; the editor is now `index.html`.
- Libraries from jsDelivr, pinned: Shoelace 2.20.1 (web components, dark theme), Phosphor icons 2.1.1 **bold** (registered as the Shoelace icon
  library `ph`: `<sl-icon library="ph" name="scissors">`), Split.js 1.6.5 (resizable panels, sizes in `localStorage` `clipco.layout.*`),
  IMask 7.6.1 (time boxes), SortableJS 1.15.6. Fonts: Barlow Condensed (headings, `.display`) and Barlow (Google Fonts).
- `ui/theme.css`: gray theme, all text white, one accent (`--accent`, violet) for selection, focus and drop targets; **green (`--ok`) for
  confirm ✓ buttons** (badrfly 2026-11-20, "Polish the whole app's look"). Design tokens there: spacing `--space-1..6` (4/8/12/16/24/32 px),
  radius `--radius-xs` 3 (timeline items) / `--radius-small` 4 (controls) / `--radius` 6 (cards) / `--radius-large` 8 (panels, bars),
  `--control-h` 28 px (tool buttons, bar inputs), `--icon-size` 18 px. Use them in new CSS instead of raw numbers. `ui/editor.css`: layout.
- `ui/core.js`: `api`, `el`, `me`/`can`, settings, date/length formatting, toasts, and **icon buttons**: every button is icon-only (except the
  EDITOR/EXPORT, VODS/VIDEOS/CLIPS, Player 1/Player 2 tabs and the Export button) with a screen-reader `label` and a tooltip showing the
  shortcut from keybinds.js (`data-keybind="toolCut"` in HTML, `iconButton({ keybind })` in JS). New shortcuts still go in keybinds.js first.
- `ui/drag.js`: one drag and drop for everything (pointer events, so canvas items can use `startDrag` too): `draggable`, `dropTarget({ accepts,
  onDrop })`. While dragging, a shade darkens the page except the dragged item and the accepting target under the pointer (clip-path holes).
  `info.altKey` is passed to targets (Alt + drag = copy). Esc cancels.
  `watchDrags({ start, move, end })` follows every drag (`end` gets the accepting target's element or null). Used for the **Player 2 peek**
  (badrfly 2026-11-18, `peekPlayer2WhileDragging` in `ui/editor.js`): dragging a clip/segment/video over Player 2's tab or screen shows
  Player 2's timeline; leaving the timeline section and Player 2's screen, or dropping anywhere else, goes back to Player 1's; if Player 2's
  timeline was already showing nothing changes. A drop on Player 2's timeline moves its playhead to the dropped pieces.
- `ui/timebox.js`: `timeBox({ seconds, fraction })` with a fixed `hh:mm:ss(.ff)` mask; `.seconds` reads/writes.
- Tools bar title/color editor (`openItemEditor` in `ui/editor.js`): tools slide left, title, the 9 colors (clips too) and a green ✓ button
  last (always shown, badrfly 2026-11-20); the tool in use is a filled violet square; also applies on a click anywhere else or Enter (in the box or on a swatch; linking a
  video needs the ✓ button), Esc cancels.
- **Left panel** (`ui/library.js`, step 2): VODS = the team's assigned channels' VODs (`GET /api/channels`, then `/api/channels/{login}/vods`,
  20 per page, more on scroll). **Channel bar** (badrfly 2026-11-19, mockup C "pick several"): the team's channels as Twitch pictures (`/api/channels/cards`; a live one has a blinking red ring, the live state from the live events first). Each click turns one on or off, the others go dark (a live dark one keeps a faint ring); with none on every channel shows. It starts with the team's primary channel on every time the editor loads (not remembered); a one-channel team can't toggle. It filters all three lists: VODs of several channels are merged newest first, cut off at the oldest loaded VOD of any channel that still has more pages (scrolling loads the channel whose oldest is newest; live VODs always on top); CLIPS and VIDEOS send `channels=`. Cards show a small channel picture when several channels are shown. GQL lists have no live flag, so the
  newest VOD (if under 2 days old) is checked with `/api/vods/info` (`isLive` → LIVE badge). VOD cards show only Twitch's info plus the
  Save audio icon (same rule as the old page) or the audio status icon. CLIPS = the team's library entries (`GET /api/entries`, polled while
  downloading): title, length, VOD date, creator; old Twitch clips get a "Twitch" tag and reference-only entries "Long". VIDEOS = `GET /api/videos` (rename / delete by `canEdit`/`canDelete`).
  No filter button since 2026-11-21 (see **Library header** below). Double-click/Enter loads a card in its
  player; right-click (or the Menu key) opens Load / Export / Rename / Delete (by permission; `ui/dialogs.js` `askText`/`confirmAction`).
- **Player 1** (`ui/media.js`, step 3): `MediaPlayer` wraps the old page's app player (hls.js, `HlsBackend`) and Twitch embed fallback
  (`EmbedBackend`), same storage keys (`clipLibrary.playerMode` / `clipLibrary.playerVolume`) and direct/proxy segment fallback. Header: clock, or a
  LIVE button while the playlist has no ENDLIST (outlined when > 8 s behind). Gear menu = quality + switch player. Keys (`ui/editor.js` `setUpKeys`):
  the `player` scope of keybinds.js, anywhere on the editor screen except while typing (and arrows/Enter/Space on tabs, sliders, menus).
- **Timeline** (`ui/timeline.js`, step 3): Player 1 tab = every member's rows on the VOD (`GET /api/vods/{id}/timeline`, re-read every 5 s),
  the caller's first and tinted. Canvases for the ruler (ticks; muted red / saved audio green along the bottom, from hls.js fragments) and the
  rows; row headers are DOM (delete checkbox when allowed, kind icon tile, name (unnamed: "Segments" / "Clips"; double-click renames own,
  `PUT /api/rows/{id}`), owner, hide / solo / lock (page only; shown on hover or while on, so the name has room)). Rows are 40 px. Footer: add video row / clip row (`POST /api/vods/{id}/rows`), delete checked rows, zoom slider (log scale, min 10 s), Fit.
  Click seeks (on an item: to its start); wheel, middle drag and cursors as in **Timeline input** below; the view follows the playhead until panned. Segments hatched with
  brackets, moments a line + triangle, clips solid violet bars (reference ones translucent), cut-out parts darker.
- **Timeline input** (`ui/timeline-input.js`, badrfly 2026-11-16: "universal throughout the app"): both timelines take their cursors
  (`timelineCursor` / `rulerCursor`: arrow, grab over a draggable item/piece, ew-resize on edges, the Premiere-like razor (red-crossed
  where it can't cut), zoom-in, grab/grabbing hand, col-resize on the ruler) and the wheel (`timelineWheel`: wheel scrolls, Ctrl+wheel
  zooms around the mouse, Shift+wheel scrolls rows; middle drag pans) from it. Change them there, never in one timeline.
- **Timeline editing** (`ui/timeline-edit.js`, mixed into `Timeline`, steps 3b and 4): tools bar tools Pointer / Cut / Clip / Zoom / Hand (V/C/X/Z/H), snapping (N,
  `clipLibrary.snap`, 8 px; **new kinds of timeline items must add their times to `snapPoints()`**), undo/redo (last 10 per VOD, this tab:
  marker add/delete/change/split and clip re-trims; steps name items through refs shared per VOD (`ref` / `moveRef`), because undoing a
  delete makes the marker again with a new ID and older steps must follow it; presses queue while a step is sent; a stored clip's re-trim
  still waiting its 3 s is dropped by undo, sent by redo; a "change" that didn't touch cut-out parts sends `cuts: null` so it keeps them). Pointer: click selects (white outline) and seeks; only the selected item's edges drag
  (segments, and VOD clips via `PUT /trim`; a **stored** clip's re-trim is saved 3 s after the last change (`scheduleTrim`, "saving…" next to
  Player 1's title, flushed on `pagehide` with a keepalive fetch: it downloads again), capped at the stored-clip limit over its whole range);
  I/O set them at the playhead; Space plays the
  selection, Shift+Space its last 3 s; Del deletes; double-click or right-click → title/color. S marks a segment (start, then end,
  dashed preview), M a moment; the **clip tool** (X or the film strip, badrfly 2026-11-20: a tool, not two presses) has its own cursor
  (`timeline-input.js`) and makes a clip by dragging across the timeline (dashed preview in the clip's color, capped at
  `maxStoredClipSeconds`), named with a color when the mouse is let go (`lastClipColor`); the tools bar's title/color editor names it and it's saved on the
  member's own row clicked last (else their first; locked rows are skipped). Others' items: view only; with `EditAnyEntries` every change asks
  **Edit original / Make a copy** (`chooseEditTarget`, Enter = copy); right-click → "Make a copy on my row" (`POST .../copy`). Locked rows
  (page only) can't be edited. Hooks to `editor.js`: `openItemEditor` (`onColor` live preview), `onChange`
  (`renderTools`), `onClipsChanged` (library refresh), `onCreated` (Player 2, step 4). The right-click menu is `ui/menu.js` (shared with cards).
- **Player 2 = the project editor** (badrfly 2026-11-16, multi-track rework; plan: Claude Doc "Vertical editor: multi-track timeline plan").
  Player 1 is the overview for marking; Player 2 works like any video editor (Premiere-like) on **projects** (see `Projects`), whose media
  are references into Twitch VODs. Modules: `ui/project.js` (content rules mirroring the server: `fits`, `nearestFit`, `flatten` = what plays,
  `piecesFromItem`: a clip = one piece per kept part, a segment = one piece, a video = its segments' kept parts in order; `ProjectStore`: the
  open project, undo/redo of whole content snapshots (100), saves ~0.6 s after a change with the revision, 409 → takes the server's copy and
  says so, keepalive flush on `pagehide`), `ui/project-timeline.js` (`ProjectTimeline`, uses the shared timeline DOM while Player 2's tab is
  open), `ui/project-playback.js` (`ProjectPlayback`: plays the flattened stretches on Player 2's `MediaPlayer`, switching VODs between
  pieces, black (`#p2-black`) and a clock in gaps, muted rows silent) and `ui/project-vertical.js` (the 9:16 view). In `ui/editor.js`:
  `openProject` / `newProject` / `closeProject` / `addToProject`, the project picker (`renderProjectMenu`: New, own and team projects,
  Add to the export list, Rename, Duplicate / Make my own copy, Delete, Close); the open project is remembered in `localStorage`
  `clipco.p2.project`. Loading a clip, segment or video in Player 2 (drop on its screen or timeline, card menu, dragging off Player 1) adds
  it to the open project at the playhead, or starts a new one. A segment or clip made on Player 1 is added by itself only while an editable
  project is open. **M on Player 2** makes a moment on the VOD under the playhead (its VOD second), the **clip tool** a clip by dragging across
  a piece (that piece's VOD, ≤ 3 min, drawn dashed on the piece while it's named: `ProjectTimeline.dragClip` → hook `onClipRange` →
  `p2SaveClip` in `ui/editor.js`), on the member's own rows of that VOD, like Player 1; in a gap nothing is made. They aren't added to the project (the piece already plays that part). Others' projects: view only (copy to edit). The left panel has a PROJECTS tab (`library.js` `projectCard`).
  **Timeline**: ruler from 0:00 to past the end; opening a project or loading an item into it starts fitted (badrfly 2026-11-16) (scroll and zoom freely: wheel scrolls, Ctrl+wheel zooms, Shift+wheel scrolls rows, middle
  button pans, scrollbar under the rows); rows top to bottom (unnamed rows are named "V1, V2…" once and keep their name), row heads with grip
  (drag to reorder), name (double-click), hide / mute / lock, right-click insert above/below, rename, paste, delete (asks when it holds
  pieces); "Add a row" and "Delete the selected row" in the footer. Pieces: click / Shift+click / marquee select, drag to move (between rows;
  Shift keeps the time; below the last row makes a new row; red where it can't go; a single piece slides to the nearest free place), drag
  edges to trim (up to its neighbors and the VOD's length), I / O trim at the playhead, razor (C, white line) or S splits, Delete / Shift+Delete
  (ripple) deletes (**ripple = sync lock**, badrfly 2026-11-20: the removed time is taken out of every unlocked row, `rippleCut` in
  `ui/project.js`, so synced rows keep in step; pieces across it are split; locked rows stay; Close gap does the same), Ctrl+C / Ctrl+V copy and paste (at the playhead, on the selected row), right-click on a gap = Close gap. Piece menu: Name and
  color, Copy, Copy / Paste layout, Use this layout on every piece, Delete,
  Delete and close the gap (badrfly 2026-11-20: no Split at the playhead or Show in Player 1 there; the menu (`ui/menu.js`) opens upward when there's no room below). **Show in Player 1** = the crosshair in Player 2's controls: the piece at Player 2's playhead (else the selected one), Player 1 goes to the same VOD moment and zooms to the whole clip/segment it came from. The timeline's known length is stretched to reach that moment (`Timeline.reveal`, `openVod`), since a live VOD's length from Twitch can lag behind or be 0 and the view would be clamped to the start (empty). Covered parts of lower rows are drawn darker; the ruler's bottom band shows what plays and hatches gaps. Snapping
  uses `clipLibrary.snap`. New pieces take the layout of the piece before them on their row.
  **Apply to the originals** (badrfly 2026-11-20: a button in Player 2's controls, `p2-apply`, replacing the piece menu's "Apply this trim
  to the original"; `applyEditToOriginals` in `ui/editor.js`): for the whole timeline, every clip/segment the pieces came from gets the
  range of all its pieces on its VOD with what's between them as cut-out parts (skipped when already so); VOD-range pieces (Sync with,
  VOD ranges) become segments for the parts that **play** (not covered): this VOD's on the member's own row, another VOD's on their synced
  row of it on this VOD (video + clip row made when missing); segments with the same times already there are skipped, so applying twice
  adds nothing. Teammates' items: one Edit originals / Make copies question (`EditAnyEntries`), else copies (pieces then come from the
  copy). One confirm lists what changes. Pieces from videos aren't applied.
  **Nothing is deleted from Player 2** except pieces of the project (badrfly 2026-11-16): unloading only closes the project; real deletes of
  clips/segments are on Player 1's timeline and the cards.
- **Sync with** (badrfly 2026-11-20, `syncWith` / `runSync` in `ui/editor.js`, `GET /api/vods/{id}/sync?channel=&start=&end=` in
  `VodSyncEndpoints`): right-click a Player 2 piece > Sync with…, then click a channel picture in the library's channel bar
  (`pickChannel` in `ui/library.js`: the rest goes dark with the drag shade). The server turns the piece's VOD range into wall-clock time
  (the VOD's `createdAt` + seconds) and finds that channel's **past broadcasts** recording then (`IChannelVodSource`, paged newest first until
  48 h before the range; highlights/uploads skipped: their date isn't the stream's). Answer: `matches: [{ vodId, start, end, offset, isLive,
  restricted }]` (offset = seconds into the asked range), `coveredSeconds`, and `before` / `after` neighbors when nothing matched; its own
  channel → 400 `same-channel`. The page puts the matches on a new row named after the channel right under the piece's row
  (`ProjectTimeline.addSyncedRows`; more rows if they'd overlap), source `{ kind: "vod" }`, no layout. Lower = covered and silent until the
  row above is cut away; parts the other stream didn't record are gaps. **Slide the footage** (`<` / `>` 0.1 s, Alt+drag inside a piece,
  or the piece menu): moves a piece's VOD range without moving it on the clock. Synced pieces don't follow Player 1 trims.
  **Lining up** (badrfly 2026-11-20): start times are a few seconds off between streamers, so after a sync the page asks
  `POST /api/vods/{id}/sync/{otherVodId}/audio { at, otherAt, seconds? }`: `IVodLoudness` (`VodLoudness`: the audio-only rendition's segments
  for the range, joined, ffmpeg to 8 kHz mono) → `AudioAligner` (log-loudness envelope at 100/s minus its 1 s average, so mics and volumes
  don't matter; Pearson correlation over ±20 s; trusted at r ≥ 0.25 and the peak ≥ 1.15× the best lag 0.5 s away). Collab streams share
  the voice call, so the talking pattern lines up. A trusted match moves the synced pieces and is saved for the pair: `VodSyncOffsets`
  (team, VodA < VodB, `Seconds`: bSec = aSec + (A.createdAt − B.createdAt) + Seconds, `Source` audio/manual); a slide saves it too
  (`PUT /api/vods/{id}/sync/{otherVodId} { at, otherAt }`, debounced, against the closest row above with another VOD's piece at that time).
  `GET .../sync` applies a saved pair (`lined`: start / audio / manual), so a pair is lined up once per collab.
  **Side by side** (piece menu "Line up side by side…", `ui/sync-view.js`, shown on a piece with another VOD's piece playing at the same time on
  another row, `partnerOf` in `ui/editor.js`): two players next to each other, the partner leads and the piece follows (re-seeked past
  0.2 s drift), both sound waves ±8 s around the playhead, `<` / `>` (Shift 1 s) or "Match by sound" move it; Save = one slide (undoable,
  saves the pair). **Sound waves** (`ui/waves.js`, W on/off, `clipco.p2.waves`): drawn on Player 2's pieces once 20 min or less is in view,
  from `GET /api/vods/{id}/waves?block=n` (`VodWaveService`: 60 s blocks of 20 levels/s from `IVodLoudness.GetLevelsAsync`, full blocks
  kept in `downloads/waves/{vodId}/{n}.bin`, deleted by the cleanup service 7 days after last shown; short ones kept a minute in memory).
- **Picking for Player 2 is in the library** (badrfly 2026-11-21, replaces the VOD tree below): clip and video rows (on their own tabs and
  under an opened VOD) have an arrow (→ / ←) that opens them to their **edits** (a video first to its **segments**, each opening to the
  edits made from it). An edit of no clip or video names its segment when every piece came from one (`segmentId` on project DTOs,
  `ProjectEndpoints.SegmentOf`); an opened VOD ends with **every segment on it** (`kids.segments`, from `/api/markers`; badrfly 2026-11-21:
  a vertical edit mustn't be found only through a video) inside one **Segments (n)** line (closed at first, `segs:{vodId}`), each opening to its edits (`lseg:` keys; inside a video `seg:`), then the edits
  left (a VOD range, or a deleted clip/video/segment: `kids.other`). Double-click / Enter loads a segment
  or opens an edit; right-click = `editMenu` / `segmentMenu` from `ui/p2-tree.js`; the loaded line is marked (`player2Changed`). An opened
  VOD reads its edits once (`/api/projects?vodId=`) and fills its rows' lists; rows on the CLIPS/VIDEOS tabs read theirs when opened
  (`entryId=` / `videoId=`); `refreshEdits()` re-reads after edits change. Player 2's header menu only has the loaded item's actions plus
  **Show in the library** (`revealLoaded`: its VOD opened to the line, else its clip/video tab). The tree isn't drawn any more.
- **Player 2's picker = the VOD tree** (`ui/p2-tree.js`, badrfly 2026-11-17, "the VOD is the project"; supersedes the project picker and the
  PROJECTS tab, which are gone; the page never says "project"): VODs with work (`GET /api/vods/work`, Player 1's first) › Videos › video › its
  segments (+ the video's edits) / Clips › clip › its edits / Other edits (lone segments, VOD ranges, deleted parents). Opening a line closes its
  siblings; opening the menu with something loaded opens only the path to it. Click opens a clip/video line, double-click or its play icon
  loads it; segments and edits load with a click; pencil / right-click renames (creator or EditAnyEntries; edits follow `canEdit`, copy and
  export follow `canCopy` / `canDownload`). **Loading a clip, video or segment** (cards, tree, drop on Player 2's screen) replaces what's
  loaded and opens it as itself (`ProjectStore.openDraft`, state "original"): nothing is saved until the first change, which POSTs a new edit
  of it (`entryId` / `videoId`, else `vodId`) named "title · Edit n". **Except** (badrfly 2026-11-17): from cards, drops and the last-loaded
  restore, an item the member already has a horizontal edit of opens their newest one (`newestOwnEdit`; a segment's: own VOD edits whose
  pieces all come from it); the tree and the vertical editor's way back load the original (`loadInPlayer2(item, { original: true })`). New edits are renamed from the menu ("Save as a new edit…" names one first). Dropping
  more on Player 2's timeline adds them only from the same VOD (`store.takesVod`). Player 1's new segments/clips go into an open saved edit
  of their VOD only. Last loaded is remembered in `clipco.p2.project` (`{ edit }` or `{ kind, id, vodId }`).
- **Row focus** (badrfly 2026-11-18, `focusRow` / `endRowFocus` in `ui/editor.js`): every row head on both timelines has a play button
  (`rowPlayButton` in `ui/project-timeline.js`) that plays only that row, gaps skipped: Player 2 through `ProjectPlayback.setSolo` (only that
  row's pieces, even if hidden/muted/covered), Player 1 its segments' and clips' kept parts (`Timeline.focusParts`, moments skipped,
  `focusTickP1` jumps gaps, remembering the part it jumped to so it never seeks twice). Meanwhile the rest goes dark with the drag shade
  (`.focus-shade`, `shadePath` from `ui/drag.js`), with holes for the player, the tools bar and the **whole timeline** (lit, badrfly
  2026-11-18: the playhead stays free to move), recut only when the path changes; the left panel and the other player fold away, other rows
  fold to 16 px lines (the row is 64 px; `rowGeom()` in both timelines) and the timeline shrinks to its rows (`--focus-lower-h`, measured
  only when the rows' height or the screen changes, so nothing jumps). Rows can't be edited then (`setUpRowFocus`): a click on an item or
  piece moves the playhead to **its start**, a click elsewhere or a ruler/playhead drag to that exact time; the zoom slider, Fit, and the
  Pointer / Zoom / Hand tools and moment marker (M) stay enabled, other tools and the footer's row buttons are grayed out, only playback/zoom/those keys work
  (`FOCUS_KEYS`), drops are refused. Stop button, Esc, a click in the dark, another tab/screen, the vertical editor or the row going away
  ends it (the player pauses where it is). Clicking a folded row's head plays that one.
- **Large libraries** (badrfly 2026-11-20, thread "Redesign the library for large teams"; design: artifact 9MdAVyAcfJWnanjTVNNRdt, built in
  5 PRs: 1 paging + rows, 2 search box with chips, 3 VOD cards open to their clips/videos, 4 team statuses (owner + a role permission) and
  tags, 5 stars / Recent / Mine and Player 2's picker): CLIPS and VIDEOS load 100 at a time (`paged=true`, next page near the bottom; clips
  still downloading are re-read one by one, not the whole list). Every list shows as **rows** by default (28 px lines; only the rows in view are drawn, `ROW_H` / `SEP_H` / `MONTH_H` in
  library.js = `.lrow` / `.lrow.lsep` / `.lmonth` in editor.css). **Date column** (badrfly 2026-11-21, his sketch; replaced the month bar): a
  thin line between items, a slightly thicker line between days, a month's line across the whole width with its name in the date column on
  the right, and each day's number in that column in front of its first row or card (both views; `linesFor` / `renderRail` in library.js).
  While a day's first item is above the top, `#side-stamp` keeps its month and day at the top of the column (pushed up by the next day);
  a click on a month, a day or the stamp opens the loaded months to jump to, plus "Load older". The **card** of the row under the mouse, else the selected one, is at the bottom (`#side-peek`), always the same height and kept (with a hint) while empty in rows/Details, so the list never resizes; the list is a whole number of rows high (`fitListHeight`) and scrolling stops on a line's start (`snapScroll`, on `scrollend`). `#side-listwrap` clips the date stamp so it never covers the buttons above. ↑/↓ move the selection.
  The cards view is the button next to the count, remembered per member in `localStorage` `clipco.library.view.{userId}`.
  **VODs open to their work** (step 3, `toggleVod` / `loadKids` in library.js): a VOD with clips or videos gets an arrow (rows; cards: a
  caret button and a menu item); opened (click, → / ←), its videos then clips, in VOD order, are slim indented rows under it (kind icon in
  the item's color; drag, double-click, menu and the peek card work as on their own tabs), up to 50 of each (`/api/videos?vodIds=` /
  `/api/entries?vodId=`, paged), then "All n clips" filters CLIPS by the VOD. Opened VODs re-read theirs when clips or videos change.
- **Library search box** (badrfly 2026-11-20, step 2 of Large libraries; "the search box" in `ui/library.js`): always in sight over the three
  lists (`#search-input`, Ctrl+K or `/`, keys in keybinds.js scope `search`). Plain words search every list: clips by title, videos by title
  and their segments' labels (`/api/videos?search=`), VODs by loaded title plus every VOD whose clips, segments, moments or videos match
  (`GET /api/vods/matches?q=` → `{ vodIds, clips, videos, segments, moments }`, also the "in 12 segment labels" hint). Typed shortcuts are
  suggested as you type and become **chips** under the box (× or Backspace in an empty box takes one off): `by:name` (made / edited by),
  `vod:title` (one stream), `@channel` (only that channel on in the channel bar, no chip), a day (`nov 14`, `14 nov`, `today`, `yesterday`,
  `24h`: clips on their sort date, videos when made (`from`/`to` on `/api/videos`), VODs when streamed), `>1m` / `<30s` (clips:
  `minSeconds` / `maxSeconds` on `/api/entries`). A chip that doesn't apply to the open tab is dashed. Enter with no suggestion picked turns
  every shortcut naming exactly one thing into its chip. `by:` adds the member to the Made by / Edited by lists (several allowed).
- **Statuses in the library** (step 4 of Large libraries): a row of status chips over CLIPS and VIDEOS (`#status-bar`, `renderStatusBar` in library.js; counts, "No status"; click filters, again clears); rows show the status dot (with its name when there's room), cards a status line and `#tags`. The clip/video menu lists the statuses (✓ on the current one, number hints), No status and Tags…; keys 1–9 / 0 on a selected row set the n-th status / none (`rowStatus` in keybinds.js). The search box suggests `#tag` and status names as chips.
- **Stars, Recent and Mine** (step 5 of Large libraries; end of library.js): ☆ on a row (shown on hover / when on), on a card and in the menus, or `*` on a row (`rowStar`), stars a VOD, clip or video for the member only. Loading a VOD in Player 1 or a clip/video in Player 2 (`noteOpened` from `openVod` / `loadInPlayer2`) puts it in Recent. The status row starts with ★ (count) / Recent / Mine chips (Mine = made by me, the member filter); VODs filter the loaded list by the starred/recent VOD IDs. Filled icons: names ending in `-fill` resolve to Phosphor's fill set (`registerIconLibrary` in editor.js). **Player 2's picker** (`ui/p2-tree.js`) has a search box (titles of clips and videos, segment labels; `GET /api/vods/matches?q=`, the first 3 VODs opened to what matched) and a ★ button (`mark=starred`); starred VODs come after Player 1's / the loaded one, then recent ones; lines carry a star toggle.
- **Library filters** (`ui/library.js`): CLIPS and VIDEOS filter by VOD and members (Made by / Edited by: made an edit of it; several each),
  shared between the two lists; card menus have "Show this VOD's clips/videos"; VOD cards show what's on them ("3 clips · 1 video").
- **Library header** (badrfly 2026-11-21, mockup "D · Your version" in artifact VJ5Zpn1oFck29DUF2eGYXB): **no filter button**. The quick
  chips row: ★ / Recent / Mine / **With work** (VODs only) / **Last 24 h**, then the statuses (not on VODs), then a sticky **×** that clears
  every filter (`clearFilters`). Next to the count: **Rows / Cards / Details** (`#side-views`, remembered in `clipco.library.view.{userId}`)
  and the **calendar** (`#side-calendar`, `ui/calendar.js`; month names, day numbers and the stamp open it too, replacing the month jump
  menu and the per-month counts): each day's count (clips/videos: `days=` on the list endpoint with the filters on minus the day ones;
  VODs: the loaded ones), the month's total, a strip of months; click = go to the day (older pages load until it's there,
  `jumpToDay`), Shift+click two days = only those days (`clipFilter.range`, a chip). **Highlights** sit under the broadcast they came from
  (opened VOD rows, sparkle icon; `loadHighlightsTo` reads highlight pages back past the oldest loaded broadcast; `highlightParent`:
  `sourceStartUtc` within 10 min of a broadcast's start, else the broadcast streamed last before the highlight); uploads aren't shown.
  **Details view**: rows with columns (clips/videos: Name, Length, Made by, Edited by, Added, Stream date; VODs: Name, Length, Work,
  Stream date). A heading click sorts (`state.sorts` per list, `clipco.library.sort.{userId}`; **rows and cards use the same sort**, and
  are grouped by month/day only when sorted by a date; clips/videos sort on the server, VODs sort the loaded ones); drag a heading's edge to
  resize, double-click it to reset; right-click the headings to show/hide columns (`clipco.library.columns.{userId}`); Made by / Edited by
  have a ▾ member checklist (`openMemberFilter`). **No horizontal scrollbar** (badrfly): the wheel over the headings scrolls the columns
  sideways (the list is `overflow-x: hidden` and scrolled by script; a sideways touchpad swipe over the rows works too).
- **Saved edits** (`ItemEdits`, the old Player 2) are no longer made or shown by the page: at startup `SavedEditConversion` turns each one into
  a project once (`EditProject.ConvertedFromEditId`; one row, the edit's kept parts end to end, its splits split pieces, a clip edit's vertical
  layout part under each piece's middle becomes its layout with keys in VOD seconds; title "item · edit title" or "item (edit)"). The edits,
  their endpoints and `kind: "edit"` exports stay on the server.
- **Videos** (step 6): the tools bar's **link** tool (Player 1, `startLinking` / `toggleLink` / `saveLink` in `ui/timeline-edit.js`): click
  segments on any of your own rows in play order (synced rows too: segments of another VOD; numbered badges; clicking again takes one out), type a title, press ✓ →
  `POST /api/vods/{id}/videos` (the VOD of its first part). **The picks survive opening other VODs** (badrfly 2026-11-20, `ui/link-tray.js`):
  chips **in the tools bar** between the title and ✓ (numbered, the number colored by stream (`STREAM_COLORS`), drag to reorder, × out,
  click = go to it), with a one-line strip under the bar (what's happening, "Sort by stream time", Cancel), kept in `sessionStorage` `clipco.p1.linking`;
  Player 1's `<` / `>` arrows open the channel's previous / next stream (`adjacentVod` in `ui/library.js`). **"Add segments…"** on a video's
  card or tree line starts the link tool with its segments in the tray; ✓ then `PUT`s the video. Player 1 draws pills "→ 3 · 20 Nov" /
  "1 · 19 Nov ←" to a video's parts on other streams (click opens them, `drawStreamLinks` in `ui/timeline.js`); Player 2's ruler marks where
  a video's next piece is another stream; video cards say "n streams"; the video shows in VIDEOS and loads in Player 2. Player 1 marks segments that are in a video with their
  position (tip: "In video "x" (n of m)"). In Player 2 a video becomes pieces of the project (its segments'
  kept parts). Rename and delete on the card's menu; others' videos are copied with `POST /api/videos/{id}/copy`.
- **Synced rows on Player 1** (badrfly 2026-11-20, `addSyncedRow` / `runSyncedRow` / `openSynced` in `ui/editor.js`): the footer's
  people button, then a channel picture (`pickChannel`): `GET /api/vods/{id}/sync` ±90 min around the playhead, the broadcast at the
  playhead (else nearest) gets a synced row, lined up by sound when the pair wasn't (same call as Player 2's Sync with). The timeline keeps
  their items **on this VOD's clock** (`load()`: their seconds − `shift`, `raw` = as sent; `shiftTimes` in `ui/timeline-edit.js`) and
  turns them back at every server call (`toServer` / `fromServer` in `postMarker`, `putMarker`, `trimClip`, copies; posts go to the row's
  VOD). Drops only go on rows of the item's own VOD (`rowVod`); a dragged item carries its own VOD and times. `targetRow` prefers this VOD's
  rows unless a synced row (head or item) was clicked last. Adding one makes a video row and a clip row of their VOD (clips made there are
  theirs: `POST /api/entries` with the row's VOD and times). Player 1 keeps playing this VOD; a synced row's head has a picture-in-picture
  button instead of solo (`ui/p1-pip.js` `SyncedPip`: their stream small in Player 1's corner, following Player 1 at position + shift, muted
  until its speaker is pressed, its own volume not saved (`video.dataset.ownVolume`); its arrows / double-click open their VOD in Player 1
  at the same moment (`openSynced`) and `#p1-synced-back` returns). **W** draws sound waves on synced rows (theirs above the middle, this
  VOD's below; `clipco.p1.waves`, ≤ 20 min in view) and **< / >** move a synced row 0.1 s (`slideSynced`: the selected item's row, else
  the row clicked last, else the only synced VOD; every synced row of that VOD moves at once, saved for the pair after 0.6 s with
  `PUT /api/vods/{id}/sync/{other}`). Player 2 / Export: a video's segments play from `segment.vodId`; the Export preview plays every part,
  opening each one's VOD in turn.
- **Checked rows play together** (badrfly 2026-11-20): every Player 1 row head has a checkbox (also what "Delete the checked rows" deletes,
  only those the member may). The footer's play button, or a checked row's own play button, plays all checked rows through row focus
  (`focusRow("p1", ids)`, `Timeline.focus` is a list): `focusParts()` = `{ start, end, vodId, shift }` on this VOD's clock, the higher row
  winning where they overlap; a synced row's parts play from its own VOD (`playPartP1` opens it in Player 1 without touching the timeline;
  `p1Away = { vodId, shift }` meanwhile, `p1Position()` = the timeline's clock, details of another VOD don't reach the timeline) and
  `endRowFocus` comes back to this VOD at the same moment.
- **Cut-out parts** (step 7, `ui/cuts.js` mirrors the server's `CutOuts` rules): set from Player 2 by a piece's "Apply this trim to the
  original" (the parts of the clip/segment that no piece copied from it keeps). Drawn darker on Player 1 (tip: "Cut out … · left out of
  files") and their edges are snap points. Player 1 skips them while a selected segment or clip plays on its own; a clip dropped into a
  project leaves them out of its pieces.
- **Export screen** (step 8, `ui/export.js`, EXPORT tab): same left panel; a view-only preview player (plays the selected item's kept parts
  in order, skipping cut-out parts; a video part by part), settings for the selected item (file name, quality from `/api/vods/info`, clip
  Horizontal/Vertical checkboxes: vertical is one of the clip's **vertical edits** (`GET /api/projects?entryId=&vertical=true`; badrfly
  2026-11-17), the member's own newest by default, a teammate's with `DownloadTeamVerticals`, or an older clip render; grayed out with none.
  Sent as clip `projectId` (or `renderId`); without either the server uses the caller's newest vertical edit of the clip, else their newest
  render, and makes a `ProjectVertical` item named like the clip's file; video `.zip` of separate files or joined, grayed out unless `me.combineSegmentDownloads`), the **drop list** (clip / video
  / segment cards and Player 1 items via right-click "Add to the export list"; reorder by the handle with SortableJS; kept in `localStorage`
  `clipco.export.list`; Twitch clips and moments refused) and the caller's exports (`GET /api/exports`, polled every 2 s while running;
  download, delete). Dropping a **VOD** opens a pop-up (own `MediaPlayer`, title, start/end time boxes): a one-off range, never an entry.
  Export = one `POST /api/exports` for the whole list; a one-item export downloads by itself when ready. A refused request (400) marks the
  rows named in `errors["items[i]"]` (`api` errors carry the response body as `error.problem`). Items re-read their card when selected
  (a deleted one is marked gone). **Projects** go on the list from Player 2's export icon /
  tree (key `project:{id}`, its format fixed by the edit's kind: Horizontal or Vertical (9:16), re-read with `GET /api/projects/{id}`, no
  preview) and are sent as `{ kind: "project", projectId, horizontal, vertical }`. Rows saved as `edit:{id}` by older pages still export.
- **The vertical editor** (`ui/project-vertical.js`, badrfly 2026-11-17: separate from Player 2): it shows a **vertical edit** with its own
  timeline (the shared timeline DOM, "Player 2" reads "Vertical"); Player 2's horizontal edits never show boxes, keyframes or layout menu
  items. A vertical edit loaded from anywhere (tree, last loaded) opens straight in it (`syncVerticalView` in `ui/editor.js`). The phone
  button / E on Player 2 makes a **new vertical edit** (`newVerticalEdit`: a draft copied from what's loaded, layouts dropped, named
  "item · Vertical n", saved on its first change; Render saves it first via `ProjectStore.ensureSaved`); in the vertical editor it goes back
  (`leaveVertical`: what was loaded before, else the edit's clip/video, else unloads). The tree shows vertical edits with a phone icon and
  "9:16", horizontal ones with a monitor. Player 2 takes the whole width (`.vertical-mode`)
  with the landscape source (Player 2's own `<video>` drawn on a canvas; with Twitch's embed the boxes can't be previewed and a note says so)
  and the live 9:16 preview. It edits the **layout of the piece under the playhead** (in a gap: the selected one): One / Two boxes (1 / 2),
  presets (built-in + own in `clipco.vertical.presets`), box drag/resize with snapping (Alt = free), arrow keys move the selected box, split
  pill, safe zones (G, `clipco.vertical.safeZone`), **moving boxes**: F adds/removes a keyframe at the playhead (VOD seconds), `[` / `]` jump,
  once a piece has keys a box change sets the key at the playhead; "Use this layout on every piece". Every change is an edit change (same
  undo). **Render 9:16** (Ctrl+Enter) = `POST /api/exports` `{ kind: "project", vertical: true }` after saving; its renders (the caller's
  ProjectVertical export items of this edit) are listed with progress and download. The vertical edit's timeline is below. Keys: scopes
  `vertical` / `vertical-box` in keybinds.js. The old clip-file vertical editor (`ui/vertical.js`, Layout row) is gone; `/shortform` stays on
  the server (old renders expire as before).
- `/?demo` fills sample cards (nothing is sent to the server) to try the shell.

### Card thumbnails (`CardThumbnailService`, badrfly 2026-11-15)
- Clip and video cards (and export list rows) show **a frame from the middle of what they play** (kept parts, so cut-out parts and
  trims move it), drawn over the VOD's thumbnail, which shows while it loads or if it can't be made (`thumbStyle` in `ui/core.js`).
  `frameUrl` on entries (`/api/entries/{id}/thumbnail?t=`) and videos (`/api/videos/{id}/thumbnail?t=`), `t` = tenths of a VOD second;
  null for Twitch clip entries (they keep Twitch's own thumbnail). A re-trim changes `t`, so the URL changes (served `max-age` 7 days).
- Made from the clip's file when it has one (stored or unexpired generated, at half its kept length), else from **one segment** of the
  VOD's HLS: the smallest rendition ≥ 360p from `VodPlaybackService`, the segment holding the time (fMP4: its init segment in front),
  ffmpeg output-side `-ss` (TS timestamps don't start at 0), 320 px wide jpg. Cached as `downloads/thumbs/{vodId}/{tenths}.jpg` (shared by
  every clip/video with that frame); 3 at a time; failures remembered 10 min (→ 404). The cleanup service deletes ones not shown for 7 days
  (write time = last shown, touched daily). Test: `CardThumbnailServiceTests` (fake HLS made with ffmpeg).

### Twitch clips (`ClipInfoService`, `ClipFileService`)
- **No longer added** (2026-11-15, team channels): `POST /api/entries` refuses clip links and `/api/clips/info` is gone. Clip entries added
  before stay in the library and still generate files as below.
- clips.twitch.tv / `twitch.tv/{channel}/clip/` links (or slugs) are added through the same `POST /api/entries` (`vodId` field)
  and are **always reference-only**: Twitch keeps the clip, so nothing is stored (user's call, 2026-10-26). They exist in the
  library so muted audio can be replaced and the vertical editor can be used on them. Trim fields are ignored; `PUT /trim` → 409.
- Twitch mutes a clip wherever its VOD is muted. When the file is generated, `ClipFileService`:
  1. if the source VOD exists and has saved clean audio for muted parts of the clip's range, cuts the file **from the VOD**
     with `AudioRestoreService` (Exact, the clip's quality where the VOD has it), so the clean audio is in;
  2. else fetches Twitch's clip file with Core's `ClipDownloader` (`EncodeMetadata` off);
  3. else (clip deleted) cuts it from the VOD without restore; if both are gone → `VodUnavailable` ("Clip deleted").
  `AudioRestoredSeconds`/`AudioMutedSeconds` are set as for VOD entries.
- Twitch's `videoOffsetSeconds` is whole seconds, so a file cut from the VOD can start/end up to ~1 s off Twitch's clip.
  Audio and video stay in sync (same restore path as VOD entries).
- `ClipInfoService.GetClipAsync` calls Core's `GetClipInfo` first: `GetShareClipRenderStatus` throws a NullReferenceException
  for a missing clip. `ReferenceVodCheckService` skips clips (a clip outlives its VOD).
- No Review for clips.

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
- **Parts** (`ShortformLayout.Parts`, `ShortformPart`, badrfly 2026-11-15): a layout per part of the file. `Validate` checks the parts
  instead of the plain fields; the endpoint then calls `Normalize()` (plain fields = first part's layout, `TrimStart` = first start,
  `TrimEnd` = last end), so the stored layout works for anything that reads one layout. `ShortformService` renders each part like a trimmed
  render into `{renderId}.p{n}.mp4` (AAC audio, same 1080x1920 libx264) and joins them with `SegmentJoiner` (stream copy); part files and
  `join.txt` are deleted after. Progress is split by part length. Audit details say "n parts". Test: `ShortformPartsTests` (needs ffmpeg).
- **Moving boxes** (`ShortformKey`, `Keys` on a layout or part: `keys: [{ time, top, bottom? }]`, file seconds, in order, ≤ 30): from 2 keys
  the box's crop follows eased expressions of t (`Ease`, nested `if(lt(t,..))`; t counts from the piece's start). The union of a box's keyed
  boxes is cropped first; same-size boxes pan with crop x/y expressions, a changing size uses `scale=...:eval=frame` then a fixed crop.
  Audit summary adds ", moving boxes". Tests in `ShortformPartsTests` (one renders a pan and a zoom).
- **Who made a render** (badrfly, 2026-11-15, frontend rework): the `.json` keeps `createdByUserId` (null for older renders); renders in
  `GET /shortform` carry `createdByUserId`/`createdBy`. Exporting a clip as vertical uses the member's **own newest** render, or with
  `TeamPermission.DownloadTeamVerticals` ("Download teammates' vertical edits", bit 18) any of the team's renders of that clip they pick; neither
  → the page grays the checkbox out. Seeing teammates' renders at all needs `ViewTeamVerticals` or that (see Projects, `VerticalAccess`). Downloading a render from the editor's list is still `ViewLibrary`.

### Gotchas
- Core silently falls back to the best quality when the requested one doesn't exist — always validate first.
- Titles are sanitized with Core's `FilenameService`, which swaps `: ? | / \` etc. for full-width look-alikes
  (`：？｜`), so file names contain non-ASCII characters (Git Bash globbing chokes on some; use PowerShell).
- Twitch deletes VODs after a while (typically 7–60 days). Reference-only entries then become `VodUnavailable`
  ("VOD deleted" on the page) when generation is attempted, or within `Downloads:VodCheckHours` (12) via
  `ReferenceVodCheckService`, which also switches them back to ReferenceOnly if the VOD reappears (unpublish/republish).
  Entries are never deleted automatically (user's choice, 2026-10-26); stored entries keep their file.
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
3. **Done.** Step 1: logins, staff Owner, teams (multi-team accounts), one-time links, team-scoped library, audit of
   signups/logins. Step 2: roles + permissions, member activity, team audio setting, per-user time zone, password change/reset.
   Step 3: app moderators (permissions picked by the Owner), "view as support" (per moderator on/off), staff password resets,
   disabling users/teams, and per-user/team activity and disk use for staff.
