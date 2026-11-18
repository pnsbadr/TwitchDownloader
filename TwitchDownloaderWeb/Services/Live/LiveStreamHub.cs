using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TwitchDownloaderWeb.Services.Live
{
    /// <summary>A channel started (<see cref="Live"/>) or stopped streaming. Logins are lower-case.</summary>
    public sealed record StreamChange(string Login, string? UserId, bool Live, DateTime? StartedAtUtc, DateTime AtUtc);

    /// <summary>What the app knows about one watched channel right now.</summary>
    /// <param name="Live">Null = not known (never checked).</param>
    /// <param name="Watched">Twitch sends this channel's start/end events (false: over Twitch's limit, or not connected).</param>
    public sealed record ChannelLiveState(string Login, string? UserId, bool? Live, DateTime? StartedAtUtc, bool Watched);

    /// <summary>How the EventSub connection is doing, for the Admin page.</summary>
    public enum LiveWatchStatus
    {
        /// <summary>No Client ID or no Twitch sign-in: nothing is watched.</summary>
        NotConnected,
        Connecting,
        Watching,
        /// <summary>No team has a channel assigned.</summary>
        NoChannels,
        /// <summary>The connection dropped; trying again.</summary>
        Retrying,
    }

    /// <summary>
    /// Live stream state of the teams' channels and the fan-out of changes to open pages (badrfly 2026-11-18: cards update when a stream
    /// starts or ends, without refreshing and without checking on a timer). Fed by <see cref="EventSubWatcher"/> (WebSocket transport);
    /// a webhook transport for a shared public server would call <see cref="Apply"/> the same way. In memory only: after a restart the
    /// watcher's catch-up check fills it again.
    /// </summary>
    public sealed class LiveStreamHub
    {
        private readonly ConcurrentDictionary<string, ChannelLiveState> _channels = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Channel<StreamChange>, byte> _listeners = new();
        private readonly ILogger<LiveStreamHub> _logger;

        public LiveStreamHub(ILogger<LiveStreamHub> logger)
        {
            _logger = logger;
        }

        public LiveWatchStatus Status { get; private set; } = LiveWatchStatus.NotConnected;
        public string? StatusDetail { get; private set; }
        public DateTime? ConnectedAtUtc { get; private set; }

        public void SetStatus(LiveWatchStatus status, string? detail = null)
        {
            Status = status;
            StatusDetail = detail;
            if (status == LiveWatchStatus.Watching)
                ConnectedAtUtc = DateTime.UtcNow;
            if (status != LiveWatchStatus.Watching)
            {
                // Nothing tells us about these channels any more; pages fall back to looking themselves
                foreach (var (login, state) in _channels)
                    _channels[login] = state with { Watched = false };
            }
        }

        public bool Watching => Status == LiveWatchStatus.Watching;

        public ChannelLiveState? Get(string login) => _channels.TryGetValue(login, out var state) ? state : null;

        public IReadOnlyList<ChannelLiveState> All => _channels.Values.OrderBy(x => x.Login, StringComparer.Ordinal).ToList();

        /// <summary>The channels being followed now (after a connect or a channel change); others are dropped.</summary>
        public void SetChannels(IEnumerable<(string Login, string? UserId, bool Watched)> channels)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (login, userId, watched) in channels)
            {
                keep.Add(login);
                _channels.AddOrUpdate(login,
                    _ => new ChannelLiveState(login.ToLowerInvariant(), userId, null, null, watched),
                    (_, old) => old with { UserId = userId ?? old.UserId, Watched = watched });
            }
            foreach (var login in _channels.Keys.Where(x => !keep.Contains(x)).ToList())
                _channels.TryRemove(login, out _);
        }

        /// <summary>
        /// A channel's state as Twitch says it is now (an event, or the catch-up check after connecting). Pages hear about it only when
        /// it differs from what was known (a repeated or late event changes nothing).
        /// </summary>
        /// <returns>Whether it changed.</returns>
        public bool Apply(StreamChange change)
        {
            var login = change.Login.ToLowerInvariant();
            var old = Get(login);
            var updated = new ChannelLiveState(login, change.UserId ?? old?.UserId, change.Live, change.Live ? change.StartedAtUtc ?? old?.StartedAtUtc : null,
                old?.Watched ?? false);
            _channels[login] = updated;
            if (old?.Live == change.Live && (!change.Live || old.StartedAtUtc == updated.StartedAtUtc))
                return false;

            _logger.LogInformation("{Login} {What}.", login, change.Live ? "went live" : "stopped streaming");
            var sent = change with { Login = login, StartedAtUtc = updated.StartedAtUtc };
            foreach (var listener in _listeners.Keys)
                listener.Writer.TryWrite(sent);
            Changed?.Invoke(sent);
            return true;
        }

        /// <summary>For server-side followers (the audio rescue). Runs on the caller's thread: keep it short.</summary>
        public event Action<StreamChange>? Changed;

        /// <summary>Changes from now on, for one open page. Dispose to stop.</summary>
        public Listener Listen() => new(this);

        public sealed class Listener : IDisposable
        {
            private readonly LiveStreamHub _hub;
            private readonly Channel<StreamChange> _channel = Channel.CreateBounded<StreamChange>(
                new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

            internal Listener(LiveStreamHub hub)
            {
                _hub = hub;
                hub._listeners[_channel] = 0;
            }

            public ChannelReader<StreamChange> Reader => _channel.Reader;

            public void Dispose()
            {
                _hub._listeners.TryRemove(_channel, out _);
                _channel.Writer.TryComplete();
            }
        }

        public int ListenerCount => _listeners.Count;
    }
}
