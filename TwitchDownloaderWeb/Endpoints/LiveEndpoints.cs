using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services.Live;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="Live">Null = not known yet.</param>
    /// <param name="Watched">Twitch tells the app when this channel starts or stops (else the page looks for itself, as before).</param>
    public sealed record LiveChannelDto(string Login, bool? Live, DateTime? StartedAtUtc, bool Watched);

    /// <param name="Watching">The app is connected to Twitch's live stream events.</param>
    public sealed record LiveHelloDto(bool Watching, IReadOnlyList<LiveChannelDto> Channels);

    public sealed record LiveStreamDto(string Login, bool Live, DateTime? StartedAtUtc, DateTime AtUtc);

    /// <param name="Logins">The team's channels now (the Owner added, revoked or ended one).</param>
    public sealed record LiveChannelsDto(IReadOnlyList<string> Logins);

    /// <param name="ClientIdSet">A Client ID is entered (here or in configuration).</param>
    /// <param name="ClientIdFromConfig">It comes from Twitch:ClientId, not the Admin page.</param>
    /// <param name="Status">NotConnected / Connecting / Watching / NoChannels / Retrying.</param>
    /// <param name="Pending">A sign-in waiting for its code to be typed at Twitch.</param>
    public sealed record AdminTwitchDto(bool ClientIdSet, string? ClientId, bool ClientIdFromConfig, string? Login, DateTime? ConnectedAtUtc, string? Problem,
        string Status, string? StatusDetail, AdminTwitchSignInDto? Pending, IReadOnlyList<LiveChannelDto> Channels);

    public sealed record AdminTwitchSignInDto(string UserCode, string VerificationUri, DateTime ExpiresAtUtc);

    public sealed record SetTwitchClientIdRequest(string? ClientId);

    /// <summary>
    /// Live stream updates (badrfly 2026-11-18): GET /api/live/events is a server-sent event stream that tells an open page when one of
    /// its team's channels starts or stops streaming (from <see cref="LiveStreamHub"/>, fed by Twitch EventSub), so cards and players
    /// update without a refresh or a timer. The Admin page's Twitch tab (/api/admin/twitch, Owner only) connects the Twitch account.
    /// </summary>
    public static class LiveEndpoints
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(25);

        public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/live/events", StreamEvents).RequireTeam(TeamPermission.ViewLibrary);

            var admin = app.MapGroup("/api/admin/twitch");
            admin.MapGet("/", GetTwitch).RequireStaffOwner();
            admin.MapPut("/client-id", SetClientId).RequireStaffOwner();
            admin.MapPost("/connect", Connect).RequireStaffOwner();
            admin.MapDelete("/", Disconnect).RequireStaffOwner();
            return app;
        }

        private static async Task StreamEvents(HttpContext context, TeamContext team, LibraryDbContext db, LiveStreamHub hub,
            CancellationToken cancellationToken)
        {
            var response = context.Response;
            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Accel-Buffering"] = "no";

            // The Owner changing this team's channels (badrfly 2026-11-20: a revoke must reach open pages at once)
            var teamChanged = System.Threading.Channels.Channel.CreateBounded<bool>(
                new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
            void OnTeamChanged(int teamId)
            {
                if (teamId == team.TeamId)
                    teamChanged.Writer.TryWrite(true);
            }

            // Listen before reading the state, so nothing falls between the two
            using var listener = hub.Listen();
            TeamChannelAccess.TeamChannelsChanged += OnTeamChanged;
            try
            {
                var logins = await TeamLoginsAsync(db, cancellationToken);
                var hello = new LiveHelloDto(hub.Watching, logins.Select(login => ToDto(login, hub.Get(login))).ToList());
                await WriteAsync(response, "hello", hello, cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        wait.CancelAfter(Heartbeat);
                        var stream = listener.Reader.WaitToReadAsync(wait.Token).AsTask();
                        var channels = teamChanged.Reader.WaitToReadAsync(wait.Token).AsTask();
                        await Task.WhenAny(stream, channels);
                        // Lets the other wait end (cancelled, never observed as a failure)
                        wait.Cancel();
                    }
                    cancellationToken.ThrowIfCancellationRequested();

                    var wrote = false;
                    if (teamChanged.Reader.TryRead(out _))
                    {
                        var now = await TeamLoginsAsync(db, cancellationToken);
                        await WriteAsync(response, "channels", new LiveChannelsDto(now.Order().ToList()), cancellationToken);
                        wrote = true;
                    }
                    while (listener.Reader.TryRead(out var change))
                    {
                        // Only the team's own channels (re-read: the Owner may have changed them since the page connected)
                        if (!(await TeamLoginsAsync(db, cancellationToken)).Contains(change.Login))
                            continue;
                        await WriteAsync(response, "stream", new LiveStreamDto(change.Login, change.Live, change.StartedAtUtc, change.AtUtc), cancellationToken);
                        wrote = true;
                    }
                    if (!wrote && listener.Reader.Completion.IsCompleted)
                        break;
                    if (!wrote)
                    {
                        // A comment line keeps proxies and the browser from closing a quiet connection (nothing is asked of Twitch)
                        await response.WriteAsync(": ping\n\n", cancellationToken);
                        await response.Body.FlushAsync(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The page closed
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
            }
            finally
            {
                TeamChannelAccess.TeamChannelsChanged -= OnTeamChanged;
            }
        }

        private static async Task<HashSet<string>> TeamLoginsAsync(LibraryDbContext db, CancellationToken cancellationToken) =>
            (await db.Channels.AsNoTracking().Where(x => x.AccessEndedAtUtc == null).Select(x => x.Login).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static LiveChannelDto ToDto(string login, ChannelLiveState? state) =>
            new(login.ToLowerInvariant(), state?.Live, state?.StartedAtUtc, state?.Watched ?? false);

        private static async Task WriteAsync<T>(HttpResponse response, string eventName, T data, CancellationToken cancellationToken)
        {
            await response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        // ---------- Admin: the Twitch account ----------

        private static async Task<AdminTwitchDto> StatusAsync(TwitchUserAuth auth, LiveStreamHub hub, IConfiguration configuration, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var row = await auth.LoadAsync(cancellationToken);
            var clientId = await auth.GetClientIdAsync(cancellationToken);
            var pending = auth.Pending;
            var assigned = await db.Channels.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.AccessEndedAtUtc == null && db.Teams.Any(t => t.Id == x.TeamId && t.DisabledAtUtc == null))
                .Select(x => x.Login).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
            return new AdminTwitchDto(clientId is not null, clientId, string.IsNullOrWhiteSpace(row?.ClientId) && clientId is not null,
                row?.TwitchLogin, row?.ConnectedAtUtc, row?.Problem, hub.Status.ToString(), hub.StatusDetail,
                pending is null ? null : new AdminTwitchSignInDto(pending.UserCode, pending.VerificationUri, pending.ExpiresAtUtc),
                assigned.Select(login => ToDto(login, hub.Get(login))).ToList());
        }

        private static async Task<IResult> GetTwitch(TwitchUserAuth auth, LiveStreamHub hub, IConfiguration configuration, LibraryDbContext db,
            CancellationToken cancellationToken) =>
            Results.Ok(await StatusAsync(auth, hub, configuration, db, cancellationToken));

        private static async Task<IResult> SetClientId(SetTwitchClientIdRequest request, TwitchUserAuth auth, LiveStreamHub hub, IConfiguration configuration,
            LibraryDbContext db, CancellationToken cancellationToken)
        {
            var clientId = request.ClientId?.Trim();
            if (!string.IsNullOrEmpty(clientId) && (clientId.Length > 100 || !clientId.All(char.IsAsciiLetterOrDigit)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["clientId"] = ["A Twitch Client ID is letters and digits only."] });
            await auth.SetClientIdAsync(clientId, cancellationToken);
            return Results.Ok(await StatusAsync(auth, hub, configuration, db, cancellationToken));
        }

        private static async Task<IResult> Connect(StaffContext me, TwitchUserAuth auth, LiveStreamHub hub, IConfiguration configuration, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            try
            {
                await auth.StartSignInAsync(me.Id, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (TwitchAuthException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(detail: "Couldn't reach Twitch.", statusCode: StatusCodes.Status502BadGateway);
            }
            return Results.Ok(await StatusAsync(auth, hub, configuration, db, cancellationToken));
        }

        private static async Task<IResult> Disconnect(StaffContext me, TwitchUserAuth auth, LiveStreamHub hub, IConfiguration configuration, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            await auth.DisconnectAsync(me.Id, cancellationToken);
            return Results.Ok(await StatusAsync(auth, hub, configuration, db, cancellationToken));
        }
    }
}
