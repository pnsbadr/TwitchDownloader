using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <param name="Counts">Actions in the period, by kind (see <see cref="ActivityReport.KindNames"/>).</param>
    /// <param name="EntriesAdded">Entries they added that still exist (any time).</param>
    /// <param name="DiskBytes">Files of those entries on disk now: stored, on-demand (unexpired) and vertical renders.</param>
    public sealed record UserActivity(IReadOnlyDictionary<string, int> Counts, int EntriesAdded, long DiskBytes)
    {
        public static UserActivity Empty { get; } = new(ActivityReport.KindNames.ToDictionary(x => x, _ => 0), 0, 0);
    }

    /// <summary>
    /// What each user did per team (audited actions in a period), how many entries they added and the disk their entries' files use now.
    /// Shared by the Team page (one team) and the Admin page (every team, or one user across teams). Only team users' actions count;
    /// staff (support visits) never do.
    /// </summary>
    public static class ActivityReport
    {
        private static readonly Dictionary<string, string> Kinds = new()
        {
            [AuditActions.EntryAdded] = "added",
            [AuditActions.EntryRenamed] = "edited",
            [AuditActions.EntryRetrimmed] = "edited",
            [AuditActions.EntryDeleted] = "deleted",
            [AuditActions.FileServed] = "downloaded",
            [AuditActions.GenerationRequested] = "generated",
            [AuditActions.SegmentsRequested] = "generated",
            [AuditActions.ExportRequested] = "generated",
            [AuditActions.ShortformRequested] = "vertical",
        };

        public static readonly IReadOnlyList<string> KindNames = Kinds.Values.Distinct().ToList();

        /// <param name="teamId">One team, or null for every team.</param>
        /// <param name="userId">One user, or null for everyone.</param>
        /// <returns>By (team, user ID as text); users with nothing are missing (use <see cref="UserActivity.Empty"/>).</returns>
        public static async Task<Dictionary<(int TeamId, string UserId), UserActivity>> ComputeAsync(LibraryDbContext db, ShortformService shortform,
            DateTime sinceUtc, int? teamId, int? userId, CancellationToken cancellationToken)
        {
            var userText = userId?.ToString();
            var actions = Kinds.Keys.ToList();
            var events = db.AuditEvents.AsNoTracking()
                .Where(x => x.UserId != null && x.TeamId != null && x.StaffId == null && x.TimestampUtc >= sinceUtc && actions.Contains(x.Action));
            if (teamId is { } t)
                events = events.Where(x => x.TeamId == t);
            if (userText is not null)
                events = events.Where(x => x.UserId == userText);
            var counts = await events
                .GroupBy(x => new { x.TeamId, x.UserId, x.Action })
                .Select(x => new { TeamId = x.Key.TeamId!.Value, UserId = x.Key.UserId!, x.Key.Action, Count = x.Count() })
                .ToListAsync(cancellationToken);

            var entryQuery = db.Entries.IgnoreQueryFilters().AsNoTracking().Where(x => x.AddedByUserId != null);
            if (teamId is { } et)
                entryQuery = entryQuery.Where(x => x.TeamId == et);
            if (userText is not null)
                entryQuery = entryQuery.Where(x => x.AddedByUserId == userText);
            var entries = await entryQuery
                .Select(x => new { x.Id, x.TeamId, AddedByUserId = x.AddedByUserId!, x.FileSizeBytes, x.GeneratedFileSizeBytes, x.GeneratedExpiresAtUtc })
                .ToListAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var builders = new Dictionary<(int, string), (Dictionary<string, int> Counts, int Added, long Disk)>();
            (Dictionary<string, int> Counts, int Added, long Disk) Get((int, string) key) =>
                builders.TryGetValue(key, out var b) ? b : (KindNames.ToDictionary(x => x, _ => 0), 0, 0L);

            foreach (var c in counts)
            {
                var key = (c.TeamId, c.UserId);
                var b = Get(key);
                b.Counts[Kinds[c.Action]] += c.Count;
                builders[key] = b;
            }

            foreach (var entry in entries)
            {
                var key = (entry.TeamId, entry.AddedByUserId);
                var b = Get(key);
                var bytes = (entry.FileSizeBytes ?? 0)
                            + (entry.GeneratedExpiresAtUtc > now ? entry.GeneratedFileSizeBytes ?? 0 : 0)
                            + shortform.ListRenders(entry.Id).Sum(x => x.SizeBytes);
                builders[key] = (b.Counts, b.Added + 1, b.Disk + bytes);
            }

            // Segment downloads' files count for whoever started them
            foreach (var export in await SegmentExportDiskAsync(db, teamId, userId, cancellationToken))
            {
                var key = (export.TeamId, export.UserId.ToString());
                var b = Get(key);
                builders[key] = (b.Counts, b.Added, b.Disk + export.Bytes);
            }

            return builders.ToDictionary(x => x.Key, x => new UserActivity(x.Value.Counts, x.Value.Added, x.Value.Disk));
        }

        /// <summary>Disk used now by each team's entries (any adder): stored, unexpired on-demand files and vertical renders.</summary>
        public static async Task<Dictionary<int, long>> TeamDiskAsync(LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var entries = await db.Entries.IgnoreQueryFilters().AsNoTracking()
                .Select(x => new { x.Id, x.TeamId, x.FileSizeBytes, x.GeneratedFileSizeBytes, x.GeneratedExpiresAtUtc })
                .ToListAsync(cancellationToken);
            var disk = new Dictionary<int, long>();
            foreach (var entry in entries)
            {
                disk[entry.TeamId] = disk.GetValueOrDefault(entry.TeamId)
                                     + (entry.FileSizeBytes ?? 0)
                                     + (entry.GeneratedExpiresAtUtc > now ? entry.GeneratedFileSizeBytes ?? 0 : 0)
                                     + shortform.ListRenders(entry.Id).Sum(x => x.SizeBytes);
            }
            foreach (var export in await SegmentExportDiskAsync(db, null, null, cancellationToken))
                disk[export.TeamId] = disk.GetValueOrDefault(export.TeamId) + export.Bytes;
            return disk;
        }

        /// <summary>Bytes of each unexpired segment download's files (every team's: staff code).</summary>
        private static async Task<List<(int TeamId, int UserId, long Bytes)>> SegmentExportDiskAsync(LibraryDbContext db, int? teamId, int? userId,
            CancellationToken cancellationToken) =>
            [.. await SegmentDownloadDiskAsync(db, teamId, userId, cancellationToken), .. await ExportDiskAsync(db, teamId, userId, cancellationToken)];

        /// <summary>Bytes of each unexpired export's files (every team's: staff code).</summary>
        private static async Task<List<(int TeamId, int UserId, long Bytes)>> ExportDiskAsync(LibraryDbContext db, int? teamId, int? userId,
            CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var query = db.ExportBatches.IgnoreQueryFilters().AsNoTracking().Where(x => x.ExpiresAtUtc == null || x.ExpiresAtUtc > now);
            if (teamId is { } t)
                query = query.Where(x => x.TeamId == t);
            if (userId is { } u)
                query = query.Where(x => x.CreatedByUserId == u);
            var rows = await query.Select(x => new { x.TeamId, x.CreatedByUserId, Bytes = x.Items.Sum(i => i.FileSizeBytes ?? 0) }).ToListAsync(cancellationToken);
            return rows.Select(x => (x.TeamId, x.CreatedByUserId, x.Bytes)).ToList();
        }

        private static async Task<List<(int TeamId, int UserId, long Bytes)>> SegmentDownloadDiskAsync(LibraryDbContext db, int? teamId, int? userId,
            CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var query = db.SegmentExports.IgnoreQueryFilters().AsNoTracking().Where(x => x.ExpiresAtUtc == null || x.ExpiresAtUtc > now);
            if (teamId is { } t)
                query = query.Where(x => x.TeamId == t);
            if (userId is { } u)
                query = query.Where(x => x.CreatedByUserId == u);
            var rows = await query
                .Select(x => new { x.TeamId, x.CreatedByUserId, Combined = x.CombinedFileSizeBytes ?? 0, Items = x.Items.Sum(i => i.FileSizeBytes ?? 0) })
                .ToListAsync(cancellationToken);
            return rows.Select(x => (x.TeamId, x.CreatedByUserId, x.Combined + x.Items)).ToList();
        }
    }
}
