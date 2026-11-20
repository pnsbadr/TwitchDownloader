using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services
{
    /// <param name="EntryId">The library entry made or moved.</param>
    /// <param name="Created">True when this change made it.</param>
    public sealed record LiveEntryChange(int EntryId, bool Created);

    /// <summary>
    /// Used to make one reference-only entry ("live markers entry") from the markers a team added on a VOD that was still live, and
    /// move it with every later marker change. Removed (badrfly 2026-11-20): segments mark ranges and the clip tool makes clips on
    /// purpose, so markers no longer make or move any entry. Entries it made before stay as they are (<see cref="LibraryEntry.FollowsMarkers"/>
    /// and <see cref="LibraryEntry.MadeFromMarkers"/> are kept but no longer act). The calls stay so the marker endpoints keep their shape
    /// (<c>liveEntry</c> is always null).
    /// </summary>
    public sealed class LiveMarkerEntryService
    {
        /// <summary>Called after the team's markers on a VOD were saved. Makes and moves nothing now.</summary>
        public Task<LiveEntryChange?> MarkersChangedAsync(LibraryDbContext db, long vodId, CancellationToken cancellationToken, bool mayCreate = true)
            => Task.FromResult<LiveEntryChange?>(null);
    }
}
