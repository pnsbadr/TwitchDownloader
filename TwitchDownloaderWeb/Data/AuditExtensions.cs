namespace TwitchDownloaderWeb.Data
{
    public static class AuditExtensions
    {
        /// <summary>
        /// Adds an audit event to the context; it is written with the caller's next SaveChanges, so it commits together
        /// with the change it describes. Never pass OAuth or session tokens in <paramref name="details"/>.
        /// </summary>
        public static void Audit(this LibraryDbContext db, string action, int? entryId, string? details = null, string? userId = null)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Action = action,
                EntryId = entryId,
                UserId = userId,
                Details = details is { Length: > 2000 } ? details[..2000] : details,
            });
        }
    }
}
