using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Services;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Shortform;

using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="VodId">A VOD ID or twitch.tv/videos URL, or a clip link (clips.twitch.tv/…, twitch.tv/{channel}/clip/…) or slug.
    /// Clips are always reference-only and ignore TrimMode/StartTime/EndTime: they keep Twitch's cut.</param>
    /// <param name="Title">Blank uses the VOD's (or clip's) Twitch title.</param>
    /// <param name="Quality">e.g. "1080p60". Blank uses the best available quality.</param>
    /// <param name="TrimMode">"Exact" (default) or "Safe".</param>
    /// <param name="StartTime">"h:mm:ss" offset from the start of the VOD. Blank is the start.</param>
    /// <param name="EndTime">"h:mm:ss" offset from the start of the VOD, not a duration. Blank is the end.</param>
    /// <param name="RowId">One of the caller's clip rows on that VOD; blank = their first one (made if they have none).</param>
    /// <param name="Cuts">Cut-out parts in VOD seconds (see <see cref="CutOuts"/>); the stored-clip limit counts the whole range.</param>
    public sealed record AddEntryRequest(string? VodId, string? Title, string? Quality, string? TrimMode, string? StartTime, string? EndTime,
        int? RowId = null, IReadOnlyList<CutRange>? Cuts = null, string? Color = null);

    /// <param name="Title">The new title; blank keeps it when <paramref name="Color"/> is given.</param>
    /// <param name="Color">One of <see cref="VodMarker.Colors"/>; null keeps it.</param>
    public sealed record RenameEntryRequest(string? Title, string? Color = null);

    /// <param name="StartTime">"h:mm:ss" offset from the start of the VOD. Blank is the start.</param>
    /// <param name="EndTime">"h:mm:ss" offset from the start of the VOD, not a duration. Blank is the end.</param>
    /// <param name="FollowMarkers">True = the live markers entry follows its markers again (undo of a hand re-cut). Ignored for other entries.</param>
    /// <param name="Cuts">Cut-out parts in VOD seconds (see <see cref="CutOuts"/>): null keeps the ones it has (clipped to the new range), [] removes them.</param>
    public sealed record RetrimEntryRequest(string? StartTime, string? EndTime, bool? FollowMarkers = null, IReadOnlyList<CutRange>? Cuts = null);

    public sealed record EntryDto(
        int Id,
        string Title,
        string Kind,
        string? ClipSlug,
        long VodId,
        string? ChannelName,
        string? Quality,
        string TrimMode,
        string StartTime,
        string? EndTime,
        double ClipLengthSeconds,
        int VodLengthSeconds,
        DateTime StreamDateUtc,
        DateTime AddedAtUtc,
        DateTime UpdatedAtUtc,
        string? ThumbnailUrl,
        string Status,
        string? Error,
        bool IsReferenceOnly,
        long? FileSizeBytes,
        string GenerationStatus,
        long? GeneratedFileSizeBytes,
        DateTime? GeneratedExpiresAtUtc,
        bool CanDownload,
        double? AudioRestoredSeconds,
        double? AudioMutedSeconds,
        string? AddedByUserId,
        string? AddedBy = null)
    {
        public static EntryDto From(LibraryEntry entry) => new(
            entry.Id,
            entry.Title,
            entry.Kind.ToString(),
            entry.ClipSlug,
            entry.VodId,
            entry.ChannelName,
            entry.Quality,
            entry.TrimMode.ToString(),
            TrimTime.Format(entry.StartTime),
            TrimTime.Format(entry.EndTime),
            entry.ClipLengthSeconds,
            entry.VodLengthSeconds,
            entry.StreamDateUtc,
            entry.AddedAtUtc,
            entry.UpdatedAtUtc,
            entry.ThumbnailUrl,
            entry.Status.ToString(),
            entry.Error,
            entry.Status == EntryStatus.ReferenceOnly || (entry.Status == EntryStatus.VodUnavailable && entry.FilePath is null),
            entry.FileSizeBytes,
            entry.GenerationStatus.ToString(),
            entry.GeneratedFileSizeBytes,
            entry.GeneratedExpiresAtUtc,
            entry.Status == EntryStatus.Ready || HasUnexpiredGeneratedFile(entry),
            entry.AudioRestoredSeconds,
            entry.AudioMutedSeconds,
            entry.AddedByUserId)
        {
            StartSeconds = entry.StartTime.TotalSeconds,
            EndSeconds = entry.EndTime?.TotalSeconds,
            Color = entry.Color,
            TeamStatusId = entry.TeamStatusId,
            FollowsMarkers = entry.FollowsMarkers,
            RowId = entry.RowId,
            CopiedFromEntryId = entry.CopiedFromEntryId,
            FrameUrl = CardThumbnailService.EntryUrl(entry),
            Cuts = entry.Kind == EntryKind.Vod
                ? CutOuts.Normalize(CutOuts.Read(entry.Cuts), entry.StartTime.TotalSeconds, entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds)
                : [],
            FileCuts = HasFileCuts(entry)
                ? CutOuts.Normalize(CutOuts.Read(entry.FileCuts), entry.StartTime.TotalSeconds, entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds)
                : [],
            FileHasCuts = HasFileCuts(entry),
        };

        /// <summary>The entry's file was made before files held the whole range: it leaves out <see cref="LibraryEntry.FileCuts"/> (the same
        /// as its cut-out parts, since changing those makes such a file again).</summary>
        public static bool HasFileCuts(LibraryEntry entry) =>
            entry.Kind == EntryKind.Vod && CutOuts.Read(entry.FileCuts).Count > 0 && (entry.FilePath is not null || entry.GeneratedFilePath is not null);

        /// <summary>How long the entry's file plays: its whole range, or what's left for a file made the old way (<see cref="HasFileCuts"/>).</summary>
        public static double FileSeconds(LibraryEntry entry) =>
            entry.Kind != EntryKind.Vod || HasFileCuts(entry)
                ? entry.ClipLengthSeconds
                : Math.Max(0, (entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds) - entry.StartTime.TotalSeconds);

        /// <summary>See <see cref="HasFileCuts"/>; false = the file covers the clip's whole range.</summary>
        public bool FileHasCuts { get; init; }

        /// <summary>The team's workflow status (<see cref="TeamStatus"/>); null = none. <see cref="Status"/> is the download's.</summary>
        public int? TeamStatusId { get; init; }

        /// <summary>The team's tags on it (lists and single reads; empty elsewhere).</summary>
        public IReadOnlyList<string> Tags { get; init; } = [];

        /// <summary>Cut-out parts (VOD seconds, inside the trim): left out of files made from it and of lengths (<see cref="ClipLengthSeconds"/>
        /// is what's left), but not of the entry's own file (see <see cref="FileCuts"/>).</summary>
        public IReadOnlyList<CutRange> Cuts { get; init; } = [];

        /// <summary>
        /// The parts already left out of the entry's own file (<c>/file</c>): empty for files made since 2026-11-16, which hold the whole
        /// range; a file made before holds its range without these until it's made again. File seconds map to the VOD over the range
        /// without these.
        /// </summary>
        public IReadOnlyList<CutRange> FileCuts { get; init; } = [];

        /// <summary>A frame from the middle of the clip for its card (see <see cref="CardThumbnailService"/>); null for Twitch clips,
        /// which keep Twitch's thumbnail in <see cref="ThumbnailUrl"/> (the VOD's for VOD clips: the page shows it while the frame loads).</summary>
        public string? FrameUrl { get; init; }

        /// <summary>The clip this one was copied from (see <see cref="CopyEndpoints"/>).</summary>
        public int? CopiedFromEntryId { get; init; }

        /// <summary>The clip row it sits on in the VOD's timeline (see <see cref="TimelineRow"/>).</summary>
        public int? RowId { get; init; }

        /// <summary>The clip's color (null = the default violet).</summary>
        public string? Color { get; init; }

        /// <summary>The trim (or a clip's place in its VOD) in seconds, so the page can place the VOD's markers and segments on it.</summary>
        public double StartSeconds { get; init; }

        /// <summary>Null = end of the VOD.</summary>
        public double? EndSeconds { get; init; }

        /// <summary>Made from markers on a live stream; its range follows the VOD's markers (see <see cref="LiveMarkerEntryService"/>).</summary>
        public bool FollowsMarkers { get; init; }

        /// <summary>The cleanup service deletes expired files periodically, so check the expiry itself rather than the status.</summary>
        public static bool HasUnexpiredGeneratedFile(LibraryEntry entry) =>
            entry.Status == EntryStatus.ReferenceOnly
            && entry.GenerationStatus == Data.GenerationStatus.Ready
            && entry.GeneratedExpiresAtUtc > DateTime.UtcNow;
    }

    /// <param name="IsLive">Still streaming: LengthSeconds is only what has been streamed so far.</param>
    public sealed record VodInfoDto(long VodId, string Title, string? ChannelName, int LengthSeconds, string Length, DateTime StreamDateUtc,
        string? ThumbnailUrl, string[] Qualities, bool Restricted, int MaxStoredClipSeconds, bool IsLive);

    public static class EntryEndpoints
    {
        private const int MaxTitleLength = 500;

        public static IEndpointRouteBuilder MapEntryEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/info", GetVodInfo).RequireTeam(TeamPermission.ViewLibrary);

            var entries = app.MapGroup("/api/entries");
            entries.MapPost("/", AddEntry).RequireTeam(TeamPermission.AddEntries);
            entries.MapGet("/", ListEntries).RequireTeam(TeamPermission.ViewLibrary);
            entries.MapGet("/{id:int}", GetEntry).RequireTeam(TeamPermission.ViewLibrary);
            entries.MapPatch("/{id:int}", RenameEntry).RequireTeam(TeamPermission.EditOwnEntries | TeamPermission.EditAnyEntries);
            entries.MapPut("/{id:int}/trim", RetrimEntry).RequireTeam(TeamPermission.EditOwnEntries | TeamPermission.EditAnyEntries);
            entries.MapDelete("/{id:int}", DeleteEntry).RequireTeam(TeamPermission.DeleteOwnEntries | TeamPermission.DeleteAnyEntries);
            entries.MapPost("/{id:int}/generate", GenerateEntry).RequireTeam(TeamPermission.GenerateFiles);
            entries.MapGet("/{id:int}/file", DownloadEntryFile).RequireTeam(TeamPermission.ViewLibrary);
            entries.MapGet("/{id:int}/thumbnail", GetThumbnail).RequireTeam(TeamPermission.ViewLibrary);

            return app;
        }

        private static async Task<IResult> GetVodInfo(string? vod, VodInfoService vodInfo, TeamContext team, TeamChannelAccess access,
            IOptions<DownloadSettings> settings, CancellationToken cancellationToken)
        {
            if (VodInfoService.ParseVodId(vod) is not { } vodId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vod"] = ["Unable to parse VOD ID/URL."] });

            if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vodId, cancellationToken), vodId) is { } refused)
                return refused;

            // Two separate Twitch lookups (video info; access token + master playlist): ask both at once
            var qualitiesTask = vodInfo.GetQualitiesAsync(vodId);
            var details = await vodInfo.GetVodAsync(vodId);
            if (details is null)
            {
                Forget(qualitiesTask);
                return VodNotFound(vodId);
            }

            var qualities = await qualitiesTask;

            return Results.Ok(new VodInfoDto(vodId, details.Title, details.ChannelName, details.LengthSeconds,
                TrimTime.Format(TimeSpan.FromSeconds(details.LengthSeconds)), details.StreamDateUtc, details.ThumbnailUrl,
                qualities is null ? [] : VodInfoService.GetVideoQualityNames(qualities), qualities is null,
                settings.Value.MaxStoredClipSeconds, details.IsLive));
        }

        /// <summary>
        /// A clip or reference cut of a VOD from one of the team's channels (the player's clip tool). Twitch clip links are no longer
        /// accepted (badrfly, 2026-11-15); clip entries added before stay in the library.
        /// </summary>
        private static async Task<IResult> AddEntry(AddEntryRequest request, LibraryDbContext db, VodInfoService vodInfo, TeamContext team,
            TeamChannelAccess access, DownloadQueue queue, IOptions<DownloadSettings> settings, CancellationToken cancellationToken)
        {
            if (VodInfoService.ParseVodId(request.VodId) is not { } vodId)
            {
                if (ClipInfoService.ParseClipSlug(request.VodId) is not null)
                    return Results.Problem(detail: "Twitch clip links can't be added any more. Make clips on your team's VODs instead.",
                        statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?> { ["code"] = "clip-links-disabled" });

                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vodId"] = ["Unable to parse VOD ID/URL."] });
            }

            if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vodId, cancellationToken), vodId) is { } refused)
                return refused;

            if (request.RowId is { } rowId
                && await TimelineEndpoints.CheckRowAsync(db, rowId, vodId, TimelineRowKind.Clip, team.UserId, cancellationToken) is { } rowProblem)
                return rowProblem;

            if (!TrimTime.TryParse(request.StartTime, out var parsedStart))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startTime"] = ["Use h:mm:ss, m:ss or seconds."] });

            if (!TrimTime.TryParse(request.EndTime, out var endTime))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endTime"] = ["Use h:mm:ss, m:ss or seconds."] });

            var trimMode = VideoTrimMode.Exact;
            // Enum.TryParse also accepts numbers like "5", so require one of the names
            if (!string.IsNullOrWhiteSpace(request.TrimMode)
                && !(Enum.TryParse(request.TrimMode, true, out trimMode) && Enum.GetNames<VideoTrimMode>().Contains(request.TrimMode.Trim(), StringComparer.OrdinalIgnoreCase)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["trimMode"] = ["Trim mode must be Exact or Safe."] });

            var requestedQuality = string.IsNullOrWhiteSpace(request.Quality) ? null : request.Quality.Trim();
            if (VodInfoService.IsAudioQuality(requestedQuality))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["quality"] = ["Audio-only quality cannot produce an mp4."] });

            var title = request.Title?.Trim();
            if (title is { Length: > MaxTitleLength })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"Title cannot be longer than {MaxTitleLength} characters."] });

            // Both Twitch lookups at once (the clip tool waits on this request)
            var qualitiesTask = vodInfo.GetQualitiesAsync(vodId);
            var vod = await vodInfo.GetVodAsync(vodId);
            if (vod is null)
            {
                Forget(qualitiesTask);
                return VodNotFound(vodId);
            }

            var startTime = parsedStart ?? TimeSpan.Zero;
            if (VodInfoService.ValidateTrim(startTime, endTime, vod.LengthSeconds) is { } trimErrors)
            {
                Forget(qualitiesTask);
                return Results.ValidationProblem(trimErrors);
            }

            var qualities = await qualitiesTask;
            if (qualities is null)
                return RestrictedVod();

            // Store the resolved name ("720p" -> "720p60") so the entry shows what will actually be downloaded
            string? quality = null;
            if (requestedQuality is not null)
            {
                quality = qualities.GetQuality(requestedQuality)?.Name;
                if (quality is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["quality"] = [$"Quality \"{requestedQuality}\" is not available. Available: {string.Join(", ", VodInfoService.GetVideoQualityNames(qualities))}."]
                    });
                }
            }

            var rangeEnd = endTime?.TotalSeconds ?? vod.LengthSeconds;
            if (CutOuts.Validate(request.Cuts, startTime.TotalSeconds, rangeEnd) is { } cutError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cuts"] = [cutError] });

            // The stored-vs-reference rule is decided here from the trim times, never by the client. It counts the whole range: the
            // stored file holds all of it (cut-out parts are left out only when files are made from it), see IsStoredLength
            var cuts = CutOuts.Normalize(request.Cuts, startTime.TotalSeconds, rangeEnd);
            var clipLength = TimeSpan.FromSeconds(CutOuts.KeptSeconds(startTime.TotalSeconds, rangeEnd, cuts));
            var isStored = IsStoredLength(rangeEnd - startTime.TotalSeconds, settings.Value, await TeamStoresClipsAsync(db, team.TeamId, cancellationToken));
            var now = DateTime.UtcNow;

            var entry = new LibraryEntry
            {
                Title = string.IsNullOrEmpty(title) ? vod.Title : title,
                VodId = vodId,
                ChannelName = vod.ChannelName,
                Quality = quality,
                TrimMode = trimMode,
                StartTime = startTime,
                EndTime = endTime,
                ClipLengthSeconds = clipLength.TotalSeconds,
                VodLengthSeconds = vod.LengthSeconds,
                StreamDateUtc = vod.StreamDateUtc,
                AddedAtUtc = now,
                UpdatedAtUtc = now,
                ThumbnailUrl = vod.ThumbnailUrl,
                Status = isStored ? EntryStatus.Queued : EntryStatus.ReferenceOnly,
                RowId = request.RowId,
                Cuts = CutOuts.Write(cuts),
                Color = request.Color is { } c && c != "violet" && VodMarker.Colors.Contains(c) ? c : null,
            };

            // Two saves because the audit event needs the new entry's ID; the transaction keeps them together
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                db.Entries.Add(entry);
                await db.SaveChangesAsync(cancellationToken);

                db.Audit(AuditActions.EntryAdded, entry.Id,
                    $"VOD {vodId}, {TrimTime.Format(startTime)}-{TrimTime.Format(endTime) ?? "end"}, {clipLength.TotalSeconds:0.###}s, {(isStored ? "stored" : "reference-only")}"
                    + (cuts.Count > 0 ? $", {cuts.Count} cut-out part(s)" : ""));
                await db.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }

            if (isStored)
                queue.Enqueue(entry.Id, DownloadJobKind.Store);

            return Results.Created($"/api/entries/{entry.Id}", EntryDto.From(entry));
        }

        /// <param name="from">Inclusive lower bound, an ISO 8601 instant (e.g. "2026-10-09T07:00:00Z"). Applies to <paramref name="dateField"/>.</param>
        /// <param name="to">Exclusive upper bound, an ISO 8601 instant.</param>
        /// <param name="dateField">"dateAdded" (default) or "streamDate".</param>
        /// <remarks>
        /// Day boundaries are the client's job: it knows which time zone it is showing and converts that zone's midnights
        /// to UTC instants, so the server only ever compares UTC values.
        /// </remarks>
        /// <param name="vodId">Only clips of this VOD.</param>
        /// <param name="madeBy">Only clips this member added.</param>
        /// <param name="editedBy">Only clips this member made an edit of (Player 2).</param>
        /// <param name="channels">Only clips of these team channels (logins, comma separated; the library's channel bar).</param>
        /// <param name="paged">true: answer <c>{ items, nextCursor, total }</c> instead of a plain list (the library pages through thousands).</param>
        /// <param name="cursor">A page's <c>nextCursor</c>: the clips after it in the same sort (<see cref="PageCursor"/>).</param>
        /// <param name="minSeconds">Only clips at least this long (what's kept, <see cref="LibraryEntry.ClipLengthSeconds"/>; the search box's "&gt;1m").</param>
        /// <param name="maxSeconds">Only clips at most this long ("&lt;30s").</param>
        private static async Task<IResult> ListEntries(LibraryDbContext db, string? search, string? sort, string? order, int? limit,
            string? from, string? to, string? dateField, long? vodId, int? madeBy, int? editedBy, string? channels, bool? paged, string? cursor,
            double? minSeconds, double? maxSeconds, string? status, string? tag, TeamContext team, CancellationToken cancellationToken)
        {
            var (statusId, noStatus, tagName, filterProblem) = StatusEndpoints.ParseFilters(status, tag);
            if (filterProblem is not null)
                return filterProblem;

            PageCursor? after = null;
            if (!string.IsNullOrEmpty(cursor) && !PageCursor.TryParse(cursor, out after))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Use the nextCursor of the page before."] });

            if (!TryParseInstant(from, out var fromUtc))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["Use an ISO 8601 date-time with an offset, e.g. 2026-10-09T07:00:00Z."] });

            if (!TryParseInstant(to, out var toUtc))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Use an ISO 8601 date-time with an offset, e.g. 2026-10-10T07:00:00Z."] });

            if (fromUtc >= toUtc)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["\"to\" must be after \"from\"."] });

            var useStreamDate = false;
            switch (dateField?.ToLowerInvariant())
            {
                case null or "" or "dateadded":
                    break;
                case "streamdate":
                    useStreamDate = true;
                    break;
                default:
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateField"] = ["Date field must be streamDate or dateAdded."] });
            }

            var query = db.Entries.AsNoTracking();

            if (await ChannelEndpoints.ChannelNamesAsync(db, channels, cancellationToken) is { } channelNames)
                query = query.Where(x => x.ChannelName != null && channelNames.Contains(x.ChannelName.ToLower()));
            if (vodId is { } vod)
                query = query.Where(x => x.VodId == vod);
            if (madeBy is { } maker)
            {
                var makerText = maker.ToString();
                query = query.Where(x => x.AddedByUserId == makerText);
            }
            if (editedBy is { } editor)
            {
                // Who made which vertical edit is only told to members who may see teammates' (VerticalAccess); horizontal edits are everyone's
                if (editor != team.UserId && !VerticalAccess.CanSeeTeam(team))
                    query = query.Where(x => db.Projects.Any(p => p.EntryId == x.Id && p.UserId == editor && !p.Vertical));
                else
                    query = query.Where(x => db.Projects.Any(p => p.EntryId == x.Id && p.UserId == editor));
            }

            if (fromUtc is { } lower)
                query = useStreamDate ? query.Where(x => x.StreamDateUtc >= lower) : query.Where(x => x.AddedAtUtc >= lower);

            if (toUtc is { } upper)
                query = useStreamDate ? query.Where(x => x.StreamDateUtc < upper) : query.Where(x => x.AddedAtUtc < upper);

            // The team's statuses and tags (badrfly 2026-11-20): a status picked shows only it; otherwise hidden statuses are left out
            if (statusId is { } wanted)
                query = query.Where(x => x.TeamStatusId == wanted);
            else if (noStatus)
                query = query.Where(x => x.TeamStatusId == null);
            else
                query = query.Where(x => x.TeamStatus == null || !x.TeamStatus.HideFromLists);
            if (tagName is not null)
                query = query.Where(x => db.EntryTags.Any(t => t.EntryId == x.Id && t.Tag!.Name == tagName));

            if (minSeconds is { } shortest)
                query = query.Where(x => x.ClipLengthSeconds >= shortest);
            if (maxSeconds is { } longest)
                query = query.Where(x => x.ClipLengthSeconds <= longest);

            if (!string.IsNullOrWhiteSpace(search))
            {
                // Escape LIKE wildcards so the search is a plain substring match. SQLite's LIKE ignores case for ASCII only.
                var pattern = "%" + search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
                query = query.Where(x => EF.Functions.Like(x.Title, pattern, @"\"));
            }

            var ascending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
            var byStreamDate = string.Equals(sort, "streamDate", StringComparison.OrdinalIgnoreCase);
            var total = paged == true ? await query.CountAsync(cancellationToken) : 0;
            if (after is { } a)
            {
                // Keyset paging: the rows after the cursor in the same order (date, then ID), so new clips don't shift pages
                var at = a.AtUtc;
                var id = a.Id;
                query = (byStreamDate, ascending) switch
                {
                    (true, true) => query.Where(x => x.StreamDateUtc > at || (x.StreamDateUtc == at && x.Id > id)),
                    (true, false) => query.Where(x => x.StreamDateUtc < at || (x.StreamDateUtc == at && x.Id < id)),
                    (false, true) => query.Where(x => x.AddedAtUtc > at || (x.AddedAtUtc == at && x.Id > id)),
                    (false, false) => query.Where(x => x.AddedAtUtc < at || (x.AddedAtUtc == at && x.Id < id)),
                };
            }
            query = sort?.ToLowerInvariant() switch
            {
                "streamdate" => ascending ? query.OrderBy(x => x.StreamDateUtc).ThenBy(x => x.Id) : query.OrderByDescending(x => x.StreamDateUtc).ThenByDescending(x => x.Id),
                "dateadded" or null or "" => ascending ? query.OrderBy(x => x.AddedAtUtc).ThenBy(x => x.Id) : query.OrderByDescending(x => x.AddedAtUtc).ThenByDescending(x => x.Id),
                _ => null
            };

            if (query is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = ["Sort must be streamDate or dateAdded."] });

            var take = Math.Clamp(limit ?? 200, 1, 1000);
            var entries = await query.Take(paged == true ? take + 1 : take).ToListAsync(cancellationToken);
            string? next = null;
            if (paged == true && entries.Count > take)
            {
                entries.RemoveAt(take);
                var last = entries[^1];
                next = new PageCursor(byStreamDate ? last.StreamDateUtc : last.AddedAtUtc, last.Id).ToString();
            }
            var names = await UsernamesAsync(db, entries.Select(x => x.AddedByUserId), cancellationToken);
            var tags = await ItemStatuses.EntryTagNamesAsync(db, entries.Select(x => x.Id), cancellationToken);
            var dtos = entries.Select(x => EntryDto.From(x) with
            {
                AddedBy = x.AddedByUserId is { } u ? names.GetValueOrDefault(u) : null,
                Tags = tags.GetValueOrDefault(x.Id) ?? [],
            }).ToList();
            return paged == true ? Results.Ok(new Page<EntryDto>(dtos, next, total)) : Results.Ok(dtos);
        }

        /// <summary>Usernames by user ID text (AddedByUserId). Unknown IDs are left out.</summary>
        internal static async Task<Dictionary<string, string>> UsernamesAsync(LibraryDbContext db, IEnumerable<string?> userIds, CancellationToken cancellationToken)
        {
            var ids = userIds.Select(x => int.TryParse(x, out var id) ? id : (int?)null).OfType<int>().Distinct().ToList();
            return ids.Count == 0 ? []
                : await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id.ToString(), x => x.Username, cancellationToken);
        }

        /// <summary>Parses an instant that states its offset; a bare local time would be ambiguous. Blank parses to null.</summary>
        internal static bool TryParseInstant(string? text, out DateTime? utc)
        {
            utc = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var value = text.Trim();
            var hasOffset = value.EndsWith('Z') || value.EndsWith('z') || Regex.IsMatch(value, @"[+-]\d{2}:?\d{2}$");
            if (!hasOffset || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return false;

            utc = parsed.UtcDateTime;
            return true;
        }

        private static async Task<IResult> GetEntry(int id, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entry is null)
                return EntryNotFound(id);
            var names = await UsernamesAsync(db, [entry.AddedByUserId], cancellationToken);
            var tags = await ItemStatuses.EntryTagNamesAsync(db, [entry.Id], cancellationToken);
            return Results.Ok(EntryDto.From(entry) with
            {
                AddedBy = entry.AddedByUserId is { } u ? names.GetValueOrDefault(u) : null,
                Tags = tags.GetValueOrDefault(entry.Id) ?? [],
            });
        }

        private static async Task<IResult> RenameEntry(int id, RenameEntryRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var title = request.Title?.Trim();
            if (request.Color is { } color && !VodMarker.Colors.Contains(color))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["color"] = [$"Color must be one of: {string.Join(", ", VodMarker.Colors)}."] });
            if (string.IsNullOrEmpty(title) && request.Color is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Title cannot be blank."] });

            if (title?.Length > MaxTitleLength)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"Title cannot be longer than {MaxTitleLength} characters."] });

            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            if (!team.CanOnOwnOrAny(entry.AddedByUserId, TeamPermission.EditOwnEntries, TeamPermission.EditAnyEntries))
                return NotYours("edit");

            if (!string.IsNullOrEmpty(title) && entry.Title != title)
            {
                db.Audit(AuditActions.EntryRenamed, entry.Id, $"\"{entry.Title}\" -> \"{title}\"");
                entry.Title = title;
                entry.UpdatedAtUtc = DateTime.UtcNow;
            }
            if (request.Color is { } newColor && entry.Color != newColor)
            {
                entry.Color = newColor == "violet" ? null : newColor;
                entry.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(EntryDto.From(entry));
        }

        /// <summary>How long a re-trim waits for the entry's running download to stop after cancelling it.</summary>
        private static readonly TimeSpan RetrimCancelWait = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Changes an entry's start/end. Any running download is cancelled and its file deleted; the stored-vs-reference rule is
        /// applied again to the new length, so a stored clip is downloaded again (or becomes reference-only if it got too long).
        /// </summary>
        private static async Task<IResult> RetrimEntry(int id, RetrimEntryRequest request, TeamContext team, LibraryDbContext db, DownloadQueue queue,
            LibraryStorage storage, IVodDetailsSource vods, LiveMarkerEntryService liveEntries, IOptions<DownloadSettings> settings, ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
        {
            if (!TrimTime.TryParse(request.StartTime, out var parsedStart))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startTime"] = ["Use h:mm:ss, m:ss or seconds."] });

            if (!TrimTime.TryParse(request.EndTime, out var endTime))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endTime"] = ["Use h:mm:ss, m:ss or seconds."] });

            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            if (!team.CanOnOwnOrAny(entry.AddedByUserId, TeamPermission.EditOwnEntries, TeamPermission.EditAnyEntries))
                return NotYours("edit");

            if (entry.Kind == EntryKind.Clip)
                return Results.Problem(detail: "Twitch clips keep Twitch's cut. Use the trim in Make vertical to shorten one.", statusCode: StatusCodes.Status409Conflict);

            if (entry.Status == EntryStatus.VodUnavailable)
                return Results.Problem(detail: "The VOD is no longer available on Twitch, so the clip can't be cut again.", statusCode: StatusCodes.Status410Gone);

            var startTime = parsedStart ?? TimeSpan.Zero;
            if (VodInfoService.ValidateTrim(startTime, endTime, entry.VodLengthSeconds) is { } trimErrors)
            {
                // A VOD that was live when the entry was made has grown since: check its length now
                var latest = await LatestVodLengthAsync(entry.VodId, vods, loggerFactory);
                if (latest is not { } length || length <= entry.VodLengthSeconds)
                    return Results.ValidationProblem(trimErrors);
                entry.VodLengthSeconds = length;
                if (VodInfoService.ValidateTrim(startTime, endTime, length) is { } stillWrong)
                    return Results.ValidationProblem(stillWrong);
            }

            var rangeEnd = endTime?.TotalSeconds ?? entry.VodLengthSeconds;
            var requestedCuts = request.Cuts ?? CutOuts.Read(entry.Cuts);
            if (CutOuts.Validate(requestedCuts, startTime.TotalSeconds, rangeEnd) is { } cutError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cuts"] = [cutError] });
            var cuts = CutOuts.Normalize(requestedCuts, startTime.TotalSeconds, rangeEnd);
            var follow = request.FollowMarkers == true && entry.MadeFromMarkers;
            return await RecutAsync(entry, startTime, endTime, cuts, follow, db, queue, storage, liveEntries, settings.Value, null, cancellationToken)
                ?? Results.Ok(EntryDto.From(entry));
        }

        /// <summary>
        /// The stored-vs-reference rule: only for teams the app Owner turned stored clips on for (<see cref="Team.StoreClips"/>, badrfly
        /// 2026-11-18; otherwise every clip is a reference, which the page shows like any clip), and then the whole range counts, not what's left
        /// after cut-out parts, because the stored file holds the whole range (badrfly 2026-11-16; this replaced the 2026-11-15 rule that counted
        /// what was left).
        /// </summary>
        internal static bool IsStoredLength(double rangeSeconds, DownloadSettings settings, bool teamStoresClips) =>
            teamStoresClips && rangeSeconds <= settings.MaxStoredClipSeconds;

        /// <summary>Whether the app Owner turned stored clips on for the team (<see cref="Team.StoreClips"/>).</summary>
        internal static Task<bool> TeamStoresClipsAsync(LibraryDbContext db, int teamId, CancellationToken cancellationToken) =>
            db.Teams.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == teamId).Select(x => x.StoreClips).FirstOrDefaultAsync(cancellationToken);

        /// <summary>
        /// Sets an entry's range and cut-out parts (checked and normalized by the caller) and saves. Files hold the whole range, so a change of
        /// cut-out parts alone keeps the entry's file; a new range (or a file made before files held the whole range) cancels any running
        /// download, deletes the files, applies the stored-vs-reference rule again and re-queues a stored download.
        /// </summary>
        /// <param name="auditAction">Null: audited as a re-trim; else that action with <paramref name="auditDetails"/>.</param>
        /// <returns>A problem (the running download didn't stop in time), or null with the entry saved (reloaded when it follows markers again).</returns>
        internal static async Task<IResult?> RecutAsync(LibraryEntry entry, TimeSpan startTime, TimeSpan? endTime, IReadOnlyList<CutRange> cuts, bool follow,
            LibraryDbContext db, DownloadQueue queue, LibraryStorage storage, LiveMarkerEntryService liveEntries, DownloadSettings settings,
            (string Action, string Details)? audit, CancellationToken cancellationToken)
        {
            var id = entry.Id;
            var cutsJson = CutOuts.Write(cuts);
            var rangeEnd = endTime?.TotalSeconds ?? entry.VodLengthSeconds;

            // Saving a cut by hand (even the same one) means "keep this cut"; undoing that makes a live markers entry follow again
            if (startTime == entry.StartTime && endTime == entry.EndTime && cutsJson == entry.Cuts)
            {
                if (entry.FollowsMarkers != follow)
                {
                    entry.FollowsMarkers = follow;
                    await db.SaveChangesAsync(cancellationToken);
                    if (follow)
                        await FollowMarkersAgainAsync(entry, db, liveEntries, cancellationToken);
                }
                return null;
            }

            var oldRange = $"{TrimTime.Format(entry.StartTime)}-{TrimTime.Format(entry.EndTime) ?? "end"}";
            var oldCuts = CutOuts.Read(entry.Cuts).Count;
            var clipLength = TimeSpan.FromSeconds(CutOuts.KeptSeconds(startTime.TotalSeconds, rangeEnd, cuts));
            // A live markers entry is always a reference while it follows (it would be re-downloaded on every marker change)
            var isStored = !follow && IsStoredLength(rangeEnd - startTime.TotalSeconds, settings, await TeamStoresClipsAsync(db, entry.TeamId, cancellationToken));
            var wasStored = entry.Status != EntryStatus.ReferenceOnly;
            var sameRange = startTime == entry.StartTime && endTime == entry.EndTime;
            var hasOldFile = entry.FileCuts is not null && (entry.FilePath is not null || entry.GeneratedFilePath is not null);
            var keepFile = sameRange && isStored == wasStored && !hasOldFile && entry.Status != EntryStatus.Failed;

            var details = $"{oldRange} -> {TrimTime.Format(startTime)}-{TrimTime.Format(endTime) ?? "end"}, {clipLength.TotalSeconds:0.###}s, {(isStored ? "stored" : "reference-only")}"
                + (oldCuts != cuts.Count ? $", cut-out parts {oldCuts} -> {cuts.Count}" : cuts.Count > 0 ? $", {cuts.Count} cut-out part(s)" : "")
                + (keepFile ? ", same file" : "")
                + (follow ? ", follows markers again" : "");

            var wasFollowing = entry.FollowsMarkers;
            if (keepFile)
            {
                // Only the cut-out parts changed: the file already holds the whole range, so nothing is downloaded again
                entry.Cuts = cutsJson;
                entry.ClipLengthSeconds = clipLength.TotalSeconds;
                entry.FollowsMarkers = follow;
                entry.UpdatedAtUtc = DateTime.UtcNow;
                db.Audit(audit?.Action ?? AuditActions.EntryRetrimmed, id, audit is { } a ? $"{a.Details}; {details}" : details);
                await db.SaveChangesAsync(cancellationToken);
                if (follow && !wasFollowing)
                    await FollowMarkersAgainAsync(entry, db, liveEntries, cancellationToken);
                return null;
            }

            // A running job would keep writing the old cut to the same file. Stop it and wait until it has cleaned up.
            if (queue.IsRunning(id))
            {
                queue.Cancel(id);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (queue.IsRunning(id))
                {
                    if (waited.Elapsed > RetrimCancelWait)
                        return Results.Problem(detail: "The current download didn't stop in time. Try again in a moment.", statusCode: StatusCodes.Status409Conflict);
                    await Task.Delay(100, cancellationToken);
                }

                // The job may have saved a status while stopping
                await db.Entry(entry).ReloadAsync(cancellationToken);
            }

            storage.TryDelete(entry.FilePath);
            storage.TryDelete(entry.GeneratedFilePath);

            entry.StartTime = startTime;
            entry.EndTime = endTime;
            entry.Cuts = cutsJson;
            entry.ClipLengthSeconds = clipLength.TotalSeconds;
            entry.FollowsMarkers = follow; // a cut picked by hand stays put
            entry.Status = isStored ? EntryStatus.Queued : EntryStatus.ReferenceOnly;
            entry.Error = null;
            entry.FilePath = null;
            entry.FileSizeBytes = null;
            entry.FileCuts = null;
            entry.GenerationStatus = GenerationStatus.None;
            entry.GeneratedFilePath = null;
            entry.GeneratedFileSizeBytes = null;
            entry.GeneratedExpiresAtUtc = null;
            entry.AudioRestoredSeconds = null;
            entry.AudioMutedSeconds = null;
            entry.UpdatedAtUtc = DateTime.UtcNow;
            db.Audit(audit?.Action ?? AuditActions.EntryRetrimmed, id, audit is { } b ? $"{b.Details}; {details}" : details);
            await db.SaveChangesAsync(cancellationToken);

            if (isStored)
                queue.Enqueue(id, DownloadJobKind.Store);

            if (follow)
                await FollowMarkersAgainAsync(entry, db, liveEntries, cancellationToken);
            return null;
        }

        /// <summary>Moves a live markers entry that follows its markers again to where the markers put it now.</summary>
        private static async Task<LibraryEntry> FollowMarkersAgainAsync(LibraryEntry entry, LibraryDbContext db, LiveMarkerEntryService liveEntries,
            CancellationToken cancellationToken)
        {
            await liveEntries.MarkersChangedAsync(db, entry.VodId, cancellationToken);
            await db.Entry(entry).ReloadAsync(cancellationToken);
            return entry;
        }

        private static async Task<int?> LatestVodLengthAsync(long vodId, IVodDetailsSource vods, ILoggerFactory loggerFactory)
        {
            try
            {
                return (await vods.GetVodAsync(vodId))?.LengthSeconds;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                loggerFactory.CreateLogger("TwitchDownloaderWeb.Entries").LogWarning("Couldn't look up VOD {VodId}: {Message}", vodId, ex.Message);
                return null;
            }
        }

        private static async Task<IResult> DeleteEntry(int id, TeamContext team, LibraryDbContext db, DownloadQueue queue, LibraryStorage storage, ShortformService shortform,
            CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            if (!team.CanOnOwnOrAny(entry.AddedByUserId, TeamPermission.DeleteOwnEntries, TeamPermission.DeleteAnyEntries))
                return NotYours("delete");

            queue.Cancel(id);

            db.Entries.Remove(entry);
            db.Audit(AuditActions.EntryDeleted, id, $"\"{entry.Title}\" (VOD {entry.VodId}, {entry.Status})");
            await db.SaveChangesAsync(cancellationToken);

            // Files that can't be deleted right now (e.g. open in another request) are removed by the cleanup sweep
            storage.TryDelete(entry.FilePath);
            storage.TryDelete(entry.GeneratedFilePath);
            storage.TryDelete(LibraryStorage.StoredRelativePath(id));
            storage.TryDelete(LibraryStorage.GeneratedRelativePath(id));
            shortform.Delete(id);

            return Results.NoContent();
        }

        /// <summary>
        /// Reference-only entries: queues an on-demand file (202) or returns the entry if one is ready (200).
        /// Failed stored entries: re-queues the download. Use GET /file once the entry's canDownload is true.
        /// </summary>
        private static async Task<IResult> GenerateEntry(int id, LibraryDbContext db, DownloadQueue queue, LibraryStorage storage, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            switch (entry.Status)
            {
                case EntryStatus.VodUnavailable:
                    return Results.Problem(detail: entry.Error ?? ReferenceVodCheckService.VodGoneMessage, statusCode: StatusCodes.Status410Gone);

                case EntryStatus.Ready:
                    return Results.Ok(EntryDto.From(entry));

                case EntryStatus.Queued or EntryStatus.Downloading:
                    return Results.Accepted($"/api/entries/{id}", EntryDto.From(entry));

                case EntryStatus.Failed:
                    entry.Status = EntryStatus.Queued;
                    entry.Error = null;
                    entry.UpdatedAtUtc = DateTime.UtcNow;
                    db.Audit(AuditActions.GenerationRequested, id, "Retry of failed stored download");
                    await db.SaveChangesAsync(cancellationToken);
                    queue.Enqueue(id, DownloadJobKind.Store);
                    return Results.Accepted($"/api/entries/{id}", EntryDto.From(entry));
            }

            // Reference-only
            switch (entry.GenerationStatus)
            {
                case GenerationStatus.Queued or GenerationStatus.Generating:
                    return Results.Accepted($"/api/entries/{id}", EntryDto.From(entry));

                case GenerationStatus.Ready when EntryDto.HasUnexpiredGeneratedFile(entry) && File.Exists(storage.Resolve(entry.GeneratedFilePath!)):
                    return Results.Ok(EntryDto.From(entry));
            }

            // None, Failed, or a Ready file that expired or went missing
            storage.TryDelete(entry.GeneratedFilePath);
            entry.GenerationStatus = GenerationStatus.Queued;
            entry.GeneratedFilePath = null;
            entry.GeneratedFileSizeBytes = null;
            entry.GeneratedExpiresAtUtc = null;
            entry.Error = null;
            entry.UpdatedAtUtc = DateTime.UtcNow;
            db.Audit(AuditActions.GenerationRequested, id);
            await db.SaveChangesAsync(cancellationToken);
            queue.Enqueue(id, DownloadJobKind.Generate);

            return Results.Accepted($"/api/entries/{id}", EntryDto.From(entry));
        }

        /// <param name="preview">True when the page plays the file (e.g. the vertical editor); not audited as a download.</param>
        /// <summary>
        /// The card's frame (<see cref="EntryDto.FrameUrl"/>). The URL carries the frame's time, so it can be cached; a re-trim gives a new URL.
        /// 404 when it can't be made (the page then shows the VOD's thumbnail).
        /// </summary>
        private static async Task<IResult> GetThumbnail(int id, LibraryDbContext db, CardThumbnailService thumbnails, HttpContext context,
            CancellationToken cancellationToken)
        {
            if (await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } entry)
                return Results.NotFound();

            return ThumbnailResult(await thumbnails.GetForEntryAsync(entry, cancellationToken), context);
        }

        internal static IResult ThumbnailResult(string? path, HttpContext context)
        {
            if (path is null)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(path, "image/jpeg");
        }

        private static async Task<IResult> DownloadEntryFile(int id, bool? preview, HttpRequest request, LibraryDbContext db, LibraryStorage storage,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            string? relativePath;
            string kind;
            if (entry.Status == EntryStatus.Ready)
            {
                relativePath = entry.FilePath;
                kind = "stored";
            }
            else if (EntryDto.HasUnexpiredGeneratedFile(entry))
            {
                relativePath = entry.GeneratedFilePath;
                kind = "on-demand";
            }
            else
            {
                var hint = entry.Status == EntryStatus.ReferenceOnly ? $" Request it with POST /api/entries/{id}/generate first." : "";
                return Results.Problem(detail: $"The file for entry {id} is not ready (status {entry.Status}, generation {entry.GenerationStatus}).{hint}",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var fullPath = storage.Resolve(relativePath!);
            if (!File.Exists(fullPath))
            {
                loggerFactory.CreateLogger("TwitchDownloaderWeb.Entries").LogError("File for entry {EntryId} is missing: {Path}", id, fullPath);

                // Put the entry in a state the user can recover from with /generate
                if (kind == "stored")
                {
                    entry.Status = EntryStatus.Failed;
                    entry.FilePath = null;
                    entry.FileSizeBytes = null;
                }
                else
                {
                    entry.GenerationStatus = GenerationStatus.None;
                    entry.GeneratedFilePath = null;
                    entry.GeneratedFileSizeBytes = null;
                    entry.GeneratedExpiresAtUtc = null;
                }

                entry.Error = "The file was missing from storage.";
                entry.UpdatedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.DownloadFailed, id, $"{kind} file missing from storage");
                await db.SaveChangesAsync(cancellationToken);
                return Results.Problem(detail: entry.Error, statusCode: StatusCodes.Status410Gone);
            }

            // Browsers and players may fetch a file in several ranges; only record the request that starts at the beginning
            var range = request.Headers.Range.ToString();
            if (preview != true && (string.IsNullOrEmpty(range) || range.StartsWith("bytes=0-", StringComparison.Ordinal)))
            {
                db.Audit(AuditActions.FileServed, id, kind);
                await db.SaveChangesAsync(cancellationToken);
            }

            return Results.File(fullPath, "video/mp4", GetDownloadFileName(entry), enableRangeProcessing: true);
        }

        /// <summary>A lookup started alongside another whose answer isn't needed after all (it may throw for a missing VOD).</summary>
        private static void Forget(Task task) => task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        internal static string GetDownloadFileName(LibraryEntry entry)
        {
            // Windows silently drops trailing dots and spaces, which could leave "" or ".." as the name
            var name = FilenameService.ReplaceInvalidFilenameChars(entry.Title)?.Trim().TrimEnd('.');
            return (string.IsNullOrEmpty(name) ? entry.ClipSlug ?? entry.VodId.ToString() : name) + ".mp4";
        }

        /// <summary>The member only has the "own entries" permission and this entry was added by someone else.</summary>
        private static IResult NotYours(string action) =>
            Results.Problem(detail: $"Your role only lets you {action} entries you added.", statusCode: StatusCodes.Status403Forbidden);

        private static IResult EntryNotFound(int id) =>
            Results.Problem(detail: $"Entry {id} was not found.", statusCode: StatusCodes.Status404NotFound);

        private static IResult VodNotFound(long vodId) =>
            Results.Problem(detail: $"VOD {vodId} was not found. It may be deleted, expired or private.", statusCode: StatusCodes.Status404NotFound);

        private static IResult RestrictedVod() =>
            Results.Problem(detail: "This VOD is restricted (e.g. subscriber-only), which is not supported yet.", statusCode: StatusCodes.Status403Forbidden);
    }
}
