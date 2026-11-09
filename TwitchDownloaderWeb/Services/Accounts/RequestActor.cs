namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <summary>
    /// Who the current request acts as (scoped). <see cref="Data.LibraryDbContext"/> uses it to show a request only its team's
    /// entries and channels, and to fill audit events. Background jobs get a default instance (<see cref="IsRequest"/> false)
    /// and see everything.
    /// </summary>
    public sealed class RequestActor
    {
        /// <summary>Set for every HTTP request before anything else runs, so a request whose team couldn't be resolved sees nothing.</summary>
        public bool IsRequest { get; set; }

        public int? UserId { get; set; }
        public int? TeamId { get; set; }
        public int? StaffId { get; set; }
    }
}
