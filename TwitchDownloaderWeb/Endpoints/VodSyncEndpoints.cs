using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services.Sync;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <summary>
    /// One VOD of the other channel that was recording during part of the asked range.
    /// <paramref name="Start"/> / <paramref name="End"/> = the matching part in that VOD's seconds; <paramref name="Offset"/> = where it
    /// begins in the asked range (0 = at its start).
    /// </summary>
    /// <param name="Lined">How the times were lined up: "start" (stream start times only), or the pair's saved sync ("audio" / "manual",
    /// see <see cref="VodSyncOffset"/>).</param>
    public sealed record VodSyncMatch(long VodId, string Title, DateTime CreatedAtUtc, int LengthSeconds, bool IsLive, bool Restricted,
        double Start, double End, double Offset, string Lined = "start");

    /// <param name="At">A moment in the VOD of the route, in its seconds.</param>
    /// <param name="OtherAt">The same moment in the other VOD.</param>
    public sealed record VodSyncPairRequest(double? At, double? OtherAt, double? Seconds = null);

    /// <param name="Trusted">The audio matched well enough to use (and was saved for the pair).</param>
    /// <param name="Shift">How far the other VOD's moment moved from <c>otherAt</c> (+ = later).</param>
    public sealed record VodSyncAudioDto(bool Trusted, double Shift, double OtherAt, double Correlation, double Confidence, string? Reason);

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
            app.MapPut("/api/vods/{vodId}/sync/{otherVodId}", SavePair).RequireTeam(TeamPermission.AddEntries).RequireChannelVod();
            app.MapPost("/api/vods/{vodId}/sync/{otherVodId}/audio", MatchAudio).RequireTeam(TeamPermission.AddEntries).RequireChannelVod();
            app.MapGet("/api/vods/{vodId}/waves", Waves).RequireTeam(TeamPermission.ViewLibrary).RequireChannelVod();
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

            // A pair lined up before (audio match or a member's nudge) uses that instead of the start times alone
            var foundIds = found.Select(x => x.VodId).ToList();
            var saved = await db.VodSyncOffsets.AsNoTracking()
                .Where(x => (x.VodA == vodId && foundIds.Contains(x.VodB)) || (x.VodB == vodId && foundIds.Contains(x.VodA)))
                .ToListAsync(cancellationToken);

            var matches = new List<VodSyncMatch>();
            foreach (var vod in found.Where(x => x.VodId != vodId).OrderBy(x => x.CreatedAtUtc))
            {
                var pair = saved.FirstOrDefault(x => x.VodA == vod.VodId || x.VodB == vod.VodId);
                var correction = pair is null ? 0 : CorrectionFrom(pair, vodId);
                // The same moment is `correction` seconds later in this VOD: as if it had started that much earlier
                var started = vod.CreatedAtUtc.AddSeconds(-correction);
                var vodEnd = started.AddSeconds(vod.LengthSeconds);
                var overlapStart = started > rangeStart ? started : rangeStart;
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
                    Math.Round((overlapStart - started).TotalSeconds, 3), Math.Round((overlapEnd - started).TotalSeconds, 3),
                    Math.Round((overlapStart - rangeStart).TotalSeconds, 3), pair?.Source ?? "start"));
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

        /// <summary>Seconds the same moment is later in the other VOD of <paramref name="pair"/> than stream start times say, seen from <paramref name="fromVod"/>.</summary>
        internal static double CorrectionFrom(VodSyncOffset pair, long fromVod) => pair.VodA == fromVod ? pair.Seconds : -pair.Seconds;

        /// <summary>Checks both VODs and reads their start times.</summary>
        private static async Task<(VodDetails A, VodDetails B, IResult? Problem)> PairAsync(long vodId, long otherVodId, TeamContext team, IVodDetailsSource vods,
            TeamChannelAccess access, CancellationToken cancellationToken)
        {
            if (otherVodId == vodId || otherVodId <= 0)
                return (null!, null!, Invalid("otherVodId", "Pick another VOD."));
            if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, otherVodId, cancellationToken), otherVodId) is { } refused)
                return (null!, null!, refused);
            VodDetails? a, b;
            try
            {
                a = await vods.GetVodAsync(vodId);
                b = await vods.GetVodAsync(otherVodId);
            }
            catch (HttpRequestException)
            {
                return (null!, null!, ChannelEndpoints.TwitchUnreachable());
            }
            if (a is null || b is null)
                return (null!, null!, Results.Problem(detail: "Twitch no longer has one of these VODs.", statusCode: StatusCodes.Status404NotFound,
                    extensions: new Dictionary<string, object?> { ["code"] = "vod-gone" }));
            return (a, b, null);
        }

        /// <summary>Saves how the pair lines up, given one moment's time in each.</summary>
        private static async Task<VodSyncOffset> StoreAsync(LibraryDbContext db, TeamContext team, VodDetails a, double at, VodDetails b, double otherAt,
            string source, double? confidence, CancellationToken cancellationToken)
        {
            // Correction from a to b: otherAt = at + (a.start - b.start) + c
            var c = otherAt - at - (a.StreamDateUtc - b.StreamDateUtc).TotalSeconds;
            var (lowId, highId, seconds) = a.VodId < b.VodId ? (a.VodId, b.VodId, c) : (b.VodId, a.VodId, -c);
            var row = await db.VodSyncOffsets.FirstOrDefaultAsync(x => x.VodA == lowId && x.VodB == highId, cancellationToken);
            if (row is null)
            {
                row = new VodSyncOffset { VodA = lowId, VodB = highId };
                db.VodSyncOffsets.Add(row);
            }
            row.Seconds = Math.Round(seconds, 3);
            row.Source = source;
            row.Confidence = confidence;
            row.UpdatedByUserId = team.UserId == 0 ? null : team.UserId;
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return row;
        }

        /// <summary>No VOD is longer than Twitch's 48 h stream limit: anything past it is a bad request, never a saved line-up.</summary>
        private const double MaxVodSeconds = 48 * 3600;

        /// <summary>A member lined the two up by hand (a slide in Player 2): later syncs between them use it.</summary>
        private static async Task<IResult> SavePair(long vodId, long otherVodId, VodSyncPairRequest request, TeamContext team, LibraryDbContext db,
            IVodDetailsSource vods, TeamChannelAccess access, CancellationToken cancellationToken)
        {
            if (request.At is not { } at || request.OtherAt is not { } otherAt || !double.IsFinite(at) || !double.IsFinite(otherAt) || at < 0 || otherAt < 0
                || at > MaxVodSeconds || otherAt > MaxVodSeconds)
                return Invalid("at", "Give the moment's time in both VODs.");
            var (a, b, problem) = await PairAsync(vodId, otherVodId, team, vods, access, cancellationToken);
            if (problem is not null)
                return problem;
            var row = await StoreAsync(db, team, a, at, b, otherAt, VodSyncOffset.ByHand, null, cancellationToken);
            return Results.Ok(new { vodA = row.VodA, vodB = row.VodB, seconds = row.Seconds, source = row.Source });
        }

        /// <summary>How far around the guess the audio match looks.</summary>
        private const double SearchSeconds = 20;

        /// <summary>
        /// Lines the pair up by sound: <c>seconds</c> (default 45) of this VOD from <c>at</c> are looked for in the other VOD within 20 s of
        /// <c>otherAt</c> (the stream-time guess). A good match is saved for the pair; a weak one changes nothing and says why.
        /// </summary>
        private static async Task<IResult> MatchAudio(long vodId, long otherVodId, VodSyncPairRequest request, TeamContext team, LibraryDbContext db,
            IVodDetailsSource vods, TeamChannelAccess access, IVodLoudness loudness, CancellationToken cancellationToken)
        {
            if (request.At is not { } at || request.OtherAt is not { } otherAt || !double.IsFinite(at) || !double.IsFinite(otherAt) || at < 0 || otherAt < 0
                || at > MaxVodSeconds || otherAt > MaxVodSeconds)
                return Invalid("at", "Give the moment's time in both VODs.");
            var seconds = Math.Clamp(request.Seconds ?? 45, 10, 90);
            var (a, b, problem) = await PairAsync(vodId, otherVodId, team, vods, access, cancellationToken);
            if (problem is not null)
                return problem;

            var searchFrom = Math.Max(0, otherAt - SearchSeconds);
            float[]? mine, theirs;
            try
            {
                mine = await loudness.GetAsync(vodId, at, seconds, cancellationToken);
                theirs = mine is null ? null : await loudness.GetAsync(otherVodId, searchFrom, seconds + (otherAt - searchFrom) + SearchSeconds, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return ChannelEndpoints.TwitchUnreachable();
            }
            catch (InvalidOperationException)
            {
                // ffmpeg failed on the audio
                mine = theirs = null;
            }

            var found = mine is null || theirs is null ? null : AudioAligner.Find(mine, theirs);
            if (found is null)
                return Results.Ok(new VodSyncAudioDto(false, 0, otherAt, 0, 0, mine is null || theirs is null ? "Couldn't read the audio." : "One of them is silent there."));

            var matchedAt = Math.Round(searchFrom + (double)found.Lag / AudioAligner.Rate, 3);
            var shift = Math.Round(matchedAt - otherAt, 3);
            if (!found.Trusted)
                return Results.Ok(new VodSyncAudioDto(false, shift, otherAt, Math.Round(found.Correlation, 3), Math.Round(found.Confidence, 2),
                    "The sound didn't match clearly (are they in the same voice call?)."));

            await StoreAsync(db, team, a, at, b, matchedAt, VodSyncOffset.FromAudio, Math.Round(found.Confidence, 2), cancellationToken);
            return Results.Ok(new VodSyncAudioDto(true, shift, matchedAt, Math.Round(found.Correlation, 3), Math.Round(found.Confidence, 2), null));
        }

        /// <summary>
        /// Sound waves (badrfly 2026-11-20): one block of <see cref="VodWaveService.BlockSeconds"/> from <c>block × 60</c> s, as
        /// <c>{ block, start, rate, levels }</c> (levels = base64 bytes, 0 silent … 255 loud, <c>rate</c> a second; empty past the VOD's end).
        /// </summary>
        private static async Task<IResult> Waves(long vodId, int? block, VodWaveService waves, HttpContext http, CancellationToken cancellationToken)
        {
            if (block is not { } n || n < 0 || n > 48 * 3600 / VodWaveService.BlockSeconds)
                return Invalid("block", "Give the block number (60 seconds each, from 0).");
            byte[]? levels;
            try
            {
                levels = await waves.GetBlockAsync(vodId, n, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
            if (levels is null)
                return Results.NotFound(new { code = "no-audio", message = "This VOD's sound can't be read." });
            // A whole block never changes; a short one (live, or just read) may still grow
            http.Response.Headers.CacheControl = levels.Length >= VodWaveService.BlockSeconds * AudioAligner.WaveRate - AudioAligner.WaveRate / 2
                ? "private, max-age=86400" : "no-store";
            return Results.Ok(new { block = n, start = n * VodWaveService.BlockSeconds, rate = AudioAligner.WaveRate, levels = Convert.ToBase64String(levels) });
        }

        private static IResult Invalid(string field, string message) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
    }
}
