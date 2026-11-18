using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Live
{
    /// <summary>A device code sign-in waiting for the Owner to type <see cref="UserCode"/> at <see cref="VerificationUri"/>.</summary>
    public sealed record TwitchDeviceSignIn(string UserCode, string VerificationUri, DateTime ExpiresAtUtc);

    /// <summary>
    /// The Twitch sign-in that EventSub needs (WebSocket subscriptions only take a user access token). The app Owner connects a Twitch
    /// account with the device code flow (Admin page → Twitch: a short code typed at twitch.tv/activate; no redirect URL, works the same
    /// in the browser app and a desktop wrapper). The registered app is a Public client, so there is no client secret: access tokens last
    /// a few hours and are refreshed with the (single-use, 30 days unused) refresh token, which is stored encrypted in
    /// <see cref="TwitchConnection"/>. Tokens stay in memory and that row only: never in logs, audit details or exception messages.
    /// </summary>
    public sealed class TwitchUserAuth
    {
        public const string HttpClientName = "TwitchAuth";
        private const string IdBase = "https://id.twitch.tv/oauth2";
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IDataProtector _protector;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TwitchUserAuth> _logger;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        private string? _accessToken;
        private DateTime _accessExpiresUtc;
        private TwitchDeviceSignIn? _pending;
        private CancellationTokenSource? _pendingCancel;

        public TwitchUserAuth(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, IDataProtectionProvider dataProtection,
            IConfiguration configuration, ILogger<TwitchUserAuth> logger)
        {
            _scopeFactory = scopeFactory;
            _httpClientFactory = httpClientFactory;
            _protector = dataProtection.CreateProtector("Clipco.TwitchConnection.RefreshToken");
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>Fired when the sign-in or Client ID changes (connected, disconnected, broken), so EventSub reconnects.</summary>
        public event Action? Changed;

        public TwitchDeviceSignIn? Pending => _pending is { } p && p.ExpiresAtUtc > DateTime.UtcNow ? p : null;

        /// <summary>The Client ID to use: the Admin page's, else Twitch:ClientId from configuration.</summary>
        public async Task<string?> GetClientIdAsync(CancellationToken cancellationToken = default)
        {
            var row = await LoadAsync(cancellationToken);
            return ClientIdOf(row);
        }

        private string? ClientIdOf(TwitchConnection? row)
        {
            var id = string.IsNullOrWhiteSpace(row?.ClientId) ? _configuration["Twitch:ClientId"] : row.ClientId;
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }

        public async Task<TwitchConnection?> LoadAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            return await db.TwitchConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
        }

        /// <summary>Whether a Client ID and a working sign-in are there (EventSub can run).</summary>
        public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        {
            var row = await LoadAsync(cancellationToken);
            return ClientIdOf(row) is not null && row?.RefreshTokenProtected is not null && row.Problem is null;
        }

        /// <summary>Sets the Client ID (blank = configuration's). A different app can't use the old sign-in, so it is dropped.</summary>
        public async Task SetClientIdAsync(string? clientId, CancellationToken cancellationToken)
        {
            clientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var row = await GetOrAddAsync(db, cancellationToken);
                if (row.ClientId == clientId)
                    return;
                row.ClientId = clientId;
                ClearSignIn(row);
                await db.SaveChangesAsync(cancellationToken);
            }
            ForgetAccessToken();
            CancelPending();
            Changed?.Invoke();
        }

        /// <summary>
        /// Starts a device code sign-in and keeps asking Twitch in the background until the code is used or expires
        /// (<see cref="Pending"/> while waiting).
        /// </summary>
        public async Task<TwitchDeviceSignIn> StartSignInAsync(int staffId, CancellationToken cancellationToken)
        {
            var clientId = await GetClientIdAsync(cancellationToken) ?? throw new InvalidOperationException("Enter the Twitch app's Client ID first.");
            var scopes = _configuration["Twitch:Scopes"] ?? "";

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync($"{IdBase}/device", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"] = scopes,
            }), cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new TwitchAuthException(await DescribeFailureAsync(response, "start the sign-in", cancellationToken));

            var start = await response.Content.ReadFromJsonAsync<DeviceStartResponse>(cancellationToken)
                ?? throw new TwitchAuthException("Twitch sent an empty answer.");

            CancelPending();
            var signIn = new TwitchDeviceSignIn(start.UserCode, start.VerificationUri, DateTime.UtcNow.AddSeconds(Math.Max(60, start.ExpiresIn)));
            var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(60, start.ExpiresIn)));
            _pending = signIn;
            _pendingCancel = cancel;
            _ = Task.Run(() => WaitForSignInAsync(clientId, scopes, start.DeviceCode, Math.Max(1, start.Interval), staffId, signIn, cancel.Token));
            return signIn;
        }

        public void CancelPending()
        {
            _pendingCancel?.Cancel();
            _pendingCancel = null;
            _pending = null;
        }

        private async Task WaitForSignInAsync(string clientId, string scopes, string deviceCode, int intervalSeconds, int staffId,
            TwitchDeviceSignIn signIn, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
                    using var response = await client.PostAsync($"{IdBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["scopes"] = scopes,
                        ["device_code"] = deviceCode,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    }), cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
                        if (tokens?.AccessToken is null || tokens.RefreshToken is null)
                            throw new TwitchAuthException("Twitch sent no tokens.");
                        await CompleteSignInAsync(clientId, tokens, staffId, cancellationToken);
                        return;
                    }

                    var message = await ReadMessageAsync(response, cancellationToken);
                    if (message is "authorization_pending" or "slow_down")
                    {
                        if (message == "slow_down")
                            intervalSeconds += 5;
                        continue;
                    }

                    _logger.LogWarning("Twitch sign-in ended: {Status} {Message}", (int)response.StatusCode, message);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Expired, replaced by a new sign-in or cancelled
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Twitch sign-in failed: {Message}", ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_pending, signIn))
                    _pending = null;
            }
        }

        private async Task CompleteSignInAsync(string clientId, TokenResponse tokens, int staffId, CancellationToken cancellationToken)
        {
            var who = await ValidateAsync(tokens.AccessToken!, cancellationToken)
                ?? throw new TwitchAuthException("Twitch didn't accept the new sign-in.");

            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var row = await GetOrAddAsync(db, cancellationToken);
                if (ClientIdOf(row) != clientId)
                    return; // the Client ID changed while waiting
                row.TwitchUserId = who.UserId;
                row.TwitchLogin = who.Login;
                row.RefreshTokenProtected = _protector.Protect(tokens.RefreshToken!);
                row.ConnectedAtUtc = DateTime.UtcNow;
                row.ConnectedByStaffId = staffId;
                row.Problem = null;
                db.Audit(AuditActions.TwitchConnected, null, $"Twitch account {who.Login} connected for live stream updates", staffId: staffId);
                await db.SaveChangesAsync(cancellationToken);
            }

            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                _accessToken = tokens.AccessToken;
                _accessExpiresUtc = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
            }
            finally
            {
                _refreshLock.Release();
            }

            _logger.LogInformation("Twitch account {Login} connected for live stream updates.", who.Login);
            Changed?.Invoke();
        }

        /// <summary>Signs out: revokes the token at Twitch (best effort) and forgets it.</summary>
        public async Task DisconnectAsync(int? staffId, CancellationToken cancellationToken)
        {
            CancelPending();
            string? clientId;
            string? login;
            string? refresh = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var row = await db.TwitchConnections.FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
                if (row is null)
                    return;
                clientId = ClientIdOf(row);
                login = row.TwitchLogin;
                if (row.RefreshTokenProtected is { } protectedToken)
                    refresh = Unprotect(protectedToken);
                ClearSignIn(row);
                if (login is not null)
                    db.Audit(AuditActions.TwitchDisconnected, null, $"Twitch account {login} disconnected", staffId: staffId);
                await db.SaveChangesAsync(cancellationToken);
            }

            var access = _accessToken;
            ForgetAccessToken();
            Changed?.Invoke();

            if (clientId is null)
                return;
            var client = _httpClientFactory.CreateClient(HttpClientName);
            foreach (var token in new[] { access, refresh }.OfType<string>())
            {
                try
                {
                    using var _ = await client.PostAsync($"{IdBase}/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["token"] = token,
                    }), cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Not revoking only means the token lives until it expires; it is forgotten here either way
                }
            }
        }

        /// <summary>A user access token for Helix / EventSub, refreshed when it's about to expire.</summary>
        /// <param name="forceRefresh">Twitch refused the current one (401).</param>
        /// <returns>The token, or null when there is no working sign-in.</returns>
        public async Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                if (!forceRefresh && _accessToken is not null && _accessExpiresUtc - RefreshMargin > DateTime.UtcNow)
                    return _accessToken;

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var row = await db.TwitchConnections.FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
                var clientId = ClientIdOf(row);
                if (row?.RefreshTokenProtected is null || row.Problem is not null || clientId is null)
                    return null;

                var refresh = Unprotect(row.RefreshTokenProtected);
                if (refresh is null)
                {
                    await MarkBrokenAsync(db, row, "The saved Twitch sign-in can't be read on this computer. Connect Twitch again.", cancellationToken);
                    return null;
                }

                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.PostAsync($"{IdBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refresh,
                }), cancellationToken);

                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                {
                    var message = await ReadMessageAsync(response, cancellationToken);
                    _logger.LogWarning("Twitch refused to refresh the sign-in: {Message}", message);
                    await MarkBrokenAsync(db, row, "Twitch ended the sign-in (it was unused for 30 days, revoked, or the app changed). Connect Twitch again.",
                        cancellationToken);
                    return null;
                }
                response.EnsureSuccessStatusCode();

                var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
                if (tokens?.AccessToken is null)
                    throw new TwitchAuthException("Twitch sent no access token.");

                // Public clients' refresh tokens are single-use: keep the new one
                if (tokens.RefreshToken is { } newRefresh && newRefresh != refresh)
                {
                    row.RefreshTokenProtected = _protector.Protect(newRefresh);
                    await db.SaveChangesAsync(cancellationToken);
                }

                _accessToken = tokens.AccessToken;
                _accessExpiresUtc = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
                return _accessToken;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task MarkBrokenAsync(LibraryDbContext db, TwitchConnection row, string problem, CancellationToken cancellationToken)
        {
            row.Problem = problem;
            row.RefreshTokenProtected = null;
            await db.SaveChangesAsync(cancellationToken);
            _accessToken = null;
            Changed?.Invoke();
        }

        private void ForgetAccessToken()
        {
            _accessToken = null;
            _accessExpiresUtc = DateTime.MinValue;
        }

        private string? Unprotect(string protectedToken)
        {
            try
            {
                return _protector.Unprotect(protectedToken);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return null;
            }
        }

        private static void ClearSignIn(TwitchConnection row)
        {
            row.TwitchUserId = null;
            row.TwitchLogin = null;
            row.RefreshTokenProtected = null;
            row.ConnectedAtUtc = null;
            row.ConnectedByStaffId = null;
            row.Problem = null;
        }

        private static async Task<TwitchConnection> GetOrAddAsync(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var row = await db.TwitchConnections.FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
            if (row is null)
            {
                row = new TwitchConnection { Id = 1 };
                db.TwitchConnections.Add(row);
            }
            return row;
        }

        private sealed record ValidateResponse(
            [property: JsonPropertyName("login")] string Login,
            [property: JsonPropertyName("user_id")] string UserId);

        private async Task<ValidateResponse?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{IdBase}/validate");
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ValidateResponse>(cancellationToken) : null;
        }

        // Twitch's error bodies are {"status":400,"message":"..."}; they never echo tokens
        private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return json.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            var message = await ReadMessageAsync(response, cancellationToken);
            return message switch
            {
                "invalid client" or "invalid client_id" => "Twitch doesn't know that Client ID. Copy it again from dev.twitch.tv.",
                _ => $"Twitch wouldn't {what} ({(int)response.StatusCode}{(message is null ? "" : $": {message}")}).",
            };
        }

        private sealed record DeviceStartResponse(
            [property: JsonPropertyName("device_code")] string DeviceCode,
            [property: JsonPropertyName("user_code")] string UserCode,
            [property: JsonPropertyName("verification_uri")] string VerificationUri,
            [property: JsonPropertyName("expires_in")] int ExpiresIn,
            [property: JsonPropertyName("interval")] int Interval);

        private sealed record TokenResponse(
            [property: JsonPropertyName("access_token")] string? AccessToken,
            [property: JsonPropertyName("refresh_token")] string? RefreshToken,
            [property: JsonPropertyName("expires_in")] int ExpiresIn);
    }

    /// <summary>A Twitch sign-in problem with a message fit for the Admin page (never carries a token).</summary>
    public sealed class TwitchAuthException(string message) : Exception(message);
}
