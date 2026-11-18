using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Live
{
    /// <summary>
    /// Hears from Twitch when the teams' channels start and stop streaming (badrfly 2026-11-18), with EventSub over a WebSocket from this
    /// app (no public address needed, so it works on a PC and in a desktop wrapper). Subscribes stream.online + stream.offline for every
    /// channel assigned to an enabled team, as long as Twitch's limit allows (a WebSocket sign-in may have subscriptions costing 10 in
    /// total: 5 channels; the rest are listed as not watched and the page looks for itself as before). Events go to
    /// <see cref="LiveStreamHub"/>, which tells open pages.
    ///
    /// Nothing is checked on a timer. Events sent while the socket is down are lost, so after every (re)connect one Helix "Get Streams"
    /// call brings the live state up to date. A channel change (<see cref="Services.TeamChannelAccess.Forget"/>) or a sign-in change starts
    /// a new session with the new channel set.
    /// </summary>
    public sealed class EventSubWatcher : BackgroundService
    {
        public const string HttpClientName = "TwitchHelix";
        private const string HelixBase = "https://api.twitch.tv/helix";
        private static readonly Uri SocketUrl = new("wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30");
        private static readonly string[] EventTypes = ["stream.online", "stream.offline"];

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TwitchUserAuth _auth;
        private readonly LiveStreamHub _hub;
        private readonly IChannelLookup _channelLookup;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EventSubWatcher> _logger;
        private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private CancellationTokenSource? _session;

        public EventSubWatcher(IServiceScopeFactory scopeFactory, TwitchUserAuth auth, LiveStreamHub hub, IChannelLookup channelLookup,
            IHttpClientFactory httpClientFactory, ILogger<EventSubWatcher> logger)
        {
            _scopeFactory = scopeFactory;
            _auth = auth;
            _hub = hub;
            _channelLookup = channelLookup;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>The channel set or sign-in changed: end the session and start a new one.</summary>
        public void Restart()
        {
            _signal.Writer.TryWrite(true);
            try
            {
                _session?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That session just ended
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _auth.Changed += Restart;
            Services.TeamChannelAccess.ChannelsChanged += Restart;
            try
            {
                var failures = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    _signal.Reader.TryRead(out _);
                    if (!await _auth.IsReadyAsync(stoppingToken))
                    {
                        _hub.SetStatus(LiveWatchStatus.NotConnected);
                        await WaitForSignalAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                        continue;
                    }

                    using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    _session = session;
                    var startedUtc = DateTime.UtcNow;
                    var restartNow = false;
                    try
                    {
                        _hub.SetStatus(LiveWatchStatus.Connecting);
                        restartNow = await RunSessionAsync(session.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        restartNow = true; // Restart(): channels or sign-in changed
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Messages only: they never carry tokens (sent in headers, not URLs)
                        _logger.LogWarning("Live stream updates: connection to Twitch lost ({Message}).", ex.Message);
                    }
                    finally
                    {
                        _session = null;
                    }

                    if (stoppingToken.IsCancellationRequested)
                        return;
                    if (restartNow)
                    {
                        failures = 0;
                        continue;
                    }

                    failures = DateTime.UtcNow - startedUtc > TimeSpan.FromMinutes(5) ? 1 : failures + 1;
                    var wait = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(failures - 1, 6))));
                    _hub.SetStatus(LiveWatchStatus.Retrying, $"Trying again in {(int)wait.TotalSeconds} s.");
                    await WaitForSignalAsync(wait, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            finally
            {
                _auth.Changed -= Restart;
                Services.TeamChannelAccess.ChannelsChanged -= Restart;
            }
        }

        private async Task WaitForSignalAsync(TimeSpan wait, CancellationToken stoppingToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (wait != Timeout.InfiniteTimeSpan)
                timeout.CancelAfter(wait);
            try
            {
                await _signal.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Waited long enough
            }
        }

        /// <returns>True when the session ended because something changed (start again at once); false on a lost connection.</returns>
        private async Task<bool> RunSessionAsync(CancellationToken cancellationToken)
        {
            var channels = await LoadChannelsAsync(cancellationToken);
            if (channels.Count == 0)
            {
                _hub.SetChannels([]);
                _hub.SetStatus(LiveWatchStatus.NoChannels);
                await WaitForSignalAsync(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }

            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(SocketUrl, cancellationToken);
                var welcome = await ReceiveWelcomeAsync(socket, cancellationToken);

                // Twitch closes a socket with no subscription after 10 s, so subscribe first and catch up after
                var watched = await SubscribeAsync(welcome.SessionId, channels, cancellationToken);
                _hub.SetChannels(channels.Select(c => (c.Login, (string?)c.UserId, watched.Contains(c.UserId))));
                if (watched.Count == 0)
                    throw new InvalidOperationException("Twitch refused every subscription");
                await CatchUpAsync(channels, cancellationToken);

                _hub.SetStatus(LiveWatchStatus.Watching, watched.Count < channels.Count
                    ? $"{channels.Count - watched.Count} of {channels.Count} channels are over Twitch's limit for one Twitch account and aren't watched."
                    : null);
                _logger.LogInformation("Live stream updates: watching {Watched} of {Total} channels.", watched.Count, channels.Count);

                var keepalive = welcome.KeepaliveSeconds;
                while (true)
                {
                    using var message = await ReceiveAsync(socket, TimeSpan.FromSeconds(keepalive + 10), cancellationToken);
                    if (message is null)
                        return false; // closed by Twitch, or no keepalive in time

                    switch (EventSubMessages.TypeOf(message.RootElement))
                    {
                        case "notification":
                            if (EventSubMessages.ReadStreamChange(message.RootElement) is { } change)
                                OnChange(change);
                            break;

                        case "session_reconnect":
                            // Twitch moves us to another server; subscriptions carry over. Keep the old socket until the new one says welcome.
                            var url = EventSubMessages.ReconnectUrl(message.RootElement);
                            if (url is null)
                                return false;
                            var next = new ClientWebSocket();
                            try
                            {
                                await next.ConnectAsync(url, cancellationToken);
                                var nextWelcome = await ReceiveWelcomeAsync(next, cancellationToken);
                                keepalive = nextWelcome.KeepaliveSeconds;
                            }
                            catch
                            {
                                next.Dispose();
                                throw;
                            }
                            await CloseQuietlyAsync(socket);
                            socket.Dispose();
                            socket = next;
                            break;

                        case "revocation":
                            _logger.LogWarning("Twitch stopped a live stream subscription ({Reason}).", EventSubMessages.RevocationReason(message.RootElement));
                            break;
                    }
                }
            }
            finally
            {
                await CloseQuietlyAsync(socket);
                socket.Dispose();
            }
        }

        private void OnChange(StreamChange change)
        {
            // An event names the channel by ID; keep the login the teams know it by
            var login = _hub.All.FirstOrDefault(x => x.UserId == change.UserId)?.Login ?? change.Login;
            _hub.Apply(change with { Login = login });
        }

        private sealed record TrackedChannel(string Login, string UserId);

        /// <summary>Channels assigned to enabled teams (each once), most-shared first, with Twitch user IDs looked up where missing.</summary>
        private async Task<List<TrackedChannel>> LoadChannelsAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var rows = await db.Channels.IgnoreQueryFilters()
                .Where(x => x.AccessEndedAtUtc == null && db.Teams.Any(t => t.Id == x.TeamId && t.DisabledAtUtc == null))
                .ToListAsync(cancellationToken);

            var result = new List<TrackedChannel>();
            foreach (var group in rows.GroupBy(x => x.Login).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var userId = group.Select(x => x.TwitchUserId).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                if (userId is null)
                {
                    try
                    {
                        userId = (await _channelLookup.GetChannelAsync(group.Key))?.TwitchUserId;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogWarning("Couldn't look up {Login} on Twitch: {Message}", group.Key, ex.Message);
                    }
                    if (userId is null)
                        continue;
                    foreach (var row in group)
                        row.TwitchUserId = userId;
                }
                result.Add(new TrackedChannel(group.Key, userId));
            }
            await db.SaveChangesAsync(cancellationToken);
            return result;
        }

        /// <returns>User IDs of the channels whose start and end events Twitch will send.</returns>
        private async Task<HashSet<string>> SubscribeAsync(string sessionId, List<TrackedChannel> channels, CancellationToken cancellationToken)
        {
            var watched = new HashSet<string>();
            foreach (var channel in channels)
            {
                var both = true;
                foreach (var type in EventTypes)
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        type,
                        version = "1",
                        condition = new { broadcaster_user_id = channel.UserId },
                        transport = new { method = "websocket", session_id = sessionId },
                    });
                    using var response = await HelixAsync(HttpMethod.Post, "eventsub/subscriptions", body, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        return watched; // over Twitch's cost limit: the remaining channels aren't watched
                    if (response.StatusCode == HttpStatusCode.Conflict)
                        continue; // already there
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Twitch refused the {Type} subscription for {Login} ({Status}).", type, channel.Login, (int)response.StatusCode);
                        both = false;
                        break;
                    }
                }
                if (both)
                    watched.Add(channel.UserId);
            }
            return watched;
        }

        /// <summary>One "Get Streams" call per 100 channels: what's live now, since events are lost while disconnected.</summary>
        private async Task CatchUpAsync(List<TrackedChannel> channels, CancellationToken cancellationToken)
        {
            foreach (var chunk in channels.Chunk(100))
            {
                var query = string.Join("&", chunk.Select(c => $"user_id={Uri.EscapeDataString(c.UserId)}"));
                using var response = await HelixAsync(HttpMethod.Get, $"streams?first=100&type=live&{query}", null, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Couldn't check which channels are live ({Status}).", (int)response.StatusCode);
                    return;
                }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var live = EventSubMessages.ReadLiveStreams(json.RootElement);
                var now = DateTime.UtcNow;
                foreach (var channel in chunk)
                {
                    live.TryGetValue(channel.UserId, out var startedAt);
                    _hub.Apply(new StreamChange(channel.Login, channel.UserId, live.ContainsKey(channel.UserId), startedAt, now));
                }
            }
        }

        private async Task<HttpResponseMessage> HelixAsync(HttpMethod method, string path, string? jsonBody, CancellationToken cancellationToken)
        {
            var clientId = await _auth.GetClientIdAsync(cancellationToken) ?? throw new TwitchAuthException("No Client ID");
            var client = _httpClientFactory.CreateClient(HttpClientName);
            for (var attempt = 0; ; attempt++)
            {
                var token = await _auth.GetAccessTokenAsync(attempt > 0, cancellationToken) ?? throw new TwitchAuthException("Not signed in to Twitch");
                var request = new HttpRequestMessage(method, $"{HelixBase}/{path}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("Client-Id", clientId);
                if (jsonBody is not null)
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                var response = await client.SendAsync(request, cancellationToken);
                request.Dispose();
                if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
                    return response;
                response.Dispose();
            }
        }

        private sealed record Welcome(string SessionId, int KeepaliveSeconds);

        private static async Task<Welcome> ReceiveWelcomeAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            using var message = await ReceiveAsync(socket, TimeSpan.FromSeconds(15), cancellationToken)
                ?? throw new InvalidOperationException("Twitch sent no welcome");
            return EventSubMessages.ReadWelcome(message.RootElement) is { } welcome
                ? new Welcome(welcome.SessionId, welcome.KeepaliveSeconds)
                : throw new InvalidOperationException("Twitch's first message wasn't a welcome");
        }

        /// <returns>The next message, or null if the socket closed or nothing came within <paramref name="timeout"/>.</returns>
        private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket socket, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(timeout);
            var buffer = new byte[16 * 1024];
            using var text = new MemoryStream();
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, wait.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;
                    text.Write(buffer, 0, result.Count);
                    if (text.Length > 1024 * 1024)
                        throw new InvalidOperationException("Twitch sent a message that is too big");
                    if (result.EndOfMessage)
                        break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null; // keepalive missed: the connection is gone
            }
            return JsonDocument.Parse(text.ToArray());
        }

        private static async Task CloseQuietlyAsync(ClientWebSocket socket)
        {
            if (socket.State != WebSocketState.Open)
                return;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }
    }
}
