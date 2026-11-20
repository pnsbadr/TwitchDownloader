using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="ChannelName">From the team's clips/videos on the VOD (null when it only has segments/moments).</param>
    /// <param name="LastWorkUtc">When the newest clip, segment, moment, video or edit on it was made or changed.</param>
    public sealed record VodWorkDto(long VodId, int Clips, int Videos, int Segments, int Moments, int Edits, string? ChannelName,
        DateTime? StreamDateUtc, string? ThumbnailUrl, DateTime LastWorkUtc);

    /// <summary>
    /// The team's work per VOD (badrfly 2026-11-17: "the VOD is the project"): a VOD counts once something is on its timeline. Empty
    /// rows don't count. Hidden VODs are left out by the query filters.
    /// </summary>
    public static class VodWorkEndpoints
    {
        public const int MaxVods = 200;

        public static IEndpointRouteBuilder MapVodWorkEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/work", List).RequireTeam(TeamPermission.ViewLibrary);
            return app;
        }

        /// <param name="vodIds">These VODs (comma separated, up to 200; VODs with no work are left out); none = the VODs with work, newest work first.</param>
        private static async Task<IResult> List(string? vodIds, int? limit, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (ids, problem) = TimelineEndpoints.ParseVodIds(vodIds);
            if (problem is not null)
                return problem;

            var entries = db.Entries.AsNoTracking().Where(x => x.VodId > 0);
            var markers = db.Markers.AsNoTracking().Where(x => x.RemovedAtUtc == null); // segments kept only for videos aren't on a timeline
            var videos = db.Videos.AsNoTracking();
            var projects = db.Projects.AsNoTracking().Where(x => x.VodId > 0);
            // Edits: teammates' vertical ones only for members who may see them (VerticalAccess); horizontal ones are everyone's
            if (!VerticalAccess.CanSeeTeam(team))
            {
                var own = team.UserId;
                projects = projects.Where(x => x.UserId == own || !x.Vertical);
            }
            if (ids.Count > 0)
            {
                entries = entries.Where(x => ids.Contains(x.VodId));
                markers = markers.Where(x => ids.Contains(x.VodId));
                videos = videos.Where(x => ids.Contains(x.VodId) || x.Segments.Any(s => ids.Contains(s.Marker!.VodId)));
                projects = projects.Where(x => ids.Contains(x.VodId));
            }

            var clipRows = await entries.GroupBy(x => x.VodId)
                .Select(g => new { VodId = g.Key, Count = g.Count(), Last = g.Max(x => x.UpdatedAtUtc) }).ToListAsync(cancellationToken);
            var clipInfo = await entries.OrderByDescending(x => x.Id)
                .Select(x => new { x.VodId, x.ChannelName, x.StreamDateUtc, x.ThumbnailUrl }).ToListAsync(cancellationToken);
            var markerRows = await markers.GroupBy(x => x.VodId)
                .Select(g => new { VodId = g.Key, Segments = g.Count(x => x.EndSeconds != null), Moments = g.Count(x => x.EndSeconds == null), Last = g.Max(x => x.CreatedAtUtc) })
                .ToListAsync(cancellationToken);
            // A video counts on every VOD it plays from (one video may span several streams)
            var videoVods = await videos.Select(x => new { x.VodId, x.UpdatedAtUtc, SegmentVods = x.Segments.Select(s => s.Marker!.VodId).ToList() })
                .ToListAsync(cancellationToken);
            var videoRows = videoVods
                .SelectMany(x => x.SegmentVods.Append(x.VodId).Distinct().Select(vod => new { VodId = vod, x.UpdatedAtUtc }))
                .Where(x => ids.Count == 0 || ids.Contains(x.VodId))
                .GroupBy(x => x.VodId)
                .Select(g => new { VodId = g.Key, Count = g.Count(), Last = g.Max(x => x.UpdatedAtUtc) }).ToList();
            var videoInfo = await videos.OrderByDescending(x => x.Id)
                .Select(x => new { x.VodId, x.ChannelName, x.StreamDateUtc, x.ThumbnailUrl }).ToListAsync(cancellationToken);
            var projectRows = await projects.GroupBy(x => x.VodId)
                .Select(g => new { VodId = g.Key, Count = g.Count(), Last = g.Max(x => x.UpdatedAtUtc) }).ToListAsync(cancellationToken);

            var all = clipRows.Select(x => x.VodId).Concat(markerRows.Select(x => x.VodId)).Concat(videoRows.Select(x => x.VodId))
                .Concat(projectRows.Select(x => x.VodId)).Distinct();
            var result = all.Select(vodId =>
            {
                var clips = clipRows.FirstOrDefault(x => x.VodId == vodId);
                var marks = markerRows.FirstOrDefault(x => x.VodId == vodId);
                var vids = videoRows.FirstOrDefault(x => x.VodId == vodId);
                var edits = projectRows.FirstOrDefault(x => x.VodId == vodId);
                var info = clipInfo.FirstOrDefault(x => x.VodId == vodId);
                var vinfo = videoInfo.FirstOrDefault(x => x.VodId == vodId);
                var last = new[] { clips?.Last, marks?.Last, vids?.Last, edits?.Last }.Where(x => x != null).Max()!.Value;
                return new VodWorkDto(vodId, clips?.Count ?? 0, vids?.Count ?? 0, marks?.Segments ?? 0, marks?.Moments ?? 0, edits?.Count ?? 0,
                    info?.ChannelName ?? vinfo?.ChannelName, info?.StreamDateUtc ?? vinfo?.StreamDateUtc,
                    info?.ThumbnailUrl ?? vinfo?.ThumbnailUrl, last);
            }).OrderByDescending(x => x.LastWorkUtc).Take(Math.Clamp(limit ?? MaxVods, 1, MaxVods)).ToList();
            return Results.Ok(result);
        }
    }
}
