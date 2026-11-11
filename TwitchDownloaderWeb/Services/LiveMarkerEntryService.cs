using Microsoft.EntityFrameworkCore;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services
{
    /// <param name="EntryId">The library entry made or moved.</param>
    /// <param name="Created">True when this change made it.</param>
    public sealed record LiveEntryChange(int EntryId, bool Created);

    /// <summary>
    /// Markers and segments added on a VOD that is still live make one reference-only library entry for the team ("live markers
    /// entry", <see cref="LibraryEntry.FollowsMarkers"/>), from 30 s before the first marker to 30 s after the last one. The end is
    /// capped at what has been streamed so far (but always covers the markers). Each later marker change on that VOD moves it,
    /// even after the stream has ended, until someone re-trims it by hand.
    /// </summary>
    public sealed class LiveMarkerEntryService(IVodDetailsSource vods, DownloadQueue queue, LibraryStorage storage, ILogger<LiveMarkerEntryService> logger)
    {
        public const double MarginSeconds = 30;

        private static readonly TimeSpan JobStopWait = TimeSpan.FromSeconds(20);

        // Two markers saved at the same moment must not both make an entry
        private readonly SemaphoreSlim _lock = new(1, 1);

        /// <summary>Call after the team's markers on a VOD were saved. Never throws for Twitch being unreachable.</summary>
        /// <param name="mayCreate">False when the markers were changed in a library card's Review: the change is about that card, so no
        /// live markers entry is made (badrfly, 2026-11-11: segments made in a clip's Review made a second, longer card). One that exists still moves.</param>
        /// <returns>The entry made or moved, or null if nothing changed.</returns>
        public async Task<LiveEntryChange?> MarkersChangedAsync(LibraryDbContext db, long vodId, CancellationToken cancellationToken, bool mayCreate = true)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return await UpdateAsync(db, vodId, mayCreate, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>The entry's range for these markers: [first - 30 s, last + 30 s], the end capped at the VOD's length but never before the last marker.</summary>
        public static (TimeSpan Start, TimeSpan End) RangeFor(IReadOnlyCollection<(double Seconds, double? EndSeconds)> markers, int vodLengthSeconds)
        {
            var first = markers.Min(x => x.Seconds);
            var last = markers.Max(x => x.EndSeconds ?? x.Seconds);
            var start = Math.Max(0, first - MarginSeconds);
            var end = Math.Max(last, Math.Min(last + MarginSeconds, vodLengthSeconds));
            if (end - start < 1)
                end = start + 1; // a lone marker at 0:00 on a VOD that just started
            return (TimeSpan.FromSeconds(Math.Round(start, 2)), TimeSpan.FromSeconds(Math.Round(end, 2)));
        }

        private async Task<LiveEntryChange?> UpdateAsync(LibraryDbContext db, long vodId, bool mayCreate, CancellationToken cancellationToken)
        {
            // Query filter: only the requesting team's entries and markers. One live markers entry per VOD: once it was re-cut by
            // hand (no longer following) the markers don't make a new one, which looked like the card duplicating itself.
            var entry = await db.Entries
                .Where(x => x.VodId == vodId && (x.MadeFromMarkers || x.FollowsMarkers) && x.Kind == EntryKind.Vod)
                .OrderByDescending(x => x.FollowsMarkers)
                .ThenBy(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (entry is { Status: EntryStatus.VodUnavailable } or { FollowsMarkers: false } || (entry is null && !mayCreate))
                return null;

            var markers = (await db.Markers.AsNoTracking()
                    .Where(x => x.VodId == vodId)
                    .Select(x => new { x.Seconds, x.EndSeconds })
                    .ToListAsync(cancellationToken))
                .Select(x => (x.Seconds, x.EndSeconds))
                .ToList();
            if (markers.Count == 0)
                return null; // the last marker was deleted: keep the entry as it is (entries are never deleted automatically)

            VodDetails? vod;
            try
            {
                vod = await vods.GetVodAsync(vodId);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Couldn't look up VOD {VodId} for its live markers entry: {Message}", vodId, ex.Message);
                return null;
            }

            if (vod is null || (entry is null && !vod.IsLive))
                return null;

            var (start, end) = RangeFor(markers, vod.LengthSeconds);
            var vodLength = Math.Max(vod.LengthSeconds, (int)Math.Ceiling(end.TotalSeconds));
            var now = DateTime.UtcNow;

            if (entry is null)
            {
                entry = new LibraryEntry
                {
                    Title = vod.Title,
                    VodId = vodId,
                    ChannelName = vod.ChannelName,
                    TrimMode = VideoTrimMode.Exact,
                    StartTime = start,
                    EndTime = end,
                    ClipLengthSeconds = (end - start).TotalSeconds,
                    VodLengthSeconds = vodLength,
                    StreamDateUtc = vod.StreamDateUtc,
                    AddedAtUtc = now,
                    UpdatedAtUtc = now,
                    ThumbnailUrl = vod.ThumbnailUrl,
                    // Always a reference, whatever its length: it moves with every marker, so a stored file would be re-downloaded each time
                    Status = EntryStatus.ReferenceOnly,
                    FollowsMarkers = true,
                    MadeFromMarkers = true,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                db.Entries.Add(entry);
                await db.SaveChangesAsync(cancellationToken);
                db.Audit(AuditActions.EntryAdded, entry.Id,
                    $"VOD {vodId}, {TrimTime.Format(start)}-{TrimTime.Format(end)}, {(end - start).TotalSeconds:0.###}s, reference-only, from markers on the live stream");
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new LiveEntryChange(entry.Id, true);
            }

            if (entry.StartTime == start && entry.EndTime == end)
            {
                if (entry.VodLengthSeconds < vodLength)
                {
                    entry.VodLengthSeconds = vodLength;
                    await db.SaveChangesAsync(cancellationToken);
                }
                return null;
            }

            // A file being made for the old range would be wrong: stop it (as a re-trim does)
            if (queue.IsRunning(entry.Id))
            {
                queue.Cancel(entry.Id);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (queue.IsRunning(entry.Id))
                {
                    if (waited.Elapsed > JobStopWait)
                    {
                        logger.LogWarning("Entry {EntryId}'s file job didn't stop; its range follows the markers next time", entry.Id);
                        return null;
                    }
                    await Task.Delay(100, cancellationToken);
                }
                await db.Entry(entry).ReloadAsync(cancellationToken);
            }

            var oldRange = $"{TrimTime.Format(entry.StartTime)}-{TrimTime.Format(entry.EndTime) ?? "end"}";
            storage.TryDelete(entry.GeneratedFilePath);
            entry.StartTime = start;
            entry.EndTime = end;
            entry.ClipLengthSeconds = (end - start).TotalSeconds;
            entry.VodLengthSeconds = Math.Max(entry.VodLengthSeconds, vodLength);
            entry.Error = null;
            entry.GenerationStatus = GenerationStatus.None; // a queued job skips the entry
            entry.GeneratedFilePath = null;
            entry.GeneratedFileSizeBytes = null;
            entry.GeneratedExpiresAtUtc = null;
            entry.AudioRestoredSeconds = null;
            entry.AudioMutedSeconds = null;
            entry.UpdatedAtUtc = now;
            db.Audit(AuditActions.EntryFollowedMarkers, entry.Id,
                $"{oldRange} -> {TrimTime.Format(start)}-{TrimTime.Format(end)}, {(end - start).TotalSeconds:0.###}s");
            await db.SaveChangesAsync(cancellationToken);
            return new LiveEntryChange(entry.Id, false);
        }
    }
}
