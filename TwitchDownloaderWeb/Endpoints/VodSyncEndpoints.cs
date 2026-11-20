using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <summary>
    /// One VOD of the other channel that was recording during part of the asked range.
    /// <paramref name="Start"/> / <paramref name="End"/> = the matching part in that VOD's seconds; <paramref name="Offset"/> = where it
    /// begins in the asked range (0 = at its start).
    /// </summary>
    public sealed record VodSyncMatch(long VodId, string Title, DateTime CreatedAtUtc, int LengthSeconds, bool IsLive, bool Restricted,
        double Start, double End, double Offset);

    /// <summary>The nearest VOD of the other channel before or after the range, when nothing matched.</summary>
    public sealed record VodSyncNeighbor(long VodId, string Title, DateTime StartedAtUtc, DateTime EndedAtUtc);

    /// <param name="CoveredSeconds">How much of the asked range the matches cover together.</param>
    public sealed record VodSyncDto(string Login, string DisplayName, DateTime RangeStartUtc, DateTime RangeEndUtc, double CoveredSeconds,
        IReadOnlyList<VodSyncMatch> Matches, VodSyncNeighbor? Before, VodSyncNeighbor? After);

    /// <summary>
    /// "Sync with" (badrfly 2026-11-20): the same moment from another streamer's point of view. A range of one VOD is turned into wall-clock
    /// time with the VOD's start (Twitch's createdAt), and the other channel's past broadcasts recording at that time are found the same way.
    /// The page adds what matched as a new row in Player 2, under the piece it came from.
    /// </summary>
    public static class VodSyncEndpoints
    {
        /// <summary>Twitch ends a broadcast's VOD after 48 hours, so nothing older than that before the range can reach into it.</summary>
        private static readonly TimeSpan LongestStream = TimeSpan.FromHours(48);
        private const int PageSize = 100;
        private const int MaxPages = 10;
        private const int MaxRangeSeconds = 3 * 3600;

        public static IEndpointRouteBuilder MapVodSyncEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/{vodId}/sync", Find).RequireTeam(TeamPermission.ViewLibrary).RequireChannelVod();
            return app;
        }

        /// <param name="vodId">The VOD the range is on.</param>
        /// <param name="channel">One of the team's channels: the other point of view.</param>
        /// <param name="start">The range in <paramref name="vodId"/>'s seconds.</param>
        private static async Task<IResult> Find(long vodId, string? channel, double? start, double? end, IVodDetailsSource vods, IChannelVodSource channelVods,
            IVodQualitySource qualities, TeamChannelAccess access, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (ChannelService.NormalizeLogin(channel) is not { } login)
                return ChannelEndpoints.InvalidLogin("channel");
            if (start is not { } from || end is not { } to || !double.IsFinite(from) || !double.IsFinite(to) || from < 0 || to - from < 0.5)
                return Invalid("end", "Give a start and an end at least half a second apart.");
            if (to - from > MaxRangeSeconds)
                return Invalid("end", "Sync at most 3 hours at a time.");
            if (!await access.IsAssignedAsync(login, cancellationToken))
                return ChannelEndpoints.NotAssigned(login);

            VodDetails? source;
            try
            {
                source = await vods.GetVodAsync(vodId);
            }
            catch (HttpRequestException)
            {
                return ChannelEndpoints.TwitchUnreachable();
            }
            if (source is null)
                return Results.Problem(detail: "Twitch no longer has this VOD, so its stream time isn't known.", statusCode: StatusCodes.Status404NotFound,
                    extensions: new Dictionary<string, object?> { ["code"] = "vod-gone" });
            if (string.Equals(source.ChannelLogin, login, StringComparison.OrdinalIgnoreCase))
                return Results.Problem(detail: "That's this VOD's own channel. Pick another streamer.", statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "same-channel" });

            var rangeStart = source.StreamDateUtc.AddSeconds(from);
            var rangeEnd = source.StreamDateUtc.AddSeconds(to);

            // Past broadcasts only: a highlight's or upload's date is when it was made, not when it was streamed
            var found = new List<ChannelVod>();
            string? cursor = null;
            try
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    var result = await channelVods.GetVodsAsync(login, VodType.Archive, cursor, PageSize);
                    if (result is null)
                        return ChannelEndpoints.ChannelNotFound(login);
                    found.AddRange(result.Items);
                    cursor = result.NextCursor;
                    if (cursor is null || result.Items.Count == 0 || result.Items.Min(x => x.CreatedAtUtc) < rangeStart - LongestStream)
                        break;
                }
            }
            catch (HttpRequestException)
            {
                return ChannelEndpoints.TwitchUnreachable();
            }

            var matches = new List<VodSyncMatch>();
            foreach (var vod in found.Where(x => x.VodId != vodId).OrderBy(x => x.CreatedAtUtc))
            {
                var vodEnd = vod.CreatedAtUtc.AddSeconds(vod.LengthSeconds);
                var overlapStart = vod.CreatedAtUtc > rangeStart ? vod.CreatedAtUtc : rangeStart;
                var overlapEnd = vodEnd < rangeEnd ? vodEnd : rangeEnd;
                if ((overlapEnd - overlapStart).TotalSeconds < 0.5)
                    continue;

                // Live VODs grow; the details say whether it's still recording. Sub-only ones can't play in the app's player.
                bool live = false, restricted = false;
                try
                {
                    if (matches.Count < 3)
                    {
                        live = (await vods.GetVodAsync(vod.VodId))?.IsLive ?? false;
                        restricted = await qualities.GetQualitiesAsync(vod.VodId) is null;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not knowing is fine: the player says so when it tries
                }

                matches.Add(new VodSyncMatch(vod.VodId, vod.Title, vod.CreatedAtUtc, vod.LengthSeconds, live, restricted,
                    Math.Round((overlapStart - vod.CreatedAtUtc).TotalSeconds, 3), Math.Round((overlapEnd - vod.CreatedAtUtc).TotalSeconds, 3),
                    Math.Round((overlapStart - rangeStart).TotalSeconds, 3)));
            }

            var covered = Math.Round(matches.Sum(x => x.End - x.Start), 3);
            VodSyncNeighbor? before = null, after = null;
            if (matches.Count == 0)
            {
                VodSyncNeighbor Neighbor(ChannelVod x) => new(x.VodId, x.Title, x.CreatedAtUtc, x.CreatedAtUtc.AddSeconds(x.LengthSeconds));
                before = found.Where(x => x.CreatedAtUtc.AddSeconds(x.LengthSeconds) <= rangeStart).MaxBy(x => x.CreatedAtUtc) is { } b ? Neighbor(b) : null;
                after = found.Where(x => x.CreatedAtUtc >= rangeEnd).MinBy(x => x.CreatedAtUtc) is { } a ? Neighbor(a) : null;
            }

            var displayName = await db.Channels.AsNoTracking().Where(x => x.Login == login).Select(x => x.DisplayName).FirstOrDefaultAsync(cancellationToken) ?? login;
            return Results.Ok(new VodSyncDto(login, displayName, rangeStart, rangeEnd, covered, matches, before, after));
        }

        private static IResult Invalid(string field, string message) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
    }
}
