namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// A channel the app Owner took from a team with "Revoke now" (badrfly, 2026-11-15): the team's work on its VODs is hidden
    /// (<see cref="HiddenVods"/>, kept in the database) until the channel is assigned to the team again, which deletes this row.
    /// </summary>
    public class RevokedChannel
    {
        public int Id { get; set; }
        public int TeamId { get; set; }

        /// <summary>Twitch login, lower-case.</summary>
        public required string Login { get; set; }
        public required string DisplayName { get; set; }
        public string? TwitchUserId { get; set; }

        public DateTime RevokedAtUtc { get; set; }

        public List<HiddenVod> HiddenVods { get; set; } = [];
    }

    /// <summary>
    /// A VOD of a <see cref="RevokedChannel"/> the team had work on when it was revoked. The team query filters leave out the team's
    /// entries, markers, rows, videos and exports on it (<see cref="LibraryDbContext"/>).
    /// </summary>
    public class HiddenVod
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public long VodId { get; set; }
        public int RevokedChannelId { get; set; }
    }
}
