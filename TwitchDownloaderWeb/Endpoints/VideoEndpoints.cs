using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="MarkerIds">The segments in the order they play. All on one video row of the video's owner, on the video's VOD.</param>
    public sealed record VideoRequest(string? Title, List<int>? MarkerIds);

    public sealed record VideoSegmentDto(int MarkerId, int Position, double StartSeconds, double EndSeconds, string Label, string Color);

    /// <param name="LengthSeconds">All its segments together.</param>
    public sealed record VideoDto(int Id, long VodId, int RowId, int UserId, string? CreatedBy, string Title, string? ChannelName, DateTime StreamDateUtc,
        string? ThumbnailUrl, double LengthSeconds, IReadOnlyList<VideoSegmentDto> Segments, int? CopiedFromVideoId, DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc, bool CanEdit, bool CanDelete);

    /// <summary>
    /// Videos: linked segments (see <see cref="Video"/>). The link tool on the page makes them from segments on one of the member's video rows.
    /// </summary>
    public static class VideoEndpoints
    {
        public static IEndpointRouteBuilder MapVideoEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/videos", List).RequireTeam(TeamPermission.ViewLibrary);
            app.MapGet("/api/videos/{id:int}", Get).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPost("/api/vods/{vodId:long}/videos", Create).RequireTeam(TeamPermission.AddEntries).RequireChannelVod();
            // Own videos need AddEntries, anyone's EditAnyEntries / DeleteAnyEntries (checked in the handler)
            app.MapPut("/api/videos/{id:int}", Update).RequireTeam(TeamPermission.AddEntries | TeamPermission.EditAnyEntries);
            app.MapDelete("/api/videos/{id:int}", Delete).RequireTeam(TeamPermission.AddEntries | TeamPermission.DeleteAnyEntries);
            app.MapPost("/api/videos/{id:int}/copy", Copy).RequireTeam(TeamPermission.AddEntries);
            return app;
        }

        private static bool CanEdit(TeamContext team, Video video) =>
            !team.IsSupport && team.CanOnOwnOrAny(video.UserId.ToString(), TeamPermission.AddEntries, TeamPermission.EditAnyEntries);

        private static bool CanDelete(TeamContext team, Video video) =>
            !team.IsSupport && team.CanOnOwnOrAny(video.UserId.ToString(), TeamPermission.AddEntries, TeamPermission.DeleteAnyEntries);

        private static IQueryable<Video> WithSegments(LibraryDbContext db) =>
            db.Videos.Include(x => x.Segments.OrderBy(s => s.Position)).ThenInclude(x => x.Marker);

        internal static async Task<List<VideoDto>> ToDtosAsync(LibraryDbContext db, IReadOnlyList<Video> videos, TeamContext team, CancellationToken cancellationToken)
        {
            var names = await TimelineEndpoints.UserNamesAsync(db, videos.Select(x => x.UserId), cancellationToken);
            return videos.Select(video =>
            {
                var segments = video.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position)
                    .Select(x => new VideoSegmentDto(x.MarkerId, x.Position, x.Marker!.Seconds, x.Marker.EndSeconds!.Value, x.Marker.Label, x.Marker.Color))
                    .ToList();
                return new VideoDto(video.Id, video.VodId, video.RowId, video.UserId, names.GetValueOrDefault(video.UserId), video.Title, video.ChannelName,
                    video.StreamDateUtc, video.ThumbnailUrl, Math.Round(segments.Sum(x => x.EndSeconds - x.StartSeconds), 2), segments,
                    video.CopiedFromVideoId, video.CreatedAtUtc, video.UpdatedAtUtc, CanEdit(team, video), CanDelete(team, video));
            }).ToList();
        }

        /// <param name="vodIds">Only these VODs (comma separated); blank = all of the team's videos, newest first.</param>
        /// <param name="userId">Only this member's.</param>
        private static async Task<IResult> List(string? vodIds, int? userId, int? limit, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (ids, problem) = TimelineEndpoints.ParseVodIds(vodIds);
            if (problem is not null)
                return problem;

            var query = WithSegments(db).AsNoTracking();
            if (ids.Count > 0)
                query = query.Where(x => ids.Contains(x.VodId));
            if (userId is { } user)
                query = query.Where(x => x.UserId == user);

            var videos = await query.OrderByDescending(x => x.CreatedAtUtc).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync(cancellationToken);
            return Results.Ok(await ToDtosAsync(db, videos, team, cancellationToken));
        }

        private static async Task<IResult> Get(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var video = await WithSegments(db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return video is null ? NotFound() : Results.Ok((await ToDtosAsync(db, [video], team, cancellationToken))[0]);
        }

        /// <summary>Checks the title and segments. The segments must be <paramref name="ownerId"/>'s, on one video row of the VOD.</summary>
        private static async Task<(string? Title, List<VodMarker> Markers, IResult? Problem)> ValidateAsync(LibraryDbContext db, VideoRequest request,
            long vodId, int ownerId, int? requiredRowId, bool titleRequired, CancellationToken cancellationToken)
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
            if (markers.Count != ids.Count || markers.Any(x => x.VodId != vodId))
                error = "Pick segments on this VOD.";
            else if (markers.Any(x => x.EndSeconds is null))
                error = "Only segments can be linked, not moments.";
            else if (markers.Any(x => x.CreatedByUserId != ownerId))
                error = "A video links segments of one member.";
            else if (markers.Select(x => x.RowId).Distinct().Count() != 1 || markers[0].RowId is null
                     || (requiredRowId is { } row && markers[0].RowId != row))
                error = "A video links segments from one row.";
            if (error is not null)
                return (null, [], Results.ValidationProblem(new Dictionary<string, string[]> { ["markerIds"] = [error] }));

            return (title, ids.Select(id => markers.First(x => x.Id == id)).ToList(), null);
        }

        private static async Task<IResult> Create(long vodId, VideoRequest request, TeamContext team, LibraryDbContext db, IVodDetailsSource vods,
            CancellationToken cancellationToken)
        {
            if (request.MarkerIds is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["markerIds"] = ["Link at least one segment."] });
            var (title, markers, problem) = await ValidateAsync(db, request, vodId, team.UserId, null, titleRequired: true, cancellationToken);
            if (problem is not null)
                return problem;

            VodDetails? vod;
            try
            {
                vod = await vods.GetVodAsync(vodId);
            }
            catch (HttpRequestException)
            {
                vod = null; // the card just lacks the date and thumbnail until it's edited
            }

            var now = DateTime.UtcNow;
            var video = new Video
            {
                VodId = vodId, RowId = markers[0].RowId!.Value, UserId = team.UserId, Title = title!, ChannelName = vod?.ChannelName,
                StreamDateUtc = vod?.StreamDateUtc ?? default, ThumbnailUrl = vod?.ThumbnailUrl, CreatedAtUtc = now, UpdatedAtUtc = now,
                Segments = markers.Select((m, i) => new VideoSegment { MarkerId = m.Id, Position = i }).ToList(),
            };
            db.Videos.Add(video);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.VideoAdded, null, $"VOD {vodId} video {video.Id} \"{video.Title}\", {markers.Count} segment{(markers.Count == 1 ? "" : "s")}");
            await db.SaveChangesAsync(cancellationToken);

            var saved = await WithSegments(db).AsNoTracking().FirstAsync(x => x.Id == video.Id, cancellationToken);
            return Results.Created($"/api/videos/{video.Id}", (await ToDtosAsync(db, [saved], team, cancellationToken))[0]);
        }

        /// <summary>Retitles it and/or sets its segments (the whole list, in order).</summary>
        private static async Task<IResult> Update(int id, VideoRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var video = await WithSegments(db).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (video is null)
                return NotFound();
            if (!CanEdit(team, video))
                return Results.Problem(detail: "Your role only lets you change your own videos. Make a copy instead.", statusCode: StatusCodes.Status403Forbidden);

            var (title, markers, problem) = await ValidateAsync(db, request, video.VodId, video.UserId, video.RowId, titleRequired: false, cancellationToken);
            if (problem is not null)
                return problem;

            var changes = new List<string>();
            if (title is not null && title != video.Title)
            {
                changes.Add($"\"{video.Title}\" -> \"{title}\"");
                video.Title = title;
            }

            if (request.MarkerIds is not null && !request.MarkerIds.SequenceEqual(video.Segments.OrderBy(x => x.Position).Select(x => x.MarkerId)))
            {
                db.VideoSegments.RemoveRange(video.Segments);
                await db.SaveChangesAsync(cancellationToken); // the unique (video, marker) index needs the old links gone first
                video.Segments = markers.Select((m, i) => new VideoSegment { VideoId = video.Id, MarkerId = m.Id, Position = i }).ToList();
                changes.Add($"{markers.Count} segment{(markers.Count == 1 ? "" : "s")}");
            }

            if (changes.Count > 0)
            {
                video.UpdatedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.VideoChanged, null, $"VOD {video.VodId} video {video.Id}: {string.Join(", ", changes)}");
                await db.SaveChangesAsync(cancellationToken);
            }

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
            return Results.NoContent();
        }

        /// <summary>A copy of someone's video for the caller: its segments are copied onto one of the caller's video rows and linked the same way.</summary>
        private static async Task<IResult> Copy(int id, CopyRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            CancellationToken cancellationToken)
        {
            var source = await WithSegments(db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (source is null)
                return NotFound();
            if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, source.VodId, cancellationToken), source.VodId) is { } refused)
                return refused;
            if (request.RowId is { } rowId
                && await TimelineEndpoints.CheckRowAsync(db, rowId, source.VodId, TimelineRowKind.Video, team.UserId, cancellationToken) is { } rowProblem)
                return rowProblem;

            var now = DateTime.UtcNow;
            var copies = source.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position).Select(x => new VodMarker
            {
                VodId = source.VodId, Seconds = x.Marker!.Seconds, EndSeconds = x.Marker.EndSeconds, Label = x.Marker.Label, Color = x.Marker.Color,
                RowId = request.RowId, CopiedFromMarkerId = x.MarkerId, CreatedAtUtc = now,
            }).ToList();
            if (copies.Count == 0)
                return Results.Problem(detail: "This video has no segments left.", statusCode: StatusCodes.Status409Conflict);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.Markers.AddRange(copies);
            await db.SaveChangesAsync(cancellationToken); // fills the row (the caller's first video row when none was given)

            var video = new Video
            {
                VodId = source.VodId, RowId = copies[0].RowId!.Value, UserId = team.UserId, Title = source.Title, ChannelName = source.ChannelName,
                StreamDateUtc = source.StreamDateUtc, ThumbnailUrl = source.ThumbnailUrl, CopiedFromVideoId = source.Id, CreatedAtUtc = now, UpdatedAtUtc = now,
                Segments = copies.Select((m, i) => new VideoSegment { MarkerId = m.Id, Position = i }).ToList(),
            };
            db.Videos.Add(video);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.VideoAdded, null, $"VOD {video.VodId} video {video.Id} \"{video.Title}\" (copy of video {source.Id}, {copies.Count} segments copied)");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var saved = await WithSegments(db).AsNoTracking().FirstAsync(x => x.Id == video.Id, cancellationToken);
            return Results.Created($"/api/videos/{video.Id}", (await ToDtosAsync(db, [saved], team, cancellationToken))[0]);
        }

        /// <summary>
        /// Deletes the team's videos on the VOD that have no segments left (after segments were deleted). Their links went with the segments
        /// (cascade); a video can't exist empty.
        /// </summary>
        internal static async Task DeleteEmptyAsync(LibraryDbContext db, long vodId, CancellationToken cancellationToken)
        {
            var empty = await db.Videos.Where(x => x.VodId == vodId && !db.VideoSegments.Any(s => s.VideoId == x.Id)).ToListAsync(cancellationToken);
            if (empty.Count == 0)
                return;
            foreach (var video in empty)
                db.Audit(AuditActions.VideoDeleted, null, $"VOD {video.VodId} video {video.Id} \"{video.Title}\" (no segments left)");
            db.Videos.RemoveRange(empty);
            await db.SaveChangesAsync(cancellationToken);
        }

        /// <summary>The videos a segment is linked in, for refusing changes that would break them.</summary>
        internal static Task<List<string>> TitlesLinkingAsync(LibraryDbContext db, int markerId, CancellationToken cancellationToken) =>
            db.VideoSegments.Where(x => x.MarkerId == markerId).Join(db.Videos, s => s.VideoId, v => v.Id, (s, v) => v.Title).ToListAsync(cancellationToken);

        private static IResult NotFound() => Results.Problem(detail: "That video is gone.", statusCode: StatusCodes.Status404NotFound);
    }
}
