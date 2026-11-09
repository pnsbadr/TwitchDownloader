namespace TwitchDownloaderWeb.Data
{
    public static class AuditExtensions
    {
        /// <summary>
        /// Adds an audit event to the context; it is written with the caller's next SaveChanges, so it commits together
        /// with the change it describes. Never pass OAuth, session or link tokens (or passwords) in <paramref name="details"/>.
        /// User, team and staff are filled from the request (<see cref="Services.Accounts.RequestActor"/>) when not given.
        /// </summary>
        public static void Audit(this LibraryDbContext db, string action, int? entryId, string? details = null, string? userId = null, int? teamId = null, int? staffId = null)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Action = action,
                EntryId = entryId,
                UserId = userId,
                TeamId = teamId,
                StaffId = staffId,
                Details = details is { Length: > 2000 } ? details[..2000] : details,
            });
        }
    }
}
