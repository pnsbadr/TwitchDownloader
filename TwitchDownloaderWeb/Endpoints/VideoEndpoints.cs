using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="MarkerIds">The segments in the order they play: the video owner's, on any of their video rows and VODs (synced rows
    /// included, badrfly 2026-11-20).</param>
    /// <param name="Cuts">Parts cut out of some of its segments in this video only (the segment itself keeps its range), by segment;
    /// segments not listed keep the ones they have, an empty list removes them.</param>
    public sealed record VideoRequest(string? Title, List<int>? MarkerIds, List<VideoCutsRequest>? Cuts = null);

    /// <param name="Cuts">VOD seconds, inside the segment (see <see cref="CutOuts"/>).</param>
    public sealed record VideoCutsRequest(int MarkerId, IReadOnlyList<CutRange>? Cuts);

    /// <param name="Cuts">The segment's own cut-out parts (it skips them everywhere).</param>
    /// <param name="VideoCuts">Parts cut out of it in this video only.</param>
    /// <param name="KeptSeconds">What the video plays of it: its range without either.</param>
    public sealed record VideoSegmentDto(int MarkerId, int Position, double StartSeconds, double EndSeconds, string Label, string Color,
        IReadOnlyList<CutRange> Cuts, IReadOnlyList<CutRange> VideoCuts, double KeptSeconds)
    {
        /// <summary>The segment's VOD (a video may link segments of several VODs since 2026-11-20).</summary>
        public long VodId { get; init; }

        /// <summary>The row it sits on.</summary>
        public int? RowId { get; init; }

        /// <summary>Deleted from its timeline but kept in its videos (<see cref="VodMarker.RemovedAtUtc"/>).</summary>
        public bool Removed { get; init; }
    }

    /// <param name="LengthSeconds">All its segments together, without their cut-out parts.</param>
    public sealed record VideoDto(int Id, long VodId, int? RowId, int UserId, string? CreatedBy, string Title, string? ChannelName, DateTime StreamDateUtc,
        string? ThumbnailUrl, double LengthSeconds, IReadOnlyList<VideoSegmentDto> Segments, int? CopiedFromVideoId, DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc, bool CanEdit, bool CanDelete)
    {
        /// <summary>A frame from the middle of the video for its card (see <see cref="CardThumbnailService"/>); null without segments.</summary>
        public string? FrameUrl { get; init; }

        /// <summary>The team's workflow status (<see cref="TeamStatus"/>); null = none.</summary>
        public int? TeamStatusId { get; init; }

        public IReadOnlyList<string> Tags { get; init; } = [];

        /// <summary>The members who made edits of it (Player 2; teammates' vertical ones only for members who may see them). Filled by the lists.</summary>
        public IReadOnlyList<string> EditedBy { get; init; } = [];
    }

    /// <summary>
    /// Videos: linked segments (see <see cref="Video"/>). The link tool on the page makes them from segments on one of the member's video rows.
    /// </summary>
    public static class VideoEndpoints
    {
        public static IEndpointRouteBuilder MapVideoEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/videos", List).RequireTeam(TeamPermission.ViewLibrary);
            app.MapGet("/api/videos/{id:int}", Get).RequireTeam(TeamPermission.ViewLibrary);
            app.MapGet("/api/videos/{id:int}/thumbnail", GetThumbnail).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPost("/api/vods/{vodId:long}/videos", Create).RequireTeam(TeamPermission.AddEntries).RequireChannelVod();
            // Own videos need AddEntries, anyone's EditAnyEntries / DeleteAnyEntries (checked in the handler)
            app.MapPut("/api/videos/{id:int}", Update).RequireTeam(TeamPermission.AddEntries | TeamPermission.EditAnyEntries);
            app.MapDelete("/api/videos/{id:int}", Delete).RequireTeam(TeamPermission.AddEntries | TeamPermission.DeleteAnyEntries);
            app.MapPost("/api/videos/{id:int}/copy", Copy).RequireTeam(TeamPermission.AddEntries);
            return app;
        }

        internal static bool CanEdit(TeamContext team, Video video) =>
            !team.IsSupport && team.CanOnOwnOrAny(video.UserId.ToString(), TeamPermission.AddEntries, TeamPermission.EditAnyEntries);

        private static bool CanDelete(TeamContext team, Video video) =>
            !team.IsSupport && team.CanOnOwnOrAny(video.UserId.ToString(), TeamPermission.AddEntries, TeamPermission.DeleteAnyEntries);

        internal static IQueryable<Video> WithSegments(LibraryDbContext db) =>
            db.Videos.Include(x => x.Segments.OrderBy(s => s.Position)).ThenInclude(x => x.Marker);

        internal static async Task<List<VideoDto>> ToDtosAsync(LibraryDbContext db, IReadOnlyList<Video> videos, TeamContext team, CancellationToken cancellationToken)
        {
            var names = await TimelineEndpoints.UserNamesAsync(db, videos.Select(x => x.UserId), cancellationToken);
            var tags = await ItemStatuses.VideoTagNamesAsync(db, videos.Select(x => x.Id), cancellationToken);
            var editors = await LibraryListing.EditorsAsync(db, team, videos.Select(x => x.Id).ToList(), videos: true, cancellationToken);
            return videos.Select(video =>
            {
                var segments = video.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position)
                    .Select(x =>
                    {
                        var (start, end) = (x.Marker!.Seconds, x.Marker.EndSeconds!.Value);
                        return new VideoSegmentDto(x.MarkerId, x.Position, start, end, x.Marker.Label, x.Marker.Color,
                            CutOuts.Normalize(CutOuts.Read(x.Marker.Cuts), start, end), CutOuts.Normalize(CutOuts.Read(x.Cuts), start, end),
                            Math.Round(KeptParts(x).Sum(p => p.End - p.Start), 2))
                        { VodId = x.Marker.VodId, RowId = x.Marker.RowId, Removed = x.Marker.RemovedAtUtc is not null };
                    })
                    .ToList();
                return new VideoDto(video.Id, video.VodId, video.RowId, video.UserId, names.GetValueOrDefault(video.UserId), video.Title, video.ChannelName,
                    video.StreamDateUtc, video.ThumbnailUrl, Math.Round(segments.Sum(x => x.KeptSeconds), 2), segments,
                    video.CopiedFromVideoId, video.CreatedAtUtc, video.UpdatedAtUtc, CanEdit(team, video), CanDelete(team, video))
                {
                    FrameUrl = CardThumbnailService.VideoUrl(video.Id, PlayedParts(video)),
                    TeamStatusId = video.TeamStatusId,
                    Tags = tags.GetValueOrDefault(video.Id) ?? [],
                    EditedBy = editors.GetValueOrDefault(video.Id) ?? [],
                };
            }).ToList();
        }

        /// <summary>Everything the video plays, in order (markers loaded).</summary>
        internal static IEnumerable<(double Start, double End)> PlayedParts(Video video) =>
            video.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position).SelectMany(KeptParts);

        /// <summary>What a video plays of one of its segments: the segment's range without its own cut-out parts or the video's.</summary>
        internal static IReadOnlyList<(double Start, double End)> KeptParts(VideoSegment link) =>
            link.Marker is { EndSeconds: { } end } marker
                ? CutOuts.Kept(marker.Seconds, end, CutOuts.Read(marker.Cuts).Concat(CutOuts.Read(link.Cuts)))
                : [];

        /// <summary>
        /// Applies <see cref="VideoRequest.Cuts"/> to the video's links (markers loaded). Each listed segment must be in the video, and something
        /// of it must be left.
        /// </summary>
        private static IResult? ApplyCuts(IReadOnlyList<VideoSegment> links, List<VideoCutsRequest>? cuts, List<string> changes)
        {
            if (cuts is null)
                return null;
            if (cuts.Select(x => x.MarkerId).Distinct().Count() != cuts.Count)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cuts"] = ["List each segment once."] });

            foreach (var request in cuts)
            {
                var link = links.FirstOrDefault(x => x.MarkerId == request.MarkerId);
                if (link?.Marker is not { EndSeconds: { } end } marker)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["cuts"] = ["Cut parts out of segments in this video."] });

                var combined = CutOuts.Read(marker.Cuts).Concat(request.Cuts ?? []).ToList();
                if ((CutOuts.Validate(request.Cuts, marker.Seconds, end) ?? CutOuts.Validate(combined, marker.Seconds, end)) is { } error)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["cuts"] = [error] });

                var json = CutOuts.Write(CutOuts.Normalize(request.Cuts, marker.Seconds, end));
                if (json != link.Cuts)
                {
                    link.Cuts = json;
                    changes.Add($"cut-out parts of segment {marker.Id}: {CutOuts.Read(json).Count}");
                }
            }

            return null;
        }

        /// <summary>The card's frame (<see cref="VideoDto.FrameUrl"/>); 404 when it can't be made.</summary>
        private static async Task<IResult> GetThumbnail(int id, LibraryDbContext db, CardThumbnailService thumbnails, HttpContext context,
            CancellationToken cancellationToken)
        {
            if (await WithSegments(db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } video)
                return Results.NotFound();

            return EntryEndpoints.ThumbnailResult(await thumbnails.GetForVideoAsync(MiddleVod(video), PlayedParts(video).ToList(), cancellationToken), context);
        }

        /// <summary>The VOD of the segment playing halfway through the video (where its card's frame comes from).</summary>
        internal static long MiddleVod(Video video)
        {
            var links = video.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position).ToList();
            var left = links.Sum(x => KeptParts(x).Sum(p => p.End - p.Start)) / 2;
            foreach (var link in links)
            {
                var length = KeptParts(link).Sum(p => p.End - p.Start);
                if (left <= length)
                    return link.Marker!.VodId;
                left -= length;
            }
            return links.LastOrDefault()?.Marker?.VodId ?? video.VodId;
        }

        /// <param name="vodIds">Only these VODs (comma separated); blank = all of the team's videos, newest first.</param>
        /// <param name="userId">Only this member's.</param>
        /// <param name="userId">Only videos this member made.</param>
        /// <param name="editedBy">Only videos this member made an edit of (Player 2).</param>
        /// <param name="channels">Only videos of these team channels (logins, comma separated; the library's channel bar).</param>
        /// <param name="search">Substring of the title or of a segment's label (LIKE, case-insensitive for ASCII).</param>
        /// <param name="paged">true: answer <c>{ items, nextCursor, total }</c>, newest first, <paramref name="cursor"/> = a page's nextCursor.</param>
        /// <param name="from">Only videos made at or after this instant (ISO 8601 with an offset).</param>
        /// <param name="to">Only videos made before this instant.</param>
        /// <param name="mark">"starred" or "recent": the caller's own stars / recently opened videos.</param>
        /// <param name="sort">"dateAdded" (made, default), "streamDate", "title", "length" or "madeBy" (the last three page by offset).</param>
        /// <param name="dateField">What from / to / days look at: "dateAdded" (made, default) or "streamDate".</param>
        /// <param name="days">A time zone (IANA ID): the page also says how many videos match on each day of it, ignoring from/to (the calendar).</param>
        private static async Task<IResult> List(string? vodIds, string? userId, string? editedBy, string? channels, int? limit, string? search, bool? paged,
            string? cursor, string? from, string? to, string? status, string? tag, string? mark, string? sort, string? order, string? dateField, string? days,
            TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (statusId, noStatus, tagName, filterProblem) = StatusEndpoints.ParseFilters(status, tag);
            if (filterProblem is not null)
                return filterProblem;
            var (marked, markProblem) = MarkEndpoints.Filter(db, mark, MarkKind.Video, team.UserId);
            if (markProblem is not null)
                return markProblem;
            var (makers, makersProblem) = LibraryListing.ParseMembers(userId, "userId");
            if (makersProblem is not null)
                return makersProblem;
            var (editors, editorsProblem) = LibraryListing.ParseMembers(editedBy, "editedBy");
            if (editorsProblem is not null)
                return editorsProblem;
            var (zone, zoneProblem) = LibraryListing.ParseZone(days);
            if (zoneProblem is not null)
                return zoneProblem;
            if (!EntryEndpoints.TryParseInstant(from, out var fromUtc))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["Use an ISO 8601 date-time with an offset, e.g. 2026-10-09T07:00:00Z."] });
            if (!EntryEndpoints.TryParseInstant(to, out var toUtc))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Use an ISO 8601 date-time with an offset, e.g. 2026-10-10T07:00:00Z."] });
            var sortKey = (sort ?? "").ToLowerInvariant() switch
            {
                "" or "dateadded" => "dateadded",
                "streamdate" => "streamdate",
                "title" => "title",
                "length" => "length",
                "madeby" => "madeby",
                "editedby" => "editedby",
                _ => null,
            };
            if (sortKey is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = ["Sort must be dateAdded, streamDate, title, length, madeBy or editedBy."] });
            bool useStreamDate;
            switch (dateField?.ToLowerInvariant())
            {
                case null or "" or "dateadded":
                    useStreamDate = false;
                    break;
                case "streamdate":
                    useStreamDate = true;
                    break;
                default:
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateField"] = ["Date field must be streamDate or dateAdded."] });
            }
            var byDate = sortKey is "dateadded" or "streamdate";
            var byStreamDate = sortKey == "streamdate";

            PageCursor? after = null;
            var offset = 0;
            if (!string.IsNullOrEmpty(cursor) && !(byDate ? PageCursor.TryParse(cursor, out after) : LibraryListing.TryParseOffset(cursor, out offset)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Use the nextCursor of the page before."] });

            var (ids, problem) = TimelineEndpoints.ParseVodIds(vodIds);
            if (problem is not null)
                return problem;

            var query = WithSegments(db).AsNoTracking();
            // A video counts for every VOD it plays from (one video may span several streams)
            if (ids.Count > 0)
                query = query.Where(x => ids.Contains(x.VodId) || x.Segments.Any(s => ids.Contains(s.Marker!.VodId)));
            if (marked is not null)
                query = query.Where(x => marked.Contains(x.Id));
            if (makers is not null)
                query = query.Where(x => makers.Contains(x.UserId));
            if (await ChannelEndpoints.ChannelNamesAsync(db, channels, cancellationToken) is { } channelNames)
                query = query.Where(x => x.ChannelName != null && channelNames.Contains(x.ChannelName.ToLower()));
            if (editors is not null)
            {
                // Who made which vertical edit is only told to members who may see teammates' (VerticalAccess); horizontal edits are everyone's
                var canSee = VerticalAccess.CanSeeTeam(team);
                var me = team.UserId;
                query = query.Where(x => db.Projects.Any(p => p.VideoId == x.Id && editors.Contains(p.UserId) && (canSee || !p.Vertical || p.UserId == me)));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = "%" + search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
                query = query.Where(x => EF.Functions.Like(x.Title, pattern, @"\")
                    || x.Segments.Any(s => EF.Functions.Like(s.Marker!.Label, pattern, @"\")));
            }
            // The team's statuses and tags: a status picked shows only it; otherwise hidden statuses are left out
            if (statusId is { } wanted)
                query = query.Where(x => x.TeamStatusId == wanted);
            else if (noStatus)
                query = query.Where(x => x.TeamStatusId == null);
            else
                query = query.Where(x => x.TeamStatus == null || !x.TeamStatus.HideFromLists);
            if (tagName is not null)
                query = query.Where(x => db.VideoTags.Any(t => t.VideoId == x.Id && t.Tag!.Name == tagName));

            // The calendar: every matching video's day, before the from/to bounds narrow it
            List<DayCount>? dayCounts = null;
            if (zone is not null && paged == true)
            {
                var instants = useStreamDate
                    ? await query.Select(x => x.StreamDateUtc).ToListAsync(cancellationToken)
                    : await query.Select(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
                dayCounts = LibraryListing.CountDays(instants, zone);
            }
            if (fromUtc is { } lower)
                query = useStreamDate ? query.Where(x => x.StreamDateUtc >= lower) : query.Where(x => x.CreatedAtUtc >= lower);
            if (toUtc is { } upper)
                query = useStreamDate ? query.Where(x => x.StreamDateUtc < upper) : query.Where(x => x.CreatedAtUtc < upper);

            var take = Math.Clamp(limit ?? 200, 1, 1000);
            var ascending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
            if (paged != true)
            {
                var all = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(take).ToListAsync(cancellationToken);
                return Results.Ok(await ToDtosAsync(db, all, team, cancellationToken));
            }

            var total = await query.CountAsync(cancellationToken);
            if (after is { } a)
            {
                var at = a.AtUtc;
                var id = a.Id;
                query = (byStreamDate, ascending) switch
                {
                    (true, true) => query.Where(x => x.StreamDateUtc > at || (x.StreamDateUtc == at && x.Id > id)),
                    (true, false) => query.Where(x => x.StreamDateUtc < at || (x.StreamDateUtc == at && x.Id < id)),
                    (false, true) => query.Where(x => x.CreatedAtUtc > at || (x.CreatedAtUtc == at && x.Id > id)),
                    (false, false) => query.Where(x => x.CreatedAtUtc < at || (x.CreatedAtUtc == at && x.Id < id)),
                };
            }
            var users = db.Users;
            // Edited by (the Details view): the first member who made an edit of it, as the column lists them; items with none come last
            var seeVerticals = VerticalAccess.CanSeeTeam(team);
            var caller = team.UserId;
            // Length: the segments' whole ranges (the card's length also leaves out cut-out parts, which SQL can't see)
            query = (sortKey, ascending) switch
            {
                ("streamdate", true) => query.OrderBy(x => x.StreamDateUtc).ThenBy(x => x.Id),
                ("streamdate", false) => query.OrderByDescending(x => x.StreamDateUtc).ThenByDescending(x => x.Id),
                ("title", true) => query.OrderBy(x => x.Title.ToLower()).ThenBy(x => x.Id),
                ("title", false) => query.OrderByDescending(x => x.Title.ToLower()).ThenByDescending(x => x.Id),
                ("length", true) => query.OrderBy(x => x.Segments.Sum(s => (s.Marker!.EndSeconds ?? s.Marker.Seconds) - s.Marker.Seconds)).ThenBy(x => x.Id),
                ("length", false) => query.OrderByDescending(x => x.Segments.Sum(s => (s.Marker!.EndSeconds ?? s.Marker.Seconds) - s.Marker.Seconds)).ThenByDescending(x => x.Id),
                ("madeby", true) => query.OrderBy(x => users.Where(u => u.Id == x.UserId).Select(u => u.Username.ToLower()).FirstOrDefault())
                    .ThenByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
                ("madeby", false) => query.OrderByDescending(x => users.Where(u => u.Id == x.UserId).Select(u => u.Username.ToLower()).FirstOrDefault())
                    .ThenByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
                ("editedby", true) => query.OrderBy(x => !db.Projects.Any(p => p.VideoId == x.Id && (seeVerticals || !p.Vertical || p.UserId == caller)))
                    .ThenBy(x => db.Projects.Where(p => p.VideoId == x.Id && (seeVerticals || !p.Vertical || p.UserId == caller)).OrderBy(p => p.CreatedAtUtc)
                    .Select(p => users.Where(u => u.Id == p.UserId).Select(u => u.Username.ToLower()).FirstOrDefault()).FirstOrDefault())
                    .ThenByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
                ("editedby", false) => query.OrderBy(x => !db.Projects.Any(p => p.VideoId == x.Id && (seeVerticals || !p.Vertical || p.UserId == caller)))
                    .ThenByDescending(x => db.Projects.Where(p => p.VideoId == x.Id && (seeVerticals || !p.Vertical || p.UserId == caller)).OrderBy(p => p.CreatedAtUtc)
                    .Select(p => users.Where(u => u.Id == p.UserId).Select(u => u.Username.ToLower()).FirstOrDefault()).FirstOrDefault())
                    .ThenByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
                (_, true) => query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
                _ => query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
            };
            if (offset > 0)
                query = query.Skip(offset);
            var videos = await query.Take(take + 1).ToListAsync(cancellationToken);
            string? next = null;
            if (videos.Count > take)
            {
                videos.RemoveAt(take);
                var last = videos[^1];
                next = byDate ? new PageCursor(byStreamDate ? last.StreamDateUtc : last.CreatedAtUtc, last.Id).ToString() : LibraryListing.OffsetCursor(offset + take);
            }
            return Results.Ok(new Page<VideoDto>(await ToDtosAsync(db, videos, team, cancellationToken), next, total, dayCounts));
        }

        private static async Task<IResult> Get(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var video = await WithSegments(db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return video is null ? NotFound() : Results.Ok((await ToDtosAsync(db, [video], team, cancellationToken))[0]);
        }

        /// <summary>
        /// Checks the title and segments. The segments must be <paramref name="ownerId"/>'s, on their rows, of VODs the team can open. A segment
        /// deleted from its timeline but kept for videos may stay only in a video that already has it (<paramref name="current"/>).
        /// </summary>
        private static async Task<(string? Title, List<VodMarker> Markers, IResult? Problem)> ValidateAsync(LibraryDbContext db, VideoRequest request,
            TeamContext team, TeamChannelAccess access, int ownerId, bool titleRequired, CancellationToken cancellationToken,
            IReadOnlyCollection<int>? current = null)
        {
            var title = request.Title?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                if (titleRequired || request.Title is not null)
                    return (null, [], Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Give the video a title."] }));
                title = null;
            }
            else if (title.Length > Video.MaxTitleLength)
                return (null, [], Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"Keep the title to {Video.MaxTitleLength} characters."] }));

            if (request.MarkerIds is null)
                return (title, [], null);

            var ids = request.MarkerIds;
            if (ids.Count == 0)
                return (null, [], Results.ValidationProblem(new Dictionary<string, string[]> { ["markerIds"] = ["Link at least one segment."] }));
            if (ids.Count > Video.MaxSegments)
                return (null, [], Results.ValidationProblem(new Dictionary<string, string[]> { ["markerIds"] = [$"A video can have at most {Video.MaxSegments} segments."] }));
            if (ids.Distinct().Count() != ids.Count)
                return (null, [], Results.ValidationProblem(new Dictionary<string, string[]> { ["markerIds"] = ["Each segment can be linked once."] }));

            var markers = await db.Markers.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
            string? error = null;
            if (markers.Count != ids.Count)
                error = "Some of those segments are gone.";
            else if (markers.Any(x => x.EndSeconds is null))
                error = "Only segments can be linked, not moments.";
            else if (markers.Any(x => x.CreatedByUserId != ownerId))
                error = "A video links segments of one member.";
            else if (markers.Any(x => x.RemovedAtUtc is not null && current?.Contains(x.Id) != true))
                error = "Some of those segments were deleted.";
            else if (markers.Any(x => x.RowId is null && x.RemovedAtUtc is null))
                error = "Link segments that are on a row.";
            if (error is not null)
                return (null, [], Results.ValidationProblem(new Dictionary<string, string[]> { ["markerIds"] = [error] }));
            // Segments of other streamers' VODs (synced rows): the team must still be able to open them
            foreach (var vod in markers.Select(x => x.VodId).Distinct())
                if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vod, cancellationToken), vod) is { } refused)
                    return (null, [], refused);

            return (title, ids.Select(id => markers.First(x => x.Id == id)).ToList(), null);
        }

        private static async Task<IResult> Create(long vodId, VideoRequest request, TeamContext team, LibraryDbContext db, IVodDetailsSource vods,
            TeamChannelAccess access, CancellationToken cancellationToken)
        {
            if (request.MarkerIds is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["markerIds"] = ["Link at least one segment."] });
            var (title, markers, problem) = await ValidateAsync(db, request, team, access, team.UserId, titleRequired: true, cancellationToken);
            if (problem is not null)
                return problem;

            VodDetails? vod;
            try
            {
                vod = await vods.GetVodAsync(vodId);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                vod = null; // the card just lacks the date and thumbnail until it's edited
            }

            var now = DateTime.UtcNow;
            var video = new Video
            {
                VodId = vodId, RowId = markers[0].RowId, UserId = team.UserId, Title = title!, ChannelName = vod?.ChannelName,
                StreamDateUtc = vod?.StreamDateUtc ?? default, ThumbnailUrl = vod?.ThumbnailUrl, CreatedAtUtc = now, UpdatedAtUtc = now,
                Segments = markers.Select((m, i) => new VideoSegment { MarkerId = m.Id, Marker = m, Position = i }).ToList(),
            };
            if (ApplyCuts(video.Segments, request.Cuts, []) is { } cutProblem)
                return cutProblem;
            db.Videos.Add(video);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.VideoAdded, null, $"VOD {vodId} video {video.Id} \"{video.Title}\", {markers.Count} segment{(markers.Count == 1 ? "" : "s")}");
            await db.SaveChangesAsync(cancellationToken);

            var saved = await WithSegments(db).AsNoTracking().FirstAsync(x => x.Id == video.Id, cancellationToken);
            return Results.Created($"/api/videos/{video.Id}", (await ToDtosAsync(db, [saved], team, cancellationToken))[0]);
        }

        /// <summary>Retitles it and/or sets its segments (the whole list, in order).</summary>
        private static async Task<IResult> Update(int id, VideoRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            CancellationToken cancellationToken)
        {
            var video = await WithSegments(db).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (video is null)
                return NotFound();
            if (!CanEdit(team, video))
                return Results.Problem(detail: "Your role only lets you change your own videos. Make a copy instead.", statusCode: StatusCodes.Status403Forbidden);

            var (title, markers, problem) = await ValidateAsync(db, request, team, access, video.UserId, titleRequired: false, cancellationToken,
                video.Segments.Select(x => x.MarkerId).ToList());
            if (problem is not null)
                return problem;

            // Changing the segment list saves the old links' removal before the new ones go in (unique index), so the whole change is
            // one transaction: a refused cut afterwards (400) used to leave the video with no segments at all
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var changes = new List<string>();
            if (title is not null && title != video.Title)
            {
                changes.Add($"\"{video.Title}\" -> \"{title}\"");
                video.Title = title;
            }

            if (request.MarkerIds is not null && !request.MarkerIds.SequenceEqual(video.Segments.OrderBy(x => x.Position).Select(x => x.MarkerId)))
            {
                // A segment that stays keeps the parts cut out of it in this video
                var keptCuts = video.Segments.ToDictionary(x => x.MarkerId, x => x.Cuts);
                db.VideoSegments.RemoveRange(video.Segments);
                await db.SaveChangesAsync(cancellationToken); // the unique (video, marker) index needs the old links gone first
                video.Segments = markers.Select((m, i) => new VideoSegment
                {
                    VideoId = video.Id, MarkerId = m.Id, Marker = m, Position = i, Cuts = keptCuts.GetValueOrDefault(m.Id),
                }).ToList();
                changes.Add($"{markers.Count} segment{(markers.Count == 1 ? "" : "s")}");
            }

            if (ApplyCuts(video.Segments, request.Cuts, changes) is { } cutProblem)
                return cutProblem;

            if (changes.Count > 0)
            {
                video.UpdatedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.VideoChanged, null, $"VOD {video.VodId} video {video.Id}: {string.Join(", ", changes)}");
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            await DeleteEmptyAsync(db, video.VodId, cancellationToken); // a kept segment taken out of its last video goes

            var saved = await WithSegments(db).AsNoTracking().FirstAsync(x => x.Id == video.Id, cancellationToken);
            return Results.Ok((await ToDtosAsync(db, [saved], team, cancellationToken))[0]);
        }

        /// <summary>Deletes the video only; its segments stay on the row.</summary>
        private static async Task<IResult> Delete(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var video = await db.Videos.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (video is null)
                return NotFound();
            if (!CanDelete(team, video))
                return Results.Problem(detail: "Your role only lets you delete your own videos.", statusCode: StatusCodes.Status403Forbidden);

            db.Videos.Remove(video);
            db.Audit(AuditActions.VideoDeleted, null, $"VOD {video.VodId} video {video.Id} \"{video.Title}\"");
            await db.SaveChangesAsync(cancellationToken);
            await DeleteEmptyAsync(db, video.VodId, cancellationToken); // segments kept only for it go with it
            return Results.NoContent();
        }

        /// <summary>A copy of someone's video for the caller: its segments are copied onto one of the caller's video rows and linked the same way.</summary>
        private static async Task<IResult> Copy(int id, CopyRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            CancellationToken cancellationToken)
        {
            var source = await WithSegments(db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (source is null)
                return NotFound();

            var (video, problem) = await CopyVideoAsync(source, request.RowId, team, db, access, cancellationToken);
            if (problem is not null)
                return problem;

            var saved = await WithSegments(db).AsNoTracking().FirstAsync(x => x.Id == video!.Id, cancellationToken);
            return Results.Created($"/api/videos/{saved.Id}", (await ToDtosAsync(db, [saved], team, cancellationToken))[0]);
        }

        /// <summary>
        /// Copies a video (segments loaded) for the caller and saves it. The copy's links are in the same order as the source's segments, each
        /// segment copy remembering its source (<see cref="VodMarker.CopiedFromMarkerId"/>).
        /// </summary>
        internal static async Task<(Video? Copy, IResult? Problem)> CopyVideoAsync(Video source, int? rowId, TeamContext team, LibraryDbContext db,
            TeamChannelAccess access, CancellationToken cancellationToken)
        {
            var sourceVods = source.Segments.Where(x => x.Marker is not null).Select(x => x.Marker!.VodId).Append(source.VodId).Distinct().ToList();
            foreach (var vod in sourceVods)
                if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vod, cancellationToken), vod) is { } refused)
                    return (null, refused);
            // The row asked for takes the segments of its VOD; the others go on the caller's first video row of their own VOD
            long? rowVod = null;
            if (rowId is { } row)
            {
                rowVod = await db.TimelineRows.AsNoTracking().Where(x => x.Id == row).Select(x => (long?)x.VodId).FirstOrDefaultAsync(cancellationToken);
                if (rowVod is null || await TimelineEndpoints.CheckRowAsync(db, row, rowVod.Value, TimelineRowKind.Video, team.UserId, cancellationToken) is { } rowProblem)
                    return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["rowId"] = ["Pick one of your video rows."] }));
            }

            var now = DateTime.UtcNow;
            var copies = source.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position).Select(x => new VodMarker
            {
                VodId = x.Marker!.VodId, Seconds = x.Marker.Seconds, EndSeconds = x.Marker.EndSeconds, Label = x.Marker.Label, Color = x.Marker.Color,
                Cuts = x.Marker.Cuts, RowId = x.Marker.RemovedAtUtc is null && x.Marker.VodId == rowVod ? rowId : null, CopiedFromMarkerId = x.MarkerId,
                CreatedAtUtc = now,
                // A segment kept only for the video is copied the same way: in the copy, not on the caller's row
                RemovedAtUtc = x.Marker.RemovedAtUtc is null ? null : now,
            }).ToList();
            var linkCuts = source.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position).Select(x => x.Cuts).ToList();
            if (copies.Count == 0)
                return (null, Results.Problem(detail: "This video has no segments left.", statusCode: StatusCodes.Status409Conflict));

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.Markers.AddRange(copies);
            await db.SaveChangesAsync(cancellationToken); // fills the row (the caller's first video row when none was given)

            var video = new Video
            {
                VodId = source.VodId, RowId = copies[0].RowId, UserId = team.UserId, Title = source.Title, ChannelName = source.ChannelName,
                StreamDateUtc = source.StreamDateUtc, ThumbnailUrl = source.ThumbnailUrl, CopiedFromVideoId = source.Id, CreatedAtUtc = now, UpdatedAtUtc = now,
                Segments = copies.Select((m, i) => new VideoSegment { MarkerId = m.Id, Position = i, Cuts = linkCuts[i] }).ToList(),
            };
            db.Videos.Add(video);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.VideoAdded, null, $"VOD {video.VodId} video {video.Id} \"{video.Title}\" (copy of video {source.Id}, {copies.Count} segments copied)");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (video, null);
        }

        /// <summary>
        /// Deletes the team's videos that have no segments left (after segments were deleted; their links went with the segments, cascade: a
        /// video can't exist empty), then segments that were only kept for videos (<see cref="VodMarker.RemovedAtUtc"/>) and no video links now.
        /// </summary>
        internal static async Task DeleteEmptyAsync(LibraryDbContext db, long vodId, CancellationToken cancellationToken)
        {
            // Any VOD of the team's: a video may link segments of other VODs
            var empty = await db.Videos.Where(x => !db.VideoSegments.Any(s => s.VideoId == x.Id)).ToListAsync(cancellationToken);
            if (empty.Count > 0)
            {
                foreach (var video in empty)
                    db.Audit(AuditActions.VideoDeleted, null, $"VOD {video.VodId} video {video.Id} \"{video.Title}\" (no segments left)");
                db.Videos.RemoveRange(empty);
                await db.SaveChangesAsync(cancellationToken);
            }

            var orphans = await db.Markers.Where(x => x.RemovedAtUtc != null && !db.VideoSegments.Any(s => s.MarkerId == x.Id)).ToListAsync(cancellationToken);
            if (orphans.Count > 0)
            {
                db.Markers.RemoveRange(orphans);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        /// <summary>The videos a segment is linked in, for refusing changes that would break them.</summary>
        internal static Task<List<string>> TitlesLinkingAsync(LibraryDbContext db, int markerId, CancellationToken cancellationToken) =>
            db.VideoSegments.Where(x => x.MarkerId == markerId).Join(db.Videos, s => s.VideoId, v => v.Id, (s, v) => v.Title).ToListAsync(cancellationToken);

        private static IResult NotFound() => Results.Problem(detail: "That video is gone.", statusCode: StatusCodes.Status404NotFound);
    }
}
