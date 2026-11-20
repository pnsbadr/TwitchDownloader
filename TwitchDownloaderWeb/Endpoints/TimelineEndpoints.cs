using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="ReferenceOnly">Not stored: a range of the VOD (re-cutting it doesn't download anything unless it gets short).</param>
    /// <param name="CanEdit">The player may move its start/end (PUT /trim): a VOD entry, still on Twitch, that this member may edit.</param>
    public sealed record TimelineClipDto(int Id, string Title, string Kind, double Start, double? End,
        bool ReferenceOnly = false, bool FollowsMarkers = false, bool CanEdit = false)
    {
        /// <summary>The clip's color (null = the default violet).</summary>
        public string? Color { get; init; }

        /// <summary>Its VOD (another streamer's on a synced row).</summary>
        public long VodId { get; init; }

        /// <summary>The clip row it sits on; null for clips without a known adder (made before accounts).</summary>
        public int? RowId { get; init; }

        public string? AddedByUserId { get; init; }

        /// <summary>The adder's username.</summary>
        public string? CreatedBy { get; init; }

        public int? CopiedFromEntryId { get; init; }

        /// <summary>Cut-out parts (VOD seconds, inside Start..End); left out of exports and lengths, not of the clip's own file.</summary>
        public IReadOnlyList<CutRange> Cuts { get; init; } = [];

        /// <summary>What's left after the cut-out parts (the stored-clip limit counts the whole range, End - Start).</summary>
        public double LengthSeconds { get; init; }

        /// <summary>The clip's file was made the old way, without its cut-out parts; false = it covers the whole range (see <see cref="EntryDto.FileHasCuts"/>).</summary>
        public bool FileHasCuts { get; init; }
    }

    /// <param name="UserId">The member who owns it and everything on it.</param>
    /// <param name="Kind">"Video" (segments and moments) or "Clip".</param>
    /// <param name="Position">Order among the owner's rows on this VOD.</param>
    /// <param name="CanEdit">The caller may put items on it, rename, move and delete it (their own row).</param>
    public sealed record TimelineRowDto(int Id, int UserId, string? Username, string Kind, int Position, string? Name, bool CanEdit)
    {
        /// <summary>The VOD its items are on: the timeline's own, or another streamer's for a synced row.</summary>
        public long VodId { get; init; }

        /// <summary>A synced row: the VOD whose timeline it also shows on.</summary>
        public long? ShownOnVodId { get; init; }

        /// <summary>A synced row on the timeline asked for: its VOD's second = the timeline's second + this (start times plus the pair's
        /// saved line-up). Null on its own VOD's timeline.</summary>
        public double? Shift { get; init; }

        public static TimelineRowDto From(TimelineRow row, IReadOnlyDictionary<int, string> names, TeamContext team, double? shift = null) =>
            new(row.Id, row.UserId, names.GetValueOrDefault(row.UserId), row.Kind.ToString(), row.Position, row.Name,
                !team.IsSupport && row.UserId == team.UserId && team.Can(TeamPermission.AddEntries))
            {
                VodId = row.VodId, ShownOnVodId = row.ShownOnVodId, Shift = shift is { } s ? Math.Round(s, 3) : null,
            };
    }

    /// <param name="Kind">"video" or "clip".</param>
    /// <param name="SyncedVodId">A synced row (badrfly 2026-11-20): another streamer's VOD (one the team can open) shown on this VOD's
    /// timeline, lined up by stream start times and the pair's saved line-up. Its items are that VOD's.</param>
    public sealed record AddRowRequest(string? Kind, string? Name, long? SyncedVodId = null);

    /// <param name="Name">New name (blank clears it); null leaves it.</param>
    /// <param name="Position">New place among the owner's rows on the VOD (0 first); null leaves it.</param>
    public sealed record UpdateRowRequest(string? Name, int? Position);

    public sealed record MoveToRowRequest(int? RowId);

    public sealed record MarkerDto(int Id, long VodId, double Seconds, double? EndSeconds, string Label, string Color, int CreatedByUserId, string? CreatedBy,
        DateTime CreatedAtUtc, bool CanDelete)
    {
        /// <summary>The creator's video row it sits on.</summary>
        public int? RowId { get; init; }

        /// <summary>The segment/moment it was copied from.</summary>
        public int? CopiedFromMarkerId { get; init; }

        /// <summary>The clip it was made from (Alt+drag).</summary>
        public int? CopiedFromEntryId { get; init; }

        /// <summary>A segment's cut-out parts (VOD seconds, inside its range); a moment has none.</summary>
        public IReadOnlyList<CutRange> Cuts { get; init; } = [];

        /// <summary>A segment's length without its cut-out parts; null for a moment.</summary>
        public double? KeptSeconds { get; init; }

        /// <summary>Whether this user may move, resize, split, recolor or retitle it (own with AddEntries, anyone's with EditAnyEntries).</summary>
        public bool CanEdit { get; init; }

        /// <summary>Set when saving the marker made or moved the VOD's live markers entry (see <see cref="LiveMarkerEntryService"/>).</summary>
        public LiveEntryChange? LiveEntry { get; init; }
    }

    /// <param name="MaxStoredClipSeconds">The stored-clip limit (Downloads:MaxStoredClipSeconds): the player's clip tool and a clip's
    /// review never make a clip longer than this.</param>
    /// <param name="Rows">Every member's rows on the VOD (see <see cref="TimelineRow"/>), by owner then position.</param>
    public sealed record TimelineDto(IReadOnlyList<TimelineClipDto> Clips, IReadOnlyList<MarkerDto> Markers, int MaxStoredClipSeconds,
        IReadOnlyList<TimelineRowDto> Rows)
    {
        /// <summary>The team's videos (linked segments) on the VOD.</summary>
        public IReadOnlyList<VideoDto> Videos { get; init; } = [];
    }

    /// <summary>A library entry's place on its VOD, for the strips on library and VOD cards.</summary>
    public sealed record EntryRangeDto(int Id, long VodId, string Title, string Kind, double Start, double? End, bool ReferenceOnly);

    /// <param name="RowId">Adding: one of the caller's video rows on the VOD (blank = their first, made if needed). Changing: another video row
    /// of the marker's creator on the VOD (blank = stays where it is).</param>
    /// <param name="Cuts">A segment's cut-out parts in VOD seconds (see <see cref="CutOuts"/>). Changing: null keeps the ones it has (clipped
    /// to the new range), [] removes them.</param>
    /// <param name="Restore">Adding only: the page's undo of a delete. With DeleteAnyEntries (needed to have deleted it) <c>RowId</c> may then
    /// be another member's video row, and the marker is made again as theirs, where it was (badrfly 2026-11-16: undo used to bring a
    /// teammate's segment back on your own row).</param>
    public sealed record AddMarkerRequest(double? Seconds, double? EndSeconds, string? Label, string? Color, int? RowId = null,
        IReadOnlyList<CutRange>? Cuts = null, bool Restore = false);

    /// <summary>
    /// What the player's timeline shows besides Twitch's muted parts (which the page reads from the playlist itself): the team's
    /// library entries cut from the VOD, and the team's markers on it.
    /// </summary>
    public static class TimelineEndpoints
    {
        public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/{vodId:long}/timeline", GetTimeline).RequireTeam(TeamPermission.ViewLibrary).RequireChannelVod();
            // Markers and segments of several VODs at once, for the library and the VOD browser cards
            app.MapGet("/api/markers", ListMarkers).RequireTeam(TeamPermission.ViewLibrary);
            // The team's library entries on several VODs at once (where they sit), for the same cards
            app.MapGet("/api/timeline/entries", ListEntryRanges).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPost("/api/vods/{vodId:long}/markers", AddMarker).RequireTeam(TeamPermission.AddEntries).RequireChannelVod();
            // Own markers need AddEntries (as when they were made); anyone's need EditAnyEntries / DeleteAnyEntries. Checked in the handler.
            app.MapPut("/api/markers/{id:int}", UpdateMarker).RequireTeam(TeamPermission.AddEntries | TeamPermission.EditAnyEntries);
            app.MapDelete("/api/markers/{id:int}", DeleteMarker).RequireTeam(TeamPermission.AddEntries | TeamPermission.DeleteAnyEntries);
            app.MapPost("/api/markers/{id:int}/restore", RestoreMarker).RequireTeam(TeamPermission.AddEntries | TeamPermission.DeleteAnyEntries);
            // Rows (badrfly, 2026-11-15): members add, rename, reorder and delete their own; DeleteAnyEntries may delete anyone's
            app.MapPost("/api/vods/{vodId:long}/rows", AddRow).RequireTeam(TeamPermission.AddEntries).RequireChannelVod();
            app.MapPut("/api/rows/{id:int}", UpdateRow).RequireTeam(TeamPermission.AddEntries);
            app.MapDelete("/api/rows/{id:int}", DeleteRow).RequireTeam(TeamPermission.AddEntries | TeamPermission.DeleteAnyEntries);
            app.MapPut("/api/entries/{id:int}/row", MoveEntryToRow).RequireTeam(TeamPermission.EditOwnEntries | TeamPermission.EditAnyEntries);
            return app;
        }

        private static bool CanDelete(TeamContext team, VodMarker marker) =>
            team.CanOnOwnOrAny(marker.CreatedByUserId.ToString(), TeamPermission.AddEntries, TeamPermission.DeleteAnyEntries);

        internal static bool CanEdit(TeamContext team, VodMarker marker) =>
            team.CanOnOwnOrAny(marker.CreatedByUserId.ToString(), TeamPermission.AddEntries, TeamPermission.EditAnyEntries);

        /// <summary>
        /// The team's clips, markers and rows on the VOD. A member who may add things (AddEntries) and opens the VOD for the first time
        /// gets one video row and one clip row on it here.
        /// </summary>
        private static async Task<IResult> GetTimeline(long vodId, TeamContext team, LibraryDbContext db, IOptions<DownloadSettings> settings,
            CancellationToken cancellationToken)
        {
            if (!team.IsSupport && team.Can(TeamPermission.AddEntries))
                await EnsureFirstRowsAsync(db, team, vodId, cancellationToken);

            var clips = await db.Entries.AsNoTracking()
                .Where(x => x.VodId == vodId)
                .OrderBy(x => x.AddedAtUtc)
                .Select(x => new { x.Id, x.Title, x.Kind, x.StartTime, x.EndTime, x.Status, x.FollowsMarkers, x.AddedByUserId, x.RowId, x.CopiedFromEntryId, x.Cuts,
                    x.ClipLengthSeconds, x.VodLengthSeconds, x.FileCuts, x.FilePath, x.GeneratedFilePath, x.Color, x.VodId })
                .ToListAsync(cancellationToken);

            // Synced rows shown here (other streamers' VODs) bring their items along
            var synced = await db.TimelineRows.AsNoTracking().Where(x => x.ShownOnVodId == vodId && x.VodId != vodId).ToListAsync(cancellationToken);
            var syncedIds = synced.Select(x => x.Id).ToList();
            if (syncedIds.Count > 0)
                clips.AddRange(await db.Entries.AsNoTracking()
                    .Where(x => x.RowId != null && syncedIds.Contains(x.RowId.Value))
                    .OrderBy(x => x.AddedAtUtc)
                    .Select(x => new { x.Id, x.Title, x.Kind, x.StartTime, x.EndTime, x.Status, x.FollowsMarkers, x.AddedByUserId, x.RowId, x.CopiedFromEntryId, x.Cuts,
                        x.ClipLengthSeconds, x.VodLengthSeconds, x.FileCuts, x.FilePath, x.GeneratedFilePath, x.Color, x.VodId })
                    .ToListAsync(cancellationToken));
            var markers = await db.Markers.AsNoTracking()
                .Where(x => x.RemovedAtUtc == null && (x.VodId == vodId || (x.RowId != null && syncedIds.Contains(x.RowId.Value)))).ToListAsync(cancellationToken);
            var rows = await db.TimelineRows.AsNoTracking().Where(x => x.VodId == vodId).ToListAsync(cancellationToken);
            var shifts = await SyncShiftsAsync(db, vodId, synced, cancellationToken);
            rows.AddRange(synced);
            var markerIds = markers.Select(x => x.Id).ToList();
            var videos = await db.Videos.AsNoTracking().Include(x => x.Segments).ThenInclude(x => x.Marker)
                .Where(x => x.VodId == vodId || x.Segments.Any(s => markerIds.Contains(s.MarkerId))).OrderBy(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
            var names = await UserNamesAsync(db, markers.Select(x => x.CreatedByUserId).Concat(rows.Select(x => x.UserId))
                .Concat(clips.Select(x => int.TryParse(x.AddedByUserId, out var u) ? u : 0)), cancellationToken);

            return Results.Ok(new TimelineDto(
                clips.Select(x => new TimelineClipDto(x.Id, x.Title, x.Kind.ToString(), x.StartTime.TotalSeconds, x.EndTime?.TotalSeconds,
                    x.Status == EntryStatus.ReferenceOnly, x.FollowsMarkers,
                    x.Kind == EntryKind.Vod && x.Status != EntryStatus.VodUnavailable
                        && team.CanOnOwnOrAny(x.AddedByUserId, TeamPermission.EditOwnEntries, TeamPermission.EditAnyEntries))
                {
                    RowId = x.RowId,
                    Color = x.Color,
                    VodId = x.VodId,
                    AddedByUserId = x.AddedByUserId,
                    CreatedBy = int.TryParse(x.AddedByUserId, out var u) ? names.GetValueOrDefault(u) : null,
                    CopiedFromEntryId = x.CopiedFromEntryId,
                    Cuts = x.Kind == EntryKind.Vod ? CutOuts.Normalize(CutOuts.Read(x.Cuts), x.StartTime.TotalSeconds, x.EndTime?.TotalSeconds ?? x.VodLengthSeconds) : [],
                    LengthSeconds = x.ClipLengthSeconds,
                    FileHasCuts = x.Kind == EntryKind.Vod && CutOuts.Read(x.FileCuts).Count > 0 && (x.FilePath is not null || x.GeneratedFilePath is not null),
                }).ToList(),
                markers.OrderBy(x => x.Seconds).Select(x => ToDto(x, names, team)).ToList(),
                settings.Value.MaxStoredClipSeconds,
                // The caller's rows first, then the others' by name
                rows.OrderBy(x => x.UserId != team.UserId).ThenBy(x => names.GetValueOrDefault(x.UserId), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.UserId).ThenBy(x => x.ShownOnVodId == vodId) // a member's synced rows after their own
                    .ThenBy(x => x.Position).ThenBy(x => x.Id)
                    .Select(x => TimelineRowDto.From(x, names, team, x.ShownOnVodId == vodId ? shifts.GetValueOrDefault(x.Id) : null)).ToList())
            {
                Videos = await VideoEndpoints.ToDtosAsync(db, videos, team, cancellationToken),
            });
        }

        /// <summary>Each synced row's shift on <paramref name="vodId"/>'s timeline: its VOD's second = this VOD's + shift.</summary>
        internal static async Task<Dictionary<int, double>> SyncShiftsAsync(LibraryDbContext db, long vodId, IReadOnlyList<TimelineRow> synced,
            CancellationToken cancellationToken)
        {
            if (synced.Count == 0)
                return [];
            var others = synced.Select(x => x.VodId).Distinct().ToList();
            var pairs = await db.VodSyncOffsets.AsNoTracking()
                .Where(x => (x.VodA == vodId && others.Contains(x.VodB)) || (x.VodB == vodId && others.Contains(x.VodA)))
                .ToListAsync(cancellationToken);
            return synced.ToDictionary(x => x.Id, x =>
            {
                var pair = pairs.FirstOrDefault(p => p.VodA == x.VodId || p.VodB == x.VodId);
                return (x.SyncBaseSeconds ?? 0) + (pair is null ? 0 : VodSyncEndpoints.CorrectionFrom(pair, vodId));
            });
        }

        /// <summary>The first time a member opens a VOD they get one video row and one clip row on it (unless they already have one of that kind).</summary>
        private static async Task EnsureFirstRowsAsync(LibraryDbContext db, TeamContext team, long vodId, CancellationToken cancellationToken)
        {
            if (await db.MemberVods.AnyAsync(x => x.UserId == team.UserId && x.VodId == vodId, cancellationToken))
                return;

            var kinds = await db.TimelineRows.Where(x => x.VodId == vodId && x.UserId == team.UserId && x.ShownOnVodId == null).Select(x => x.Kind).Distinct()
                .ToListAsync(cancellationToken);
            var next = await db.TimelineRows.Where(x => x.VodId == vodId && x.UserId == team.UserId).Select(x => (int?)x.Position)
                .MaxAsync(cancellationToken) ?? -1;
            var now = DateTime.UtcNow;
            db.MemberVods.Add(new MemberVod { UserId = team.UserId, VodId = vodId, FirstOpenedAtUtc = now });
            foreach (var kind in new[] { TimelineRowKind.Video, TimelineRowKind.Clip }.Where(x => !kinds.Contains(x)))
                db.TimelineRows.Add(new TimelineRow { VodId = vodId, UserId = team.UserId, Kind = kind, Position = ++next, CreatedAtUtc = now });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another request of the same member got there first (unique MemberVod); its rows count
                db.ChangeTracker.Clear();
            }
        }

        /// <summary>Checks that an item may go on the row. Returns the problem to answer with, or null.</summary>
        internal static async Task<IResult?> CheckRowAsync(LibraryDbContext db, int rowId, long vodId, TimelineRowKind kind, int ownerUserId,
            CancellationToken cancellationToken)
        {
            var row = await db.TimelineRows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rowId, cancellationToken);
            if (row is null || row.VodId != vodId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rowId"] = ["That row isn't on this VOD."] });
            if (row.Kind != kind)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["rowId"] = [kind == TimelineRowKind.Clip ? "Clips go on clip rows." : "Segments and moments go on video rows."] });
            if (row.UserId != ownerUserId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rowId"] = ["Items can only go on their creator's own rows."] });
            return null;
        }

        private static (TimelineRowKind? Kind, string? Name, Dictionary<string, string[]> Errors) ValidateRow(string? kindText, string? nameText, bool kindRequired)
        {
            var errors = new Dictionary<string, string[]>();
            TimelineRowKind? kind = kindText?.Trim().ToLowerInvariant() switch
            {
                "video" => TimelineRowKind.Video,
                "clip" => TimelineRowKind.Clip,
                _ => null,
            };
            if (kind is null && (kindRequired || !string.IsNullOrWhiteSpace(kindText)))
                errors["kind"] = ["Kind must be video or clip."];

            var name = string.IsNullOrWhiteSpace(nameText) ? null : nameText.Trim();
            if (name is { Length: > TimelineRow.MaxNameLength })
                errors["name"] = [$"Keep the name to {TimelineRow.MaxNameLength} characters."];
            return (kind, name, errors);
        }

        private static async Task<IResult> AddRow(long vodId, AddRowRequest request, TeamContext team, LibraryDbContext db, IVodDetailsSource vods,
            TeamChannelAccess access, CancellationToken cancellationToken)
        {
            var (kind, name, errors) = ValidateRow(request.Kind, request.Name, kindRequired: true);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            // A synced row is a row of the other VOD, shown on this one's timeline
            double? syncBase = null;
            var rowVod = vodId;
            if (request.SyncedVodId is { } other)
            {
                if (other == vodId || other <= 0)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["syncedVodId"] = ["Pick another streamer's VOD."] });
                if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, other, cancellationToken), other) is { } refused)
                    return refused;
                VodDetails? here, there;
                try
                {
                    here = await vods.GetVodAsync(vodId);
                    there = await vods.GetVodAsync(other);
                }
                catch (HttpRequestException)
                {
                    return ChannelEndpoints.TwitchUnreachable();
                }
                if (here is null || there is null)
                    return Results.Problem(detail: "Twitch no longer has one of these VODs, so their stream times aren't known.",
                        statusCode: StatusCodes.Status404NotFound, extensions: new Dictionary<string, object?> { ["code"] = "vod-gone" });
                syncBase = (here.StreamDateUtc - there.StreamDateUtc).TotalSeconds;
                rowVod = other;
                name ??= there.ChannelName is { Length: > 0 } channel ? channel[..Math.Min(channel.Length, TimelineRow.MaxNameLength)] : null;
            }

            var mine = await db.TimelineRows.Where(x => x.VodId == rowVod && x.UserId == team.UserId).Select(x => x.Position).ToListAsync(cancellationToken);
            if (mine.Count >= TimelineRow.MaxRowsPerMember)
                return Results.Problem(detail: $"You can have at most {TimelineRow.MaxRowsPerMember} rows on a VOD.", statusCode: StatusCodes.Status409Conflict);

            var row = new TimelineRow
            {
                VodId = rowVod, UserId = team.UserId, Kind = kind!.Value, Name = name, Position = mine.Count == 0 ? 0 : mine.Max() + 1,
                CreatedAtUtc = DateTime.UtcNow, ShownOnVodId = syncBase is null ? null : vodId, SyncBaseSeconds = syncBase,
            };
            db.TimelineRows.Add(row);
            await db.SaveChangesAsync(cancellationToken);

            var names = await UserNamesAsync(db, [team.UserId], cancellationToken);
            var shift = syncBase is null ? (double?)null : (await SyncShiftsAsync(db, vodId, [row], cancellationToken))[row.Id];
            return Results.Created($"/api/rows/{row.Id}", TimelineRowDto.From(row, names, team, shift));
        }

        /// <summary>Renames the caller's row or moves it among their rows on the VOD.</summary>
        private static async Task<IResult> UpdateRow(int id, UpdateRowRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var row = await db.TimelineRows.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (row is null)
                return Results.Problem(detail: "That row is gone.", statusCode: StatusCodes.Status404NotFound);
            if (row.UserId != team.UserId)
                return Results.Problem(detail: "Only the row's owner can change it.", statusCode: StatusCodes.Status403Forbidden);

            var (_, name, errors) = ValidateRow(null, request.Name, kindRequired: false);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            if (request.Name is not null)
                row.Name = name;

            if (request.Position is { } position)
            {
                var mine = await db.TimelineRows.Where(x => x.VodId == row.VodId && x.UserId == row.UserId && x.Id != row.Id)
                    .OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(cancellationToken);
                mine.Insert(Math.Clamp(position, 0, mine.Count), row);
                for (var i = 0; i < mine.Count; i++)
                    mine[i].Position = i;
            }

            await db.SaveChangesAsync(cancellationToken);
            var names = await UserNamesAsync(db, [row.UserId], cancellationToken);
            return Results.Ok(TimelineRowDto.From(row, names, team));
        }

        /// <summary>
        /// Deletes a row with its segments and moments (own row, or anyone's with DeleteAnyEntries). A clip row that still holds clips is
        /// refused (409 <c>row-has-clips</c>): clips are library entries with files, deleted one by one.
        /// </summary>
        /// <param name="videos">As for deleting a segment: <c>keep</c> leaves the row's segments in the videos that link them.</param>
        private static async Task<IResult> DeleteRow(int id, string? videos, TeamContext team, LibraryDbContext db, LiveMarkerEntryService liveEntries,
            CancellationToken cancellationToken)
        {
            if (ParseVideosMode(videos) is not { } keep)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["videos"] = ["Use unlink or keep."] });
            var row = await db.TimelineRows.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (row is null)
                return Results.Problem(detail: "That row is gone.", statusCode: StatusCodes.Status404NotFound);

            var own = row.UserId == team.UserId && team.Can(TeamPermission.AddEntries);
            if (!own && !team.Can(TeamPermission.DeleteAnyEntries))
                return Results.Problem(detail: "Your role only lets you delete your own rows.", statusCode: StatusCodes.Status403Forbidden);

            var clipCount = await db.Entries.CountAsync(x => x.RowId == row.Id, cancellationToken);
            if (clipCount > 0)
                return Results.Problem(detail: $"This row still has {clipCount} clip{(clipCount == 1 ? "" : "s")}. Delete or move them first.",
                    statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = "row-has-clips" });

            var markers = await db.Markers.Where(x => x.RowId == row.Id).ToListAsync(cancellationToken);
            var rowMarkerIds = markers.Select(x => x.Id).ToList();
            var linkedIds = keep
                ? (await db.VideoSegments.Where(x => rowMarkerIds.Contains(x.MarkerId)).Select(x => x.MarkerId).ToListAsync(cancellationToken)).ToHashSet()
                : [];
            var now = DateTime.UtcNow;
            foreach (var marker in markers)
            {
                if (linkedIds.Contains(marker.Id))
                {
                    marker.RemovedAtUtc ??= now;
                    marker.RowId = null; // the row goes; the segment stays in its videos only
                }
                else
                    db.Markers.Remove(marker);
            }
            db.TimelineRows.Remove(row);
            var names = await UserNamesAsync(db, [row.UserId], cancellationToken);
            db.Audit(AuditActions.RowDeleted, null,
                $"VOD {row.VodId} {row.Kind.ToString().ToLowerInvariant()} row{(row.Name is null ? "" : $" \"{row.Name}\"")} of {names.GetValueOrDefault(row.UserId)}"
                + (markers.Count > 0 ? $" with {markers.Count} segment{(markers.Count == 1 ? "" : "s")}/moment{(markers.Count == 1 ? "" : "s")}" : "")
                + (linkedIds.Count > 0 ? $" ({linkedIds.Count} kept in videos)" : ""));
            await db.SaveChangesAsync(cancellationToken);

            await VideoEndpoints.DeleteEmptyAsync(db, row.VodId, cancellationToken);
            if (markers.Count > 0)
                await liveEntries.MarkersChangedAsync(db, row.VodId, cancellationToken, mayCreate: false);
            return Results.NoContent();
        }

        /// <summary>Moves a clip to another clip row of the member who added it, on the same VOD.</summary>
        private static async Task<IResult> MoveEntryToRow(int id, MoveToRowRequest request, TeamContext team, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entry is null)
                return Results.Problem(detail: $"Entry {id} was not found.", statusCode: StatusCodes.Status404NotFound);
            if (!team.CanOnOwnOrAny(entry.AddedByUserId, TeamPermission.EditOwnEntries, TeamPermission.EditAnyEntries))
                return Results.Problem(detail: "Your role only lets you edit entries you added.", statusCode: StatusCodes.Status403Forbidden);
            if (request.RowId is not { } rowId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rowId"] = ["Pick a row."] });
            if (!int.TryParse(entry.AddedByUserId, out var owner))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rowId"] = ["This clip has no owner to put it under."] });
            if (await CheckRowAsync(db, rowId, entry.VodId, TimelineRowKind.Clip, owner, cancellationToken) is { } problem)
                return problem;

            if (entry.RowId != rowId)
            {
                entry.RowId = rowId;
                db.Audit(AuditActions.EntryMovedRow, entry.Id, $"to row {rowId}");
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        public const int MaxVodIdsPerRequest = 200;

        /// <returns>The VOD IDs, or the validation problem to return.</returns>
        internal static (HashSet<long> Ids, IResult? Problem) ParseVodIds(string? vodIds)
        {
            var ids = new HashSet<long>();
            foreach (var part in (vodIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!long.TryParse(part, out var id) || id <= 0)
                    return (ids, Results.ValidationProblem(new Dictionary<string, string[]> { ["vodIds"] = ["Give VOD IDs separated by commas."] }));
                ids.Add(id);
            }

            if (ids.Count > MaxVodIdsPerRequest)
                return (ids, Results.ValidationProblem(new Dictionary<string, string[]> { ["vodIds"] = [$"Ask for at most {MaxVodIdsPerRequest} VODs at once."] }));

            return (ids, null);
        }

        private static async Task<IResult> ListEntryRanges(string? vodIds, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (ids, problem) = ParseVodIds(vodIds);
            if (problem is not null)
                return problem;

            if (ids.Count == 0)
                return Results.Ok(Array.Empty<EntryRangeDto>());

            var entries = await db.Entries.AsNoTracking()
                .Where(x => ids.Contains(x.VodId))
                .OrderBy(x => x.VodId).ThenBy(x => x.AddedAtUtc)
                .Select(x => new { x.Id, x.VodId, x.Title, x.Kind, x.StartTime, x.EndTime, x.Status })
                .ToListAsync(cancellationToken);
            return Results.Ok(entries.Select(x => new EntryRangeDto(x.Id, x.VodId, x.Title, x.Kind.ToString(), x.StartTime.TotalSeconds,
                x.EndTime?.TotalSeconds, x.Status == EntryStatus.ReferenceOnly)).ToList());
        }

        private static async Task<IResult> ListMarkers(string? vodIds, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (ids, problem) = ParseVodIds(vodIds);
            if (problem is not null)
                return problem;

            if (ids.Count == 0)
                return Results.Ok(Array.Empty<MarkerDto>());

            var markers = await db.Markers.AsNoTracking().Where(x => ids.Contains(x.VodId) && x.RemovedAtUtc == null).ToListAsync(cancellationToken);
            var names = await UserNamesAsync(db, markers.Select(x => x.CreatedByUserId), cancellationToken);
            return Results.Ok(markers.OrderBy(x => x.VodId).ThenBy(x => x.Seconds).Select(x => ToDto(x, names, team)).ToList());
        }

        /// <returns>Errors by field (empty if valid), and the cleaned label and color.</returns>
        private static (Dictionary<string, string[]> Errors, string Label, string Color) Validate(AddMarkerRequest request)
        {
            var errors = new Dictionary<string, string[]>();
            if (request.Seconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0 || seconds > 7 * 24 * 3600)
                errors["seconds"] = ["Give the marker's time in seconds from the start of the VOD."];

            if (request.EndSeconds is { } end
                && (!double.IsFinite(end) || end > 7 * 24 * 3600 || request.Seconds is not { } start || end < start + MinSegmentSeconds))
                errors["endSeconds"] = [$"A segment must end at least {MinSegmentSeconds} s after it starts."];

            if (request.Cuts is { Count: > 0 } cuts)
            {
                if (request.EndSeconds is not { } cutEnd || request.Seconds is not { } cutStart)
                    errors["cuts"] = ["Only segments have cut-out parts."];
                else if (!errors.ContainsKey("endSeconds") && CutOuts.Validate(cuts, cutStart, cutEnd) is { } cutError)
                    errors["cuts"] = [cutError];
            }

            var label = request.Label?.Trim() ?? "";
            if (label.Length > VodMarker.MaxLabelLength)
                errors["label"] = [$"Keep the label to {VodMarker.MaxLabelLength} characters."];

            var color = string.IsNullOrWhiteSpace(request.Color) ? VodMarker.Colors[0] : request.Color.Trim().ToLowerInvariant();
            if (!VodMarker.Colors.Contains(color))
                errors["color"] = [$"Color must be one of {string.Join(", ", VodMarker.Colors)}."];

            return (errors, label, color);
        }

        // makeLiveEntry=false (markers changed in a library card's Review): don't make a live markers entry; one that exists still moves
        private static async Task<IResult> AddMarker(long vodId, AddMarkerRequest request, TeamContext team, LibraryDbContext db, LiveMarkerEntryService liveEntries,
            CancellationToken cancellationToken, bool makeLiveEntry = true)
        {
            var (errors, label, color) = Validate(request);
            if (vodId <= 0)
                errors["vodId"] = ["Unknown VOD."];

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            // Undoing the delete of a teammate's marker puts it back on their row, as theirs
            var ownerUserId = team.UserId;
            if (request.Restore && request.RowId is { } restoreRowId && team.Can(TeamPermission.DeleteAnyEntries)
                && await db.TimelineRows.AsNoTracking().Where(x => x.Id == restoreRowId && x.VodId == vodId).Select(x => (int?)x.UserId)
                    .FirstOrDefaultAsync(cancellationToken) is { } rowOwner)
                ownerUserId = rowOwner;

            if (request.RowId is { } rowId
                && await CheckRowAsync(db, rowId, vodId, TimelineRowKind.Video, ownerUserId, cancellationToken) is { } rowProblem)
                return rowProblem;

            var marker = new VodMarker
            {
                CreatedByUserId = ownerUserId == team.UserId ? 0 : ownerUserId,
                RowId = request.RowId,
                VodId = vodId,
                Seconds = Math.Round(request.Seconds!.Value, 2),
                EndSeconds = request.EndSeconds is { } endSeconds ? Math.Round(endSeconds, 2) : null,
                Label = label,
                Color = color,
                CreatedAtUtc = DateTime.UtcNow,
            };
            marker.Cuts = NormalizedCuts(marker, request.Cuts);
            db.Markers.Add(marker);
            db.Audit(AuditActions.MarkerAdded, null, ownerUserId == team.UserId
                ? $"VOD {vodId} {Describe(marker)}"
                : $"VOD {vodId} {Describe(marker)} (put back on user {ownerUserId}'s row)");
            await db.SaveChangesAsync(cancellationToken);

            // A marker on a live stream makes (or moves) the team's reference entry around its markers
            var liveEntry = await liveEntries.MarkersChangedAsync(db, vodId, cancellationToken, makeLiveEntry);

            var names = await UserNamesAsync(db, [marker.CreatedByUserId], cancellationToken);
            return Results.Created($"/api/markers/{marker.Id}", ToDto(marker, names, team) with { LiveEntry = liveEntry });
        }

        /// <summary>Moves, resizes, recolors or retitles a marker: the body is the whole marker (seconds, endSeconds, label, color).</summary>
        private static async Task<IResult> UpdateMarker(int id, AddMarkerRequest request, TeamContext team, LibraryDbContext db, LiveMarkerEntryService liveEntries,
            CancellationToken cancellationToken, bool makeLiveEntry = true)
        {
            var marker = await db.Markers.FirstOrDefaultAsync(x => x.Id == id && x.RemovedAtUtc == null, cancellationToken);
            if (marker is null)
                return Results.Problem(detail: "That marker is gone.", statusCode: StatusCodes.Status404NotFound);

            if (!CanEdit(team, marker))
                return Results.Problem(detail: "Your role only lets you change markers you added.", statusCode: StatusCodes.Status403Forbidden);

            var (errors, label, color) = Validate(request);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            // A linked segment can't become a moment (videos link segments from any of the owner's rows since 2026-11-20, so moving rows is fine)
            var breaksVideo = request.EndSeconds is null && marker.EndSeconds is not null;
            if (breaksVideo && await VideoEndpoints.TitlesLinkingAsync(db, marker.Id, cancellationToken) is { Count: > 0 } linked)
                return Results.Problem(detail: $"This segment is in the video \"{linked[0]}\". Unlink it there first.", statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["code"] = "segment-in-video" });

            if (request.RowId is { } rowId && rowId != marker.RowId)
            {
                if (await CheckRowAsync(db, rowId, marker.VodId, TimelineRowKind.Video, marker.CreatedByUserId, cancellationToken) is { } rowProblem)
                    return rowProblem;
                marker.RowId = rowId;
            }

            var before = $"{Describe(marker)} ({marker.Color})";
            var cuts = request.Cuts ?? CutOuts.Read(marker.Cuts);
            var seconds = Math.Round(request.Seconds!.Value, 2);
            if (request.EndSeconds is { } newEnd && CutOuts.Validate(cuts, seconds, Math.Round(newEnd, 2)) is { } cutError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cuts"] = [cutError] });

            marker.Seconds = seconds;
            marker.EndSeconds = request.EndSeconds is { } endSeconds ? Math.Round(endSeconds, 2) : null;
            marker.Label = label;
            marker.Color = color;
            marker.Cuts = NormalizedCuts(marker, cuts);
            var after = $"{Describe(marker)} ({marker.Color})";
            LiveEntryChange? liveEntry = null;
            if (db.ChangeTracker.HasChanges())
            {
                db.Audit(AuditActions.MarkerChanged, null, $"VOD {marker.VodId} {before} -> {after}");
                await db.SaveChangesAsync(cancellationToken);
                liveEntry = await liveEntries.MarkersChangedAsync(db, marker.VodId, cancellationToken, makeLiveEntry);
            }

            var names = await UserNamesAsync(db, [marker.CreatedByUserId], cancellationToken);
            return Results.Ok(ToDto(marker, names, team) with { LiveEntry = liveEntry });
        }

        /// <param name="videos">What happens to videos that link the segment (badrfly 2026-11-20, the page asks): <c>unlink</c> (default) takes
        /// it out of them (a video left with none is deleted), <c>keep</c> leaves them as they are: the segment leaves its timeline but stays in
        /// them (<see cref="VodMarker.RemovedAtUtc"/>).</param>
        private static async Task<IResult> DeleteMarker(int id, string? videos, TeamContext team, LibraryDbContext db, LiveMarkerEntryService liveEntries,
            CancellationToken cancellationToken, bool makeLiveEntry = true)
        {
            if (ParseVideosMode(videos) is not { } keep)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["videos"] = ["Use unlink or keep."] });
            var marker = await db.Markers.FirstOrDefaultAsync(x => x.Id == id && x.RemovedAtUtc == null, cancellationToken);
            if (marker is null)
                return Results.Problem(detail: "That marker is gone.", statusCode: StatusCodes.Status404NotFound);

            if (!CanDelete(team, marker))
                return Results.Problem(detail: "Your role only lets you delete markers you added.", statusCode: StatusCodes.Status403Forbidden);

            var linked = keep ? await VideoEndpoints.TitlesLinkingAsync(db, marker.Id, cancellationToken) : [];
            if (linked.Count > 0)
            {
                marker.RemovedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.MarkerDeleted, null,
                    $"VOD {marker.VodId} {Describe(marker)} (kept in {linked.Count} video{(linked.Count == 1 ? "" : "s")})");
            }
            else
            {
                db.Markers.Remove(marker);
                db.Audit(AuditActions.MarkerDeleted, null, $"VOD {marker.VodId} {Describe(marker)}");
            }
            await db.SaveChangesAsync(cancellationToken);
            await VideoEndpoints.DeleteEmptyAsync(db, marker.VodId, cancellationToken);
            await liveEntries.MarkersChangedAsync(db, marker.VodId, cancellationToken, makeLiveEntry);
            return Results.NoContent();
        }

        /// <returns>true = keep videos as they are, false = unlink, null = not a mode.</returns>
        internal static bool? ParseVideosMode(string? videos) => videos?.Trim().ToLowerInvariant() switch
        {
            null or "" or "unlink" => false,
            "keep" => true,
            _ => null,
        };

        /// <summary>
        /// Puts a segment deleted with "keep videos as they are" back on its timeline (the page's undo): same ID, still in its videos. Its row
        /// if that's still there, else its creator's first video row on the VOD.
        /// </summary>
        private static async Task<IResult> RestoreMarker(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var marker = await db.Markers.FirstOrDefaultAsync(x => x.Id == id && x.RemovedAtUtc != null, cancellationToken);
            if (marker is null)
                return Results.Problem(detail: "That segment is gone.", statusCode: StatusCodes.Status404NotFound);
            if (!CanDelete(team, marker))
                return Results.Problem(detail: "Your role only lets you bring back markers you added.", statusCode: StatusCodes.Status403Forbidden);

            marker.RemovedAtUtc = null;
            if (marker.RowId is null || !await db.TimelineRows.AnyAsync(x => x.Id == marker.RowId, cancellationToken))
                marker.Row = await db.DefaultRowAsync(marker.TeamId, marker.VodId, marker.CreatedByUserId, TimelineRowKind.Video, cancellationToken);
            db.Audit(AuditActions.MarkerAdded, null, $"VOD {marker.VodId} {Describe(marker)} (put back on its timeline)");
            await db.SaveChangesAsync(cancellationToken);

            var names = await UserNamesAsync(db, [marker.CreatedByUserId], cancellationToken);
            return Results.Ok(ToDto(marker, names, team));
        }

        /// <summary>The marker's cut-out parts as stored: none for a moment, otherwise clipped to its range and merged.</summary>
        private static string? NormalizedCuts(VodMarker marker, IReadOnlyList<CutRange>? cuts) =>
            marker.EndSeconds is { } end ? CutOuts.Write(CutOuts.Normalize(cuts, marker.Seconds, end)) : null;

        internal static MarkerDto ToDto(VodMarker x, IReadOnlyDictionary<int, string> names, TeamContext team) =>
            new(x.Id, x.VodId, x.Seconds, x.EndSeconds, x.Label, x.Color, x.CreatedByUserId, names.GetValueOrDefault(x.CreatedByUserId), x.CreatedAtUtc,
                !team.IsSupport && CanDelete(team, x))
            {
                CanEdit = !team.IsSupport && CanEdit(team, x),
                RowId = x.RowId,
                CopiedFromMarkerId = x.CopiedFromMarkerId,
                CopiedFromEntryId = x.CopiedFromEntryId,
                Cuts = x.EndSeconds is { } end ? CutOuts.Normalize(CutOuts.Read(x.Cuts), x.Seconds, end) : [],
                KeptSeconds = x.EndSeconds is { } e ? Math.Round(CutOuts.KeptSeconds(x.Seconds, e, CutOuts.Read(x.Cuts)), 2) : null,
            };

        internal static async Task<Dictionary<int, string>> UserNamesAsync(LibraryDbContext db, IEnumerable<int> userIds, CancellationToken cancellationToken)
        {
            var ids = userIds.Distinct().ToList();
            return await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
        }

        public const double MinSegmentSeconds = 0.5;

        internal static string Describe(VodMarker marker) =>
            (marker.EndSeconds is { } end ? $"from {FormatTime(marker.Seconds)} to {FormatTime(end)}" : $"at {FormatTime(marker.Seconds)}")
            + (marker.Label.Length > 0 ? $": \"{marker.Label}\"" : "")
            + (CutOuts.Read(marker.Cuts).Count is > 0 and var cuts ? $", {cuts} cut-out part{(cuts == 1 ? "" : "s")}" : "");

        private static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }
}
