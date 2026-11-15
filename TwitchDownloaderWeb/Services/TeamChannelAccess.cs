using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services
{
    public enum VodAccess
    {
        Allowed,

        /// <summary>The VOD is from a channel the app Owner hasn't assigned to the team.</summary>
        NotAssigned,

        /// <summary>Twitch has no such VOD (deleted, expired or never existed).</summary>
        NotFound,

        /// <summary>Twitch couldn't be reached to find the VOD's channel.</summary>
        TwitchUnreachable,
    }

    /// <summary>
    /// Which VODs a team may work on (badrfly, 2026-11-15): members no longer add channels, VOD links or clip links; the app Owner assigns
    /// each team its channels (<see cref="SavedChannel"/>, on the registration link and on the Admin page) and the team sees only their VODs.
    /// A VOD the team already has library entries or markers on stays open, so work made before a channel was assigned or removed keeps
    /// playing. Scoped: it reads <see cref="LibraryDbContext.Channels"/> through the team query filter, so it must run inside a team request.
    /// </summary>
    public sealed class TeamChannelAccess(LibraryDbContext db, IVodDetailsSource vods, IMemoryCache cache)
    {
        /// <summary>A VOD's channel never changes, so it is cached for a long time (playback asks once per segment).</summary>
        private static readonly TimeSpan OwnerCacheTime = TimeSpan.FromHours(12);

        /// <summary>A team's answer for one VOD; short so an Owner's change applies within seconds.</summary>
        private static readonly TimeSpan DecisionCacheTime = TimeSpan.FromSeconds(30);

        private sealed record VodOwner(string? Login, string? UserId);

        public async Task<VodAccess> CheckVodAsync(int teamId, long vodId, CancellationToken cancellationToken)
        {
            var decisionKey = ("team-vod-access", teamId, Generations.GetValueOrDefault(teamId), vodId);
            if (cache.TryGetValue(decisionKey, out VodAccess cached))
                return cached;

            var result = await DecideAsync(vodId, cancellationToken);
            // Twitch being down is not a decision worth remembering
            if (result != VodAccess.TwitchUnreachable)
                cache.Set(decisionKey, result, DecisionCacheTime);
            return result;
        }

        /// <summary>Is <paramref name="login"/> one of the team's channels?</summary>
        public Task<bool> IsAssignedAsync(string login, CancellationToken cancellationToken)
        {
            var normalized = login.ToLowerInvariant();
            return db.Channels.AsNoTracking().AnyAsync(x => x.Login == normalized, cancellationToken);
        }

        /// <summary>Bumped when the Owner changes a team's channels, which makes every cached answer for that team stale at once.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Generations = new();

        /// <summary>Forget the team's cached answers, after the Owner changed its channels.</summary>
        public static void Forget(int teamId) => Generations.AddOrUpdate(teamId, 1, (_, n) => n + 1);

        private async Task<VodAccess> DecideAsync(long vodId, CancellationToken cancellationToken)
        {
            if (await db.Entries.AsNoTracking().AnyAsync(x => x.VodId == vodId, cancellationToken)
                || await db.Markers.AsNoTracking().AnyAsync(x => x.VodId == vodId, cancellationToken))
                return VodAccess.Allowed;

            var channels = await db.Channels.AsNoTracking().Select(x => new { x.Login, x.TwitchUserId }).ToListAsync(cancellationToken);
            if (channels.Count == 0)
                return VodAccess.NotAssigned;

            VodOwner? owner;
            try
            {
                owner = await GetOwnerAsync(vodId);
            }
            catch (HttpRequestException)
            {
                return VodAccess.TwitchUnreachable;
            }

            if (owner is null)
                return VodAccess.NotFound;

            // Compare by Twitch user ID first: a streamer can rename their login
            var match = channels.Any(x =>
                (!string.IsNullOrEmpty(owner.UserId) && x.TwitchUserId == owner.UserId)
                || (!string.IsNullOrEmpty(owner.Login) && string.Equals(x.Login, owner.Login, StringComparison.OrdinalIgnoreCase)));
            return match ? VodAccess.Allowed : VodAccess.NotAssigned;
        }

        private async Task<VodOwner?> GetOwnerAsync(long vodId)
        {
            var key = ("vod-owner", vodId);
            if (cache.TryGetValue(key, out VodOwner? cached))
                return cached;

            var details = await vods.GetVodAsync(vodId);
            if (details is null)
                return null; // not cached: an unpublished VOD can come back

            var owner = new VodOwner(details.ChannelLogin, details.ChannelUserId);
            cache.Set(key, owner, OwnerCacheTime);
            return owner;
        }

        /// <summary>The API answer for a VOD the team may not open, or null when it may.</summary>
        public static IResult? Refusal(VodAccess access, long vodId) => access switch
        {
            VodAccess.Allowed => null,
            VodAccess.NotFound => Results.Problem(detail: $"VOD {vodId} was not found.", statusCode: StatusCodes.Status404NotFound),
            VodAccess.TwitchUnreachable => Results.Problem(detail: "Couldn't reach Twitch. Try again in a moment.", statusCode: StatusCodes.Status502BadGateway),
            _ => Results.Problem(detail: "This VOD isn't from one of your team's channels.", statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = "channel-not-assigned" }),
        };
    }
}

namespace TwitchDownloaderWeb.Services
{
    using TwitchDownloaderWeb.Services.Accounts;

    public static class TeamChannelAccessExtensions
    {
        /// <summary>
        /// The route's <c>{vodId}</c> must be a VOD the team may open (<see cref="TeamChannelAccess"/>): 403 <c>channel-not-assigned</c>,
        /// 404 or 502 otherwise. Put it on every team endpoint that takes a VOD ID from the client.
        /// </summary>
        public static TBuilder RequireChannelVod<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
            builder.AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                if (TeamContext.Of(http) is not { } team
                    || !long.TryParse(http.Request.RouteValues["vodId"]?.ToString(), out var vodId))
                    return Results.Problem(detail: "Not allowed.", statusCode: StatusCodes.Status403Forbidden);

                var access = http.RequestServices.GetRequiredService<TeamChannelAccess>();
                var result = await access.CheckVodAsync(team.TeamId, vodId, http.RequestAborted);
                return TeamChannelAccess.Refusal(result, vodId) ?? await next(context);
            });
    }
}
