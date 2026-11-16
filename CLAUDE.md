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
- `LibraryEntries.Kind` (`Vod`/`Clip`, migration default `Vod`) + `ClipSlug`: see Twitch clips. For clips `VodId` is the source
  VOD (0 if Twitch didn't say) and `StartTime`/`EndTime` the clip's place in it (whole seconds).
- `Channels` (`SavedChannel`): the team's **assigned channels** (see Team channels): login (unique per team, lower-case), display name,
  Twitch user ID, `WatchMutedAudio` (for the muted-audio rescue), `AddedAtUtc`, `AddedByUserId`.
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
- `LibraryEntries.FollowsMarkers`: a **live markers entry** (`LiveMarkerEntryService`, badrfly 2026-11-10). The first marker/segment a team
  adds on a VOD that is still live (GQL video `status == "RECORDING"`, `VodDetails.IsLive`) makes a reference-only entry (always reference,
  whatever its length; **not from a Review**: the player sends `?makeLiveEntry=false` on marker POST/PUT/DELETE while reviewing a card, badrfly 2026-11-11) from 30 s before the first marker to 30 s after the last, the end capped at the VOD's length so far but never before
  the last marker. Every later marker add/delete on that VOD moves it (even after the stream ended; generated file dropped, audited
  `entry.followed_markers`, not counted as a member edit) until someone saves a cut by hand (`PUT /trim` clears the flag). Deleting the last
  marker leaves it as it is. **One per VOD per team**: `MadeFromMarkers` stays set after it stops following, so later markers don't make a
  second card (they used to, which looked like cards duplicating; the migration backfilled it from the `entry.added` audit text).
  `PUT /trim` with `followMarkers: true` (the player's undo of a hand re-cut) makes it follow again and moves it to the markers. `IVodDetailsSource` (VodInfoService; faked in `AccountsTests`) is where it reads the VOD.
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
- `Videos` (`Video`) + `VideoSegments` (badrfly, 2026-11-15, `VideoEndpoints`): a **video** is one or more segments from one video row of one VOD,
  linked in order with a title (team query filter; `RowId` FK cascade; links cascade with the video and with the marker). Owner = the row's
  member; edit own (`AddEntries`) or anyone's (`EditAnyEntries`), delete own or anyone's (`DeleteAnyEntries`); others copy it
  (`POST /api/videos/{id}/copy`: segments copied onto the caller's row, linked the same way). It **follows its segments**: lengths come from the
  markers live; deleting a segment unlinks it and a video with none left is deleted (`VideoEndpoints.DeleteEmptyAsync`, after marker/row
  deletes, audited `video.deleted`); a linked segment can't become a moment or move rows (409 `code: "segment-in-video"`). Deleting a video
  keeps its segments. Channel name, stream date and thumbnail are read from Twitch when it's made. Audited `video.added`/`video.changed`.
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
  **saved edits** in Player 2, reference-only versions of a clip (`EntryId`), segment (`MarkerId`) or video (`VideoId`), FK cascade with the item,
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
- `ExportBatches` + `ExportItems` (cascade): **exports** (badrfly, 2026-11-15, the Export tab's list; `ExportEndpoints`, `ExportWorker`). One
  `POST /api/exports` (`GenerateFiles`) with items of kind `vod` (a VOD range dropped on the list: **a one-off, never an entry**), `clip`
  (horizontal and/or vertical: two items), `video` (a `.zip` of one file per segment, or `joined` into one file, which needs the team's
  `CombineSegmentDownloads`, 403 `combine-not-enabled`) or `segment`; each with its own `quality` and `fileName`. What an item is made of is
  copied at request time (ranges, cut-out parts, a video's segments in `Parts` JSON, the vertical `RenderId`). The worker (one export at a
  time, separate from the other queues) makes items in list order with `KeptPartsCutter`; a **stored clip at its own quality is copied**
  (no download), so is a vertical render; one failing item doesn't stop the rest. Progress: `partsDone/partsTotal` per item, `itemsDone`
  per export (the page polls). Files in `downloads/exports/{id}/{position}.mp4` (a .zip video: folder `{position}/`, zipped when served);
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
- A converter marks every `DateTime` as UTC when read back (SQLite has no date type) — keep all times UTC.
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
  The app Owner (or a moderator with `StaffPermission.ManageTeamChannels`) assigns each team its Twitch channels on team registration/owner
  links (`AccountLinks.ChannelsJson`, checked on Twitch when the link is made, added when it's used) and on the Admin page's Teams tab
  (`/api/admin/teams/{id}/channels`, audited `channel.added`/`channel.removed`). `TeamChannelAccess` decides which VODs a team may open,
  **strictly**: the VOD's channel (GQL owner, cached 12 h; matched by Twitch user ID, then login) must be assigned; work already on a VOD
  doesn't open it. Removing a channel takes a mode (badrfly): `?mode=revoke` deletes the row (every VOD closes now, work included) **and hides
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
- **Automatic audio saving is a team setting** (`Teams.AutoSaveAudio`, off by default, `ManageAudioSettings`). A saved channel is watched
  when its team has it on; rescues not started by hand run only while such a team watches their channel (`AudioRescueService.AutoWatchedLogins`,
  across all teams). `AudioRescue:Enabled` is now the app-wide kill switch (default true).
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
  update so they can't be used twice. Only the token's SHA-256 is stored; the token is in the URL **fragment** (`/join.html#token`) and
  sent in request bodies only, so it never reaches server logs. Opening a link while logged in offers "Continue as …".
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

### Muted audio rescue (`Services/AudioRescue/`)
Twitch mutes segments (`123-muted.ts`) and keeps the clean copy (`123.ts`) for a limited time (Core assumes 24 h and
only tries to unmute VODs <= 24 h old, silently keeping muted audio on 403). We save clean audio and splice it back:
- **Automatic saving is off by default, per team** (it filled the dev disk, ~4.8 GB in 5 days for 2 channels). It's the team setting
  `Teams.AutoSaveAudio` (Team page, permission `ManageAudioSettings`); `AudioRescue:Enabled` (default true) is only the app-wide kill switch.
  Without a team that has it on, a channel isn't watched and rescues not started by hand pause (no grab, no prune; VOD cards say
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
| `GET /api/entries?search=&sort=dateAdded\|streamDate&order=desc\|asc&limit=&from=&to=&dateField=dateAdded\|streamDate` | Search is a LIKE substring match (case-insensitive for ASCII only). `from` (inclusive) / `to` (exclusive) are ISO 8601 instants **with an offset** (bare dates/times → 400) and filter on `dateField` (default dateAdded) |
| `GET /api/entries/{id}` | |
| `PATCH /api/entries/{id}` | `{ title }` — audited as `entry.renamed` |
| `PUT /api/entries/{id}/trim` | `{ startTime, endTime }` (blank end = end of VOD; a time past the stored VOD length re-reads it from Twitch, for VODs that were live) — re-cuts the entry: cancels a running job (waits up to 20 s, else 409), deletes its files, re-applies the stored-vs-reference rule and re-queues a stored download. VodUnavailable → 410. Audited `entry.retrimmed` |
| `DELETE /api/entries/{id}` | Removes entry + stored/generated files — audited. Phase 3: restrict who can delete |
| `POST /api/entries/{id}/generate` | Reference-only: queue on-demand file (202) or 200 if ready. Failed stored entry: retry. VodUnavailable → 410 |
| `GET /api/entries/{id}/file` | Streams the file (range requests supported); 409 if not ready; 410 if missing on disk (entry then becomes retryable). `?preview=true` (the vertical editor's player) isn't audited as a download |
| `GET /api/channels` | The team's assigned channels (members can't add or remove them; see Team channels) |
| `GET /api/channels/{login}/vods?type=archive\|highlight\|upload\|all&cursor=&limit=` | One of the team's channels' VODs (else 403 `channel-not-assigned`), newest first, via Core's `GetGqlVideos`. `nextCursor` = last edge's cursor; `unmuteWindowOpen` = ended < 24 h ago. 502 if Twitch is unreachable |
| `GET /api/vods/{id}/rescue` / `POST /api/vods/{id}/rescue` | Audio rescue status (404 if none) / start one now (202; a Failed one is retried; one paused by `AudioRescue:Enabled` being off is resumed; others returned as is) |
| `GET /api/entries/{id}/shortform` / `POST /api/entries/{id}/shortform` | Vertical (9:16) renders: `{ status: None\|Rendering\|Failed, progress, error, layout, renders: [{ id, createdAtUtc, expiresAtUtc, sizeBytes, layout, fileUrl, thumbnailUrl }] }` (newest first) / start a render (202) with `{ mode: single\|split, top: {x,y,width,height}, bottom?, splitRatio?, trimStart?, trimEnd?, parts?, keys? }` (fractions of the source video; `parts: [{ start, end, mode, top, bottom?, splitRatio?, keys? }]` in file seconds, in order, no overlap, each ≥ 0.5 s, at most 40: rendered one by one and joined, the plain fields are then set from them). 409 if the entry has no file |
| `GET /api/entries/{id}/shortform/{renderId}/file[?inline=true]` / `.../thumbnail` / `DELETE .../{renderId}` | A render's mp4 (`<title> (vertical).mp4`, audited `file.served`; `inline` = the page's preview player, not audited), its jpg thumbnail, delete it. 404 if gone/expired |
| `GET /api/vods/{id}/play.m3u8[?segments=proxy]` / `.../play/{n}.m3u8` / `.../play/{n}/{segment}` | HLS for the page's player (see Watching VODs), plus `.../play/{n}/clean/{key}.ts` (muted segment with saved audio swapped in). 403 sub-only, 404 gone, 502 Twitch unreachable. Playlists `no-store` |
| `POST /api/auth/login` / `logout` / `GET /api/auth/me` / `POST /api/auth/team` `{ teamId }` | Team user session; `me` = `{ id, username, teams: [{ id, name, isOwner }], currentTeamId, permissions, timeZone, support, combineSegmentDownloads }` |
| `POST /api/links/inspect` `{ token }` / `POST /api/links/accept` `{ token, newAccount, username?, password?, teamName? }` | One-time links (see Accounts). Blank username with a session = join as the logged-in user |
| `GET /api/team` / `DELETE /api/team/members/{userId}` / `PUT /api/team/members/{userId}/role` `{ roleId }` / `POST /api/team/members/{userId}/password-reset` | Team page: members, roles, permission catalog, `myPermissions` |
| `POST /api/team/roles` `{ name, permissions: [keys] }` / `PUT,DELETE /api/team/roles/{id}` | Roles (guard rails above) |
| `GET,POST /api/team/invites` / `DELETE /api/team/invites/{id}` | Invite links. A link URL is only in the POST response |
| `GET /api/team/activity?days=` / `GET /api/team/audit?userId=&before=&limit=` / `GET,PUT /api/team/settings` `{ autoSaveAudio }` | Activity, team log, team settings |
| `PUT /api/auth/settings` `{ timeZone: local\|pacific }` / `POST /api/auth/password` `{ currentPassword, newPassword }` | Own account |
| `GET,POST /api/admin/setup` / `POST /api/admin/login`, `logout`, `password` / `GET /api/admin/me` / `GET,POST /api/admin/links` `{ kind: registration\|ownership, teamId?, note? }` / `DELETE /api/admin/links/{id}` | Staff API (each endpoint's staff permission: see Staff) |
| `GET /api/admin/teams` / `GET /api/admin/teams/{id}?days=` / `PUT /api/admin/teams/{id}/disabled` `{ disabled }` | Teams with disk use and last active; members' activity |
| `GET /api/admin/users?search=` / `GET /api/admin/users/{id}?days=` / `PUT .../disabled` / `POST .../password-reset` | Users across teams |
| `GET /api/admin/audit?teamId=&userId=&staffId=&before=&limit=` / `POST,DELETE /api/admin/support` | App log; view as support |
| `GET /api/admin/staff` / `POST /api/admin/staff/invites` / `PUT /api/admin/staff/{id}/permissions` `{ permissions }` / `PUT .../disabled` / `POST .../password-reset` | Moderators (Owner only) |
| `GET /api/timeline/entries?vodIds=1,2` | The team's library entries on up to 200 VODs: `[{ id, vodId, title, kind, start, end, referenceOnly }]` (card strips) |
| `GET /api/videos?vodIds=&userId=&limit=` / `GET /api/videos/{id}` / `POST /api/vods/{id}/videos` `{ title, markerIds, cuts? }` / `PUT /api/videos/{id}` `{ title?, markerIds?, cuts?: [{ markerId, cuts }] }` / `DELETE /api/videos/{id}` / `POST /api/videos/{id}/copy` `{ rowId? }` | Videos: `{ id, vodId, rowId, userId, createdBy, title, channelName, streamDateUtc, thumbnailUrl, lengthSeconds, segments: [{ markerId, position, startSeconds, endSeconds, label, color, cuts, videoCuts, keptSeconds }], copiedFromVideoId, createdAtUtc, updatedAtUtc, canEdit, canDelete }`. `markerIds` = the whole list in play order. The VOD's timeline includes `videos` |
| `POST /api/markers/{id}/copy` / `POST /api/entries/{id}/copy` / `POST /api/entries/{id}/copy-as-segment`, body `{ rowId? }` | Copies onto one of the caller's rows (see Copies) → 201 with the new marker/entry (`copiedFrom...` set) |
| `POST /api/vods/{id}/rows` `{ kind: video\|clip, name? }` / `PUT /api/rows/{id}` `{ name?, position? }` / `DELETE /api/rows/{id}` / `PUT /api/entries/{id}/row` `{ rowId }` | Rows (see `TimelineRows`). Add/change own rows (`AddEntries`); delete own or anyone's (`DeleteAnyEntries`); a clip moves only to another clip row of its adder on the same VOD (Edit own/any) |
| `GET /api/vods/{id}/timeline` / `POST /api/vods/{id}/markers` `{ seconds, endSeconds?, label?, color?, rowId? }` / `PUT /api/markers/{id}` (same body, whole marker) / `DELETE /api/markers/{id}` / `GET /api/markers?vodIds=1,2` | Player timeline: `{ clips: [{ id, title, kind, start, end, referenceOnly, followsMarkers, canEdit, rowId, addedByUserId, createdBy }], maxStoredClipSeconds, markers: [{ id, vodId, seconds, endSeconds, label, color, createdBy, createdAtUtc, canDelete, canEdit, rowId }], rows: [{ id, userId, username, kind, position, name, canEdit }] }` (rows: the caller's first, then by member) (team's own); `/api/markers` lists markers of up to 200 VODs (library and VOD cards). Entries carry `startSeconds`/`endSeconds` for placing them (and `followsMarkers`). Add 201 (`AddEntries`; `liveEntry: { entryId, created }` when it made or moved the live markers entry, else null); change own (`AddEntries`) or anyone's (`EditAnyEntries`); delete own (`AddEntries`) or anyone's (`DeleteAnyEntries`), else 403 |
| `GET /api/edits?entryId=\|markerId=\|videoId=` / `GET /api/edits/{id}` / `POST /api/edits` `{ entryId \| markerId \| videoId, title?, cuts? \| parts?, layout? }` / `PUT /api/edits/{id}` (same, null keeps; `layout: {}` removes it) / `DELETE /api/edits/{id}` / `POST /api/edits/{id}/copy` `{ title? }` | Saved edits (see `ItemEdits`). List = `{ edits (newest first), applies: [{ id, editId, editTitle, userId, username, appliedAtUtc }], canApply, applyMakesCopy, canRevert }`; edit = `{ id, kind: clip\|segment\|video, entryId, markerId, videoId, vodId, title, createdByUserId, createdBy, createdAtUtc, updatedAtUtc, cuts, parts, layout, keptSeconds, sourceStart, sourceEnd, sourceParts: [{ markerId, label, start, end, cuts }], canEdit, canDelete }` |
| `POST /api/edits/{id}/apply` `{ rowId? }` / `POST /api/entries\|markers\|videos/{id}/revert` `{ to: previous\|original }` | Apply → `{ kind, item, copied, edit }` (`item` as its own endpoints return it; the copy when `copied`). Revert → 200 with the item; nothing to undo → 409 `code: "nothing-to-undo"`; an edit or revert that would leave nothing of the item as it is now → 409 |
| `POST /api/vods/{id}/segment-downloads` `{ markerIds, combined?, quality?, entryId? }` | Segment download (202; `GenerateFiles`). `combined` without the team feature → 403 `code: "combine-not-enabled"`. Markers must be the team's segments on that VOD (400) |
| `GET /api/segment-downloads?vodIds=` / `GET .../{id}` / `.../{id}/files/{position}` / `.../{id}/combined` / `.../{id}/zip` / `DELETE .../{id}` | `{ id, vodId, entryId, title, quality, combined, status: Queued\|Running\|Ready\|Failed, error, expiresAtUtc, combinedFileUrl, zipUrl, canDelete, items: [{ position, label, startSeconds, endSeconds, status, error, fileSizeBytes, audioRestoredSeconds, audioMutedSeconds, fileUrl }] }`; files audited `file.served`. Delete own (`GenerateFiles`) or anyone's (`DeleteAnyEntries`) |
| `POST /api/exports` `{ items: [{ kind: vod\|clip\|video\|segment\|edit, editId?, fileName?, quality?, vodId, start, end \| entryId, horizontal, vertical, renderId? \| videoId, joined \| markerId }] }` | Export (202): `{ id, status, error, itemsDone, itemsTotal, canDelete, items: [{ position, kind: VodRange\|Clip\|ClipVertical\|Video\|Segment, fileName, quality, vodId, startSeconds, endSeconds, cuts, entryId, markerId, videoId, renderId, joined, lengthSeconds, status, error, partsDone, partsTotal, fileSizeBytes, fileUrl }] }`. Vertical without a render → 409 `no-vertical` |
| `GET /api/exports` / `GET .../{id}` / `.../{id}/items/{position}/file` / `DELETE .../{id}` | The caller's exports; a file (`<fileName>.mp4`, `(vertical).mp4`, a video's `.zip`; audited `file.served`). Delete own (`GenerateFiles`) or anyone's (`DeleteAnyEntries`) |
| `PUT /api/admin/teams/{id}/features` `{ combineSegmentDownloads }` | App Owner only: per-team features being tried out |
| `GET,POST /api/admin/teams/{id}/channels` `{ login }` / `DELETE .../channels/{channelId}?mode=revoke\|future` | A team's channels (`accessEndedAtUtc` set = existing work only) (`ManageTeamChannels`; GET also `ViewTeamsAndUsers`). POST checks Twitch (404) and duplicates (409). Team links take `channels: [logins]` too; `GET /api/admin/teams` lists each team's `channels` |
| `POST /api/vods/download` | Phase 1 dev endpoint: synchronous, writes `<title>.mp4` straight into `downloads/`, not part of the library |

`canDownload` in entry responses checks the generated file's expiry itself, since cleanup runs periodically.

### Web pages
The old library page (`app.js`, `player-bar.js`, `app.css`) was removed in frontend rework step 10; the editor below is the main page
(`index.html`; `editor.html` only redirects to `/`, keeping `?demo`). Its old features live on in the editor (git history has the old code).
- **Keyboard shortcuts live in `wwwroot/keybinds.js`** (`KEYBINDS`: id, scope, keys, description; `KEYBIND_SCOPES`: the scopes with a title
  and where they work; badrfly 2026-11-10). Check it before adding a shortcut and add the new one there first: the page logs a console error
  when two bindings in one scope share a key, and the **shortcuts help** (`ui/shortcuts.js`: `?`, the keyboard icon in the top bar, or the
  account menu) is built from it, so mouse bindings and keys handled elsewhere are listed too.
- Account pages (`login.html`, `join.html`, `account.html`, `team.html` + `team.js`, `admin.html` + `admin.js`, helpers in `account.js`) are
  plain HTML forms (no Shoelace) styled by `ui/theme.css` + `ui/pages.css` (same gray theme and fonts as the editor; back arrow to the editor).
  Not logged in → `login.html`; after login → `/`. `team.html` = members/roles/activity/log/invites/audio setting (`ACTION_LABELS` in team.js
  names audit actions; add new team-visible ones there), `join.html` = opening any link (staff links go to `admin.html` after),
  `admin.html` = staff (tabs Teams/Users/Links/Moderators/App log, shown by permission; tab saved in `localStorage` key `clipAdmin.tab`).
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
  from Twitch's CDN; if that fails while starting, the page retries with `?segments=proxy` (segments through `/play/{n}/{segment}`,
  only file names in the rendition's folder) and keeps that for the visit. Tokens never reach the browser or the logs.
  The video's `currentTime` is the offset into the VOD (hls.js maps the playlist start to 0). **Fallback:** if the app player can't play a
  VOD (403 sub-only, 404, hls.js missing, unsupported codec, no first frame in 20 s) the official Twitch embed
  (`player.twitch.tv/js/embed/v1.js`) is used (choice saved in `localStorage` key `clipLibrary.playerMode`, `app`/`twitch`; if the chosen one
  fails the other is tried). Volume is saved in `clipLibrary.playerVolume`. The embed needs `parent` = `location.hostname`, so open the page
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
- `ui/theme.css`: gray theme, all text white, one accent (`--accent`, violet) for selection, focus and drop targets. `ui/editor.css`: layout.
- `ui/core.js`: `api`, `el`, `me`/`can`, settings, date/length formatting, toasts, and **icon buttons**: every button is icon-only (except the
  EDITOR/EXPORT, VODS/VIDEOS/CLIPS, Player 1/Player 2 tabs and the Export button) with a screen-reader `label` and a tooltip showing the
  shortcut from keybinds.js (`data-keybind="toolCut"` in HTML, `iconButton({ keybind })` in JS). New shortcuts still go in keybinds.js first.
- `ui/drag.js`: one drag and drop for everything (pointer events, so canvas items can use `startDrag` too): `draggable`, `dropTarget({ accepts,
  onDrop })`. While dragging, a shade darkens the page except the dragged item and the accepting target under the pointer (clip-path holes).
  `info.altKey` is passed to targets (Alt + drag = copy). Esc cancels.
- `ui/timebox.js`: `timeBox({ seconds, fraction })` with a fixed `hh:mm:ss(.ff)` mask; `.seconds` reads/writes.
- Tools bar title/color editor (`openItemEditor` in `ui/editor.js`): tools slide left, title + the 9 marker colors on the right; applies on a
  click anywhere else or Enter (linking a video needs the ✓ button), Esc cancels.
- **Left panel** (`ui/library.js`, step 2): VODS = the team's assigned channels' VODs (`GET /api/channels`, then `/api/channels/{login}/vods`,
  20 per page, more on scroll; a channel picker when the team has 2+, saved in `clipco.library.channel`). GQL lists have no live flag, so the
  newest VOD (if under 2 days old) is checked with `/api/vods/info` (`isLive` → LIVE badge). VOD cards show only Twitch's info plus the
  Save audio icon (same rule as the old page) or the audio status icon. CLIPS = the team's library entries (`GET /api/entries`, polled while
  downloading): title, length, VOD date, creator; old Twitch clips get a "Twitch" tag and reference-only entries "Long". VIDEOS = `GET /api/videos` (rename / delete by `canEdit`/`canDelete`).
  Filter popover (funnel icon): VODs = search loaded titles + broadcasts/highlights/uploads; clips = server search, sort field + order, Last
  24 hours or one day (`ui/dates.js`, midnights in the viewer's time zone, filtered on the sort field). Double-click/Enter loads a card in its
  player; right-click (or the Menu key) opens Load / Export / Rename / Delete (by permission; `ui/dialogs.js` `askText`/`confirmAction`).
- **Player 1** (`ui/media.js`, step 3): `MediaPlayer` wraps the old page's app player (hls.js, `HlsBackend`) and Twitch embed fallback
  (`EmbedBackend`), same storage keys (`clipLibrary.playerMode` / `clipLibrary.playerVolume`) and direct/proxy segment fallback. Header: clock, or a
  LIVE button while the playlist has no ENDLIST (outlined when > 8 s behind). Gear menu = quality + switch player. Keys (`ui/editor.js` `setUpKeys`):
  the `player` scope of keybinds.js, anywhere on the editor screen except while typing (and arrows/Enter/Space on tabs, sliders, menus).
- **Timeline** (`ui/timeline.js`, step 3): Player 1 tab = every member's rows on the VOD (`GET /api/vods/{id}/timeline`, re-read every 5 s),
  the caller's first and tinted. Canvases for the ruler (ticks; muted red / saved audio green along the bottom, from hls.js fragments) and the
  rows; row headers are DOM (delete checkbox when allowed, kind icon, name (double-click renames own, `PUT /api/rows/{id}`), owner, hide / solo /
  lock (page only)). Footer: add video row / clip row (`POST /api/vods/{id}/rows`), delete checked rows, zoom slider (log scale, min 10 s), Fit.
  Click seeks (on an item: to its start), wheel zooms, Shift+wheel pans; the view follows the playhead until panned. Segments hatched with
  brackets, moments a line + triangle, clips solid violet bars (reference ones translucent), cut-out parts darker.
- **Timeline editing** (`ui/timeline-edit.js`, mixed into `Timeline`, steps 3b and 4): tools bar tools Pointer / Cut / Zoom / Hand (V/C/Z/H), snapping (N,
  `clipLibrary.snap`, 8 px; **new kinds of timeline items must add their times to `snapPoints()`**), undo/redo (last 10 per VOD, this tab:
  marker add/delete/change/split and clip re-trims; steps name items through refs shared per VOD (`ref` / `moveRef`), because undoing a
  delete makes the marker again with a new ID and older steps must follow it; presses queue while a step is sent; a stored clip's re-trim
  still waiting its 3 s is dropped by undo, sent by redo; a "change" that didn't touch cut-out parts sends `cuts: null` so it keeps them). Pointer: click selects (white outline) and seeks; only the selected item's edges drag
  (segments, and VOD clips via `PUT /trim`; a **stored** clip's re-trim is saved 3 s after the last change (`scheduleTrim`, "saving…" next to
  Player 1's title, flushed on `pagehide` with a keepalive fetch: it downloads again), capped at the stored-clip limit over its whole range);
  I/O set them at the playhead; Space plays the
  selection, Shift+Space its last 3 s; Del deletes; double-click or right-click → title/color. S / X mark a segment / clip (start, then end,
  dashed preview; clips capped at `maxStoredClipSeconds`), M a moment; the tools bar's title/color editor names it and it's saved on the
  member's own row clicked last (else their first; locked rows are skipped). Others' items: view only; with `EditAnyEntries` every change asks
  **Edit original / Make a copy** (`chooseEditTarget`, Enter = copy); right-click → "Make a copy on my row" (`POST .../copy`). Locked rows
  (page only) can't be edited. Hooks to `editor.js`: `openItemEditor` (`colors: false` for clips, `onColor` live preview), `onChange`
  (`renderTools`), `onClipsChanged` (library refresh), `onCreated` (Player 2, step 4). The right-click menu is `ui/menu.js` (shared with cards).
- **Player 2** (step 4, `openInPlayer2` in `ui/editor.js`): plays one segment, clip or video, loaded by dropping a card (or "Load in Player 2"),
  by dragging an item's body off the Player 1 timeline (`startDrag`; only edges of the selected item resize), or by itself when a segment or
  clip is made on Player 1 (`onCreated`, without switching the tab). It's a second `MediaPlayer` and a `SourceTimeline` (**saved edits**,
  below). Header: the **edit picker**, then type, length (what the edit keeps) and title; icons Show in Player 1 (opens the VOD there if
  needed, selects and zooms to the item), **Apply to Player 1**, vertical, rename (the item itself), **unload** (eject), volume, fullscreen.
  **Nothing is deleted from Player 2** (badrfly 2026-11-16): its Delete key, eject icon and right-click "Unload from Player 2" only clear it
  (`hooks.onUnload` → `closePlayer2`); real deletes are on Player 1's timeline and the library cards. Only one player plays at a time. The
  tools bar and keys work on the player whose timeline tab is open; clicking a player with something loaded opens its tab. In Player 2 the
  create tools are off. Old Twitch clips without a VOD can't.
- **Saved edits in Player 2** (badrfly 2026-11-16, Premiere-style; design and API in the project file `rework/player2-edits-spec.md`;
  `ui/source-timeline.js`, `SourceTimeline extends Timeline`, mode `"item"`): the item in Player 2 is the **source**; its timeline runs in
  **source time** from 0:00 to the item's length (a video: its segments end to end, in play order), nothing outside it is shown and nothing
  can be dragged past it (extend the item on Player 1). What's done there is an **edit**: the ends trimmed inward (the white outline's
  grips, I / O) and parts cut out (cut tool drag; right-click a cut part to put it back); Player 1 doesn't change. **Splits**
  (badrfly 2026-11-16): a cut tool click splits the item there (white line with a notch; saved on the edit as `layout.splits`
  `{ partId: [VOD seconds] }`, the rest of `layout` stays the vertical one; right-click "Remove this split"); a pointer click picks the
  **piece** under it (between the item's ends, its parts, splits and cut edges; `pieceSel`, violet) and Delete leaves it out of the edit or,
  on a left-out piece, puts it back (`deletePiece`); with nothing picked Delete still unloads. Splits are undo steps too. The cut tool's
  cursor is a **razor** like Premiere's (`RAZOR` / `RAZOR_BLOCKED` SVG cursors in `ui/timeline-edit.js`, crossed out in red where a click
  can't cut) and a white line shows where the click will cut (`updateRazor` / `drawRazor`, snapped like the click; Player 1 too, on segments). Edits are kept
  per part in VOD seconds (`partCuts`, like the item's own cut-out parts; a video's segments' own cut-out parts stay out of every edit) and
  saved ~0.6 s after the last change (`POST/PUT /api/edits`, one request at a time, keepalive on `pagehide`). **Original** = the item as on
  Player 1, view only: the first change makes a new edit ("Edit N"); a teammate's edit is view only too and the first change makes your own
  copy ("Copy of …"). The picker (`renderEditMenu` in `ui/editor.js`) lists Original, your edits, each member's edits (length, "applied"),
  New edit (the whole item), Apply to Player 1 (or "to a copy on my row" when `applyMakesCopy`), Undo last apply, Revert to original
  (`POST /api/{entries|markers|videos}/{id}/revert`), Make my own copy, Rename, Delete. **Apply** makes what the edit leaves out the item's
  cut-out parts (grayed out on Player 1); `onApplied` reloads Player 1 and the cards. The edit last open per item is remembered in
  `localStorage` `clipco.p2.lastEdit`. Undo/redo in Player 2 = changes to the edit on show (this visit). Playback (`p2Position` / `p2Seek` /
  `p2Tick` in `ui/editor.js`, over `sourceParts()`) plays what the edit keeps: it skips left-out parts, goes from one part of a video to the
  next and stops at the edit's end. Twitch clips: view only.
- **Dropping on rows** (step 5, `dropPlan` / `runDrop` in `ui/timeline-edit.js`): the Player 1 track is a drop target for timeline items and
  clip cards of the same VOD, on the member's **own, unlocked** rows only. Own segment/moment on another own video row → moved (`PUT /api/markers/{id}`
  with `rowId`); own clip on another own clip row → moved (`PUT /api/entries/{id}/row`); someone else's (or own with Alt) → copied there
  (`POST .../copy`); **Alt + a VOD clip on a video row** → a segment at its times (`copy-as-segment`). Segments and moments never go on clip
  rows, clips go on video rows only with Alt, Twitch clips aren't copied. The row is highlighted and a tip says what letting go does;
  moves and copies are undo steps (`clipRow` for clip moves).
- **Videos** (step 6): the tools bar's **link** tool (Player 1, `startLinking` / `toggleLink` / `saveLink` in `ui/timeline-edit.js`): click
  segments on one of your own rows in play order (numbered badges; clicking again takes one out), type a title, press ✓ →
  `POST /api/vods/{id}/videos`; the video shows in VIDEOS and loads in Player 2. Player 1 marks segments that are in a video with their
  position (tip: "In video "x" (n of m)"). In Player 2 a video is a source of saved edits (above); its parts' right-click menu also has the
  video's own changes: title/color, Move earlier/later and Take out of the video (`PUT /api/videos/{id}` with the whole `markerIds`), Make a
  copy. Others' videos: view only, or with `EditAnyEntries` the Edit original / Make a copy choice (`POST /api/videos/{id}/copy`). Rename on
  Player 2's icon and the card's menu; delete on the card's menu.
- **Cut-out parts** (step 7, `ui/cuts.js` mirrors the server's `CutOuts` rules): made in Player 2 as part of an edit and put on the item by
  Apply to Player 1 (a video: its own per-segment cuts). Drawn darker on both timelines (tip: "Cut out … · left out of files", Player 2:
  "left out of this edit") and their edges are snap points. Playback skips them (`skipCut` / `jumpCuts` in `ui/editor.js`): Player 2 always,
  Player 1 while a selected segment or clip plays on its own.
- **Export screen** (step 8, `ui/export.js`, EXPORT tab): same left panel; a view-only preview player (plays the selected item's kept parts
  in order, skipping cut-out parts; a video part by part), settings for the selected item (file name, quality from `/api/vods/info`, clip
  Horizontal/Vertical checkboxes: vertical uses the member's own newest render, or with `DownloadTeamVerticals` a picked team render, grayed
  out with neither; video `.zip` of separate files or joined, grayed out unless `me.combineSegmentDownloads`), the **drop list** (clip / video
  / segment cards and Player 1 items via right-click "Add to the export list"; reorder by the handle with SortableJS; kept in `localStorage`
  `clipco.export.list`; Twitch clips and moments refused) and the caller's exports (`GET /api/exports`, polled every 2 s while running;
  download, delete). Dropping a **VOD** opens a pop-up (own `MediaPlayer`, title, start/end time boxes): a one-off range, never an entry.
  Export = one `POST /api/exports` for the whole list; a one-item export downloads by itself when ready. A refused request (400) marks the
  rows named in `errors["items[i]"]` (`api` errors carry the response body as `error.problem`). Items re-read their card when selected
  (a deleted one is marked gone). **Saved edits** go on the list from Player 2's edit menu ("Add to the export list", saved first):
  key `edit:{id}`, the item's kind plus `editId`, labeled "Clip edit" etc., re-read with `GET /api/edits/{id}` (`editFields`: a video's
  segments with their own cut-out parts plus the edit's) and sent as `{ kind: "edit", editId, ... }`. Renders are matched by `editId`
  (an edit's vertical uses renders made for it; a plain clip only renders made without one).
- **Vertical editor in Player 2** (step 9, `ui/vertical.js`): a clip in Player 2 gets the 9:16 toggle (E). Player 2 then takes the whole
  width (`.vertical-mode`, Player 1 hidden) and shows the old page's editor on the clip's file (`/file?preview=true`; trim times are seconds
  into the file). It works on the **edit shown in Player 2** (`openVertical(entryId, { editId, cuts })`, `setVerticalEdit` on every edit
  change; `verticalEdit()` in `ui/editor.js`): `vx.editCuts` = what the edit (Original: the clip's own cut-out parts) leaves out, in file
  seconds; the preview skips it, `pieces()` leaves it out of the layout sent, renders are made with `editId` (a new edit is saved first,
  `hooks.editForRender`) and only that edit's renders are listed (`renderingEditId` → "Rendering another version"); one or two crop boxes on the landscape video, a live canvas preview with the
  split pill, a bar (play, length, I/O, volume, back), Render (`POST /shortform`, polled) and the clip's vertical versions (play in the 9:16 frame, download, "Use these
  settings", delete; "Matches" on the one made from what's on screen). It opens with the newest render's layout. Added: **layouts** (built-in
  presets made for the video's shape, plus your own saved in `localStorage` `clipco.vertical.presets`; team-wide presets would need the
  backend), **safe zones** overlay (TikTok / Shorts / Reels, approximate; `clipco.vertical.safeZone`), **snapping** of boxes to the video's
  edges, middle and each other (shares `clipLibrary.snap`; Alt drags freely, dashed guides), a selected box moves with the arrow keys,
  **undo/redo** of box/layout/split/trim/part changes (50 steps, page only; while it's open Ctrl+Z, its buttons and the tools bar's undo
  whichever of it and the Player 2 edit changed last, by `nextUndoSeq()` numbers: `screenUndo` in `ui/editor.js`), keys in keybinds.js scopes `vertical` / `vertical-box`.
  **Parts** (vertical part 2, badrfly 2026-11-15): S splits the file at the playhead into parts, each with its own layout (`vx.parts`, a
  partition of the whole file; the part under the playhead is live in `vx.mode/ratio/boxes`, `stash()` / `loadPart()`); presets, One/Two boxes
  and box edits change that part only ("Use this layout on every part" copies it). Delete leaves a part out (skipped in the preview, the render
  closes the gap); J joins it with the part before (which keeps its layout). No trim
  handles (badrfly): I / O leave out everything before / after the playhead (`leaveOutSide`), the bar's reset puts every part back, and a
  render's `trimStart`/`trimEnd` opens as left-out parts at the ends (`trimToParts`); the bar shows the vertical video's length.
  `currentLayout()` sends kept parts inside the trim with same-looking neighbors joined: one → a plain layout as before, more → `parts`.
  The parts show in a **Layout row** under the clip in the Player 2 timeline (`hooks.onParts` → `Timeline.setVerticalParts`, file seconds
  mapped to source time with `fileToSource`; `hasLayoutRow` / `drawVerticalParts` / `layoutPartAt`): a white line at each split, "n · One box /
  Two boxes / Left out" labels, the part under the playhead with a white bar, left-out parts hatched; click seeks. Dragging a split line moves it, dragging a part
  slides it (keeps its length, pushes into its neighbors; the file's start/end stay put; parts ≥ 0.5 s; snaps like other edges; one
  undo step): `layoutGrab` / `dragLayout` in `ui/timeline-edit.js` → `hooks.onLayoutParts` → `setVerticalPartStarts`. `vx.trim` is always the
  whole file (a longer new file used to stay cut at the old end, drawn dark and not rendered). **Moving boxes** (keyframes, badrfly
  2026-11-15): a part can have `keys` (`[{ t, boxes }]`, file seconds; at most 30); its boxes glide between them with smoothstep easing
  (`boxesAt`, the same curve as the server's `ShortformLayout.Ease`) and hold before the first / after the last; mode and split ratio stay
  fixed. F adds a keyframe at the playhead or removes the one there, `[` / `]` go to the previous / next; once a part has one, moving a box
  (drag, arrows, a preset) sets the keyframe at the playhead (`keyHere`). Splitting keeps each side's keyframes plus the nearest one past
  the split, so motion doesn't change; J keeps the earlier part's; "Use this layout on every part" drops them. Diamonds on the Layout row
  (click = go there). The Player 2
  timeline stays below and follows the preview (`fileToSource` / `sourceToFile` in `ui/editor.js`: files keep the whole clip, so file
  seconds are source time, except an old file with `fileHasCuts`, which skips the clip's cut-out parts); a changed file is reloaded
  (`verticalEntryChanged`), and a clip without a file says why (long clips get "Make
  the file", `POST /generate`). While it's open only the timeline's tool and zoom keys still work.
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
  `TeamPermission.DownloadTeamVerticals` ("Download team vertical entries", bit 18) any of the team's renders of that clip they pick; neither
  → the page grays the checkbox out. Downloading a render from the editor's list is still `ViewLibrary`.

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
