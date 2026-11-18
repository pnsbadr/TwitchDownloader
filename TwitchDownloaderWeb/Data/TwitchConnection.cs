namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// The Twitch account the app signs in with to hear when the teams' channels start and stop streaming (EventSub, badrfly 2026-11-18).
    /// One row (Id 1), set on the Admin page's Twitch tab by the app Owner. Twitch only sends these events to a registered Twitch app
    /// (<see cref="ClientId"/>) with a user's sign-in; the account needs no permissions and is never the broadcaster.
    /// The refresh token is kept encrypted with ASP.NET Core data protection and is never logged or audited.
    /// </summary>
    public class TwitchConnection
    {
        public int Id { get; set; }

        /// <summary>The registered Twitch app's Client ID (Public client type, not a secret). Null = use Twitch:ClientId from configuration.</summary>
        public string? ClientId { get; set; }

        /// <summary>The signed-in Twitch account. Null while not connected.</summary>
        public string? TwitchUserId { get; set; }
        public string? TwitchLogin { get; set; }

        /// <summary>The refresh token, encrypted (<see cref="Microsoft.AspNetCore.DataProtection.IDataProtector"/>). Never log it.</summary>
        public string? RefreshTokenProtected { get; set; }

        public DateTime? ConnectedAtUtc { get; set; }
        public int? ConnectedByStaffId { get; set; }

        /// <summary>Why the sign-in stopped working (Twitch refused the refresh token, the app was deleted, …). Null = fine.</summary>
        public string? Problem { get; set; }
    }
}
