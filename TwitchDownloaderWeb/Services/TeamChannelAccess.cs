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

        /// <summary>
        /// The VOD is from a channel the app Owner took away from the team ("Revoke now", or "Stop new VODs" for a VOD the team never
        /// worked on). Refused like <see cref="NotAssigned"/>, but the page says access was revoked (badrfly 2026-11-20).
        /// </summary>
        Revoked,
    }

    /// <summary>
    /// Which VODs a team may open (badrfly, 2026-11-15): members no longer add channels, VOD links or clip links; the app Owner assigns
    /// each team its channels (<see cref="SavedChannel"/>, on the registration link and on the Admin page) and the team opens only their VODs.
    /// Strict: a VOD whose channel isn't assigned is closed, even if the team worked on it. The one exception is a channel the Owner removed
    /// with "keep existing work" (<see cref="SavedChannel.AccessEndedAtUtc"/>): its VODs the team already has entries or markers on stay open.
    /// Scoped: it reads <see cref="LibraryDbContext.Channels"/> through the team query filter, so it must run inside a team request.
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

            var result = await DecideAsync(teamId, vodId, cancellationToken);
            // Twitch being down is not a decision worth remembering
            if (result != VodAccess.TwitchUnreachable)
                cache.Set(decisionKey, result, DecisionCacheTime);
            return result;
        }

        /// <summary>Is <paramref name="login"/> one of the team's channels?</summary>
        public Task<bool> IsAssignedAsync(string login, CancellationToken cancellationToken)
        {
            var normalized = login.ToLowerInvariant();
            return db.Channels.AsNoTracking().AnyAsync(x => x.Login == normalized && x.AccessEndedAtUtc == null, cancellationToken);
        }

        /// <summary>Bumped when the Owner changes a team's channels, which makes every cached answer for that team stale at once.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Generations = new();

        /// <summary>Forget the team's cached answers, after the Owner changed its channels.</summary>
        public static void Forget(int teamId)
        {
            Generations.AddOrUpdate(teamId, 1, (_, n) => n + 1);
            ChannelsChanged?.Invoke();
            TeamChannelsChanged?.Invoke(teamId);
        }

        /// <summary>
        /// One team's channels changed: its open pages hear it on <c>/api/live/events</c> (<c>channels</c>) and drop what they can no
        /// longer open, without a refresh (badrfly 2026-11-20).
        /// </summary>
        public static event Action<int>? TeamChannelsChanged;

        /// <summary>A team's channels changed (<see cref="Forget"/>): live stream updates follow the new set (<see cref="Live.EventSubWatcher"/>).</summary>
        public static event Action? ChannelsChanged;

        private async Task<VodAccess> DecideAsync(int teamId, long vodId, CancellationToken cancellationToken)
        {
            var channels = await db.Channels.AsNoTracking().Select(x => new { x.Login, x.TwitchUserId, Ended = x.AccessEndedAtUtc != null })
                .ToListAsync(cancellationToken);
            var revoked = await db.RevokedChannels.AsNoTracking().Where(x => x.TeamId == teamId)
                .Select(x => new { x.Login, x.TwitchUserId }).ToListAsync(cancellationToken);
            if (channels.Count == 0 && revoked.Count == 0)
                return VodAccess.NotAssigned;

            VodOwner? owner;
            try
            {
                owner = await GetOwnerAsync(vodId);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Timed out or answered oddly: "try again" (502), not a server error
                return VodAccess.TwitchUnreachable;
            }

            if (owner is null)
                return await IsGoneVodOfOwnChannelAsync(vodId, channels.Select(x => x.Login).ToList(), cancellationToken)
                    ? VodAccess.Allowed
                    : VodAccess.NotFound;

            // Compare by Twitch user ID first: a streamer can rename their login
            var match = channels.FirstOrDefault(x =>
                (!string.IsNullOrEmpty(owner.UserId) && x.TwitchUserId == owner.UserId)
                || (!string.IsNullOrEmpty(owner.Login) && string.Equals(x.Login, owner.Login, StringComparison.OrdinalIgnoreCase)));
            if (match is null)
                return revoked.Any(x =>
                    (!string.IsNullOrEmpty(owner.UserId) && x.TwitchUserId == owner.UserId)
                    || (!string.IsNullOrEmpty(owner.Login) && string.Equals(x.Login, owner.Login, StringComparison.OrdinalIgnoreCase)))
                    ? VodAccess.Revoked
                    : VodAccess.NotAssigned;
            if (!match.Ended)
                return VodAccess.Allowed;

            // Removed with "keep existing work": only VODs the team already worked on
            return await db.Entries.AsNoTracking().AnyAsync(x => x.VodId == vodId, cancellationToken)
                   || await db.Markers.AsNoTracking().AnyAsync(x => x.VodId == vodId, cancellationToken)
                ? VodAccess.Allowed
                : VodAccess.Revoked;
        }

        /// <summary>
        /// A VOD Twitch no longer has stays open when the team's own work on it names one of its channels (badrfly 2026-11-16: stored clips
        /// must stay exportable after Twitch deletes the VOD). Its channel can't be asked of Twitch any more, so it goes by the channel name
        /// saved on the team's entries and videos, as "Revoke now" does. A removed (revoked) channel has no row left, so it doesn't match.
        /// Everything that needs the VOD itself still fails on its own (playback 404, cuts "VOD gone").
        /// </summary>
        private async Task<bool> IsGoneVodOfOwnChannelAsync(long vodId, List<string> logins, CancellationToken cancellationToken)
        {
            var names = await db.Entries.AsNoTracking().Where(x => x.VodId == vodId && x.ChannelName != null).Select(x => x.ChannelName!)
                .Concat(db.Videos.AsNoTracking().Where(x => x.VodId == vodId && x.ChannelName != null).Select(x => x.ChannelName!))
                .Distinct().ToListAsync(cancellationToken);
            if (names.Count == 0)
                return false;

            var displayNames = await db.Channels.AsNoTracking().Select(x => x.DisplayName).ToListAsync(cancellationToken);
            return names.Any(name => logins.Concat(displayNames).Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// The team's channel row the VOD belongs to (still assigned), or null when it's none of them or Twitch has no such VOD. Throws
        /// <see cref="HttpRequestException"/> when Twitch is unreachable.
        /// </summary>
        public async Task<SavedChannel?> FindVodChannelAsync(long vodId, CancellationToken cancellationToken)
        {
            if (await GetOwnerAsync(vodId) is not { } owner)
                return null;
            var channels = await db.Channels.AsNoTracking().Where(x => x.AccessEndedAtUtc == null).ToListAsync(cancellationToken);
            return channels.FirstOrDefault(x => !string.IsNullOrEmpty(owner.UserId) && x.TwitchUserId == owner.UserId)
                   ?? channels.FirstOrDefault(x => !string.IsNullOrEmpty(owner.Login) && string.Equals(x.Login, owner.Login, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The VOD's channel (login, Twitch user ID), or null when Twitch has no such VOD. Throws <see cref="HttpRequestException"/> when Twitch is unreachable.</summary>
        public async Task<(string? Login, string? UserId)?> GetVodChannelAsync(long vodId) =>
            await GetOwnerAsync(vodId) is { } owner ? (owner.Login, owner.UserId) : null;

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
            // Same code as not assigned (callers check it), plus revoked: true so the page can say why
            VodAccess.Revoked => Results.Problem(detail: "Your team's access to this channel was revoked.", statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = "channel-not-assigned", ["revoked"] = true }),
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
