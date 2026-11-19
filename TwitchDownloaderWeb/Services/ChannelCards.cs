using System.Collections.Concurrent;
using TwitchDownloaderCore;
using TwitchDownloaderWeb.Services.Live;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// What a team card on the start screen shows about its channel (badrfly 2026-11-19).
    /// </summary>
    /// <param name="ProfileImageUrl">The channel's Twitch picture, or null.</param>
    /// <param name="Live">True = streaming now, false = not, null = unknown (Twitch unreachable).</param>
    /// <param name="LiveSinceUtc">While live: when the stream started.</param>
    /// <param name="LastLiveUtc">When the last stream ended (its VOD's start + length), or null if unknown / none kept.</param>
    public sealed record ChannelCardInfo(string? ProfileImageUrl, bool? Live, DateTime? LiveSinceUtc, DateTime? LastLiveUtc);

    /// <summary>Channel facts for the start screen's team cards. <see cref="ChannelCardService"/> in the app; a fake in tests.</summary>
    public interface IChannelCardSource
    {
        Task<ChannelCardInfo> GetAsync(string login, string? twitchUserId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Reads a channel's picture and live state from Twitch: the live stream watcher's state when it watches the channel, else the
    /// newest broadcast VOD (a recent one is checked for still recording, as the VOD list has no live flag). Cached per channel for 5 min
    /// (1 min after a failure), so the start screen never waits on Twitch twice in a row.
    /// </summary>
    public sealed class ChannelCardService : IChannelCardSource
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan CacheFailureFor = TimeSpan.FromMinutes(1);

        private readonly ChannelService _channels;
        private readonly IVodDetailsSource _vods;
        private readonly LiveStreamHub _hub;
        private readonly ILogger<ChannelCardService> _logger;
        private readonly ConcurrentDictionary<string, (DateTime Until, ChannelCardInfo Info)> _cache = new();

        public ChannelCardService(ChannelService channels, IVodDetailsSource vods, LiveStreamHub hub, ILogger<ChannelCardService> logger)
        {
            _channels = channels;
            _vods = vods;
            _hub = hub;
            _logger = logger;
        }

        public async Task<ChannelCardInfo> GetAsync(string login, string? twitchUserId, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            if (_cache.TryGetValue(login, out var cached) && cached.Until > now)
                return WithHub(login, cached.Info);

            var failed = false;
            string? picture = null;
            try
            {
                var id = twitchUserId;
                if (string.IsNullOrEmpty(id))
                    id = (await _channels.GetChannelAsync(login).WaitAsync(cancellationToken))?.TwitchUserId;
                if (!string.IsNullOrEmpty(id))
                    picture = (await TwitchHelper.GetUserInfo([id]).WaitAsync(cancellationToken)).data?.users?.FirstOrDefault(x => x is not null)?.profileImageURL;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = true;
                _logger.LogWarning("Couldn't read {Login}'s Twitch picture: {Error}", login, ex.Message);
            }

            bool? live = null;
            DateTime? since = null, lastLive = null;
            try
            {
                var newest = (await _channels.GetVodsAsync(login, VodType.Archive, null, 1).WaitAsync(cancellationToken))?.Items.FirstOrDefault();
                live = false;
                if (newest is not null)
                {
                    // The list has no live flag: a broadcast VOD from the last 2 days may still be recording
                    if (newest.CreatedAtUtc > now.AddDays(-2) && await _vods.GetVodAsync(newest.VodId).WaitAsync(cancellationToken) is { IsLive: true })
                    {
                        live = true;
                        since = newest.CreatedAtUtc;
                    }
                    else
                    {
                        lastLive = newest.CreatedAtUtc.AddSeconds(newest.LengthSeconds);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = true;
                _logger.LogWarning("Couldn't read {Login}'s last stream from Twitch: {Error}", login, ex.Message);
            }

            var info = new ChannelCardInfo(picture, live, since, lastLive);
            _cache[login] = (now + (failed ? CacheFailureFor : CacheFor), info);
            return WithHub(login, info);
        }

        /// <summary>The live stream watcher hears starts and ends the moment they happen, so it wins over the cached VOD list.</summary>
        private ChannelCardInfo WithHub(string login, ChannelCardInfo info)
        {
            if (_hub.Get(login) is not { Watched: true, Live: { } live } state)
                return info;
            if (live)
                return info with { Live = true, LiveSinceUtc = state.StartedAtUtc ?? info.LiveSinceUtc };
            // Ended since the VOD list was read: the stream that was live just ended
            return info.Live == true ? info with { Live = false, LiveSinceUtc = null, LastLiveUtc = DateTime.UtcNow } : info with { Live = false };
        }
    }
}
