using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Deletes on-demand files once they pass their expiry, and sweeps up library files that no entry points to
    /// (e.g. a delete that couldn't remove a file because it was open at the time).
    /// </summary>
    public sealed class LibraryCleanupService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

        // Leave recent files alone so we never race a job that is still writing one
        private static readonly TimeSpan OrphanMinAge = TimeSpan.FromMinutes(10);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DownloadQueue _queue;
        private readonly LibraryStorage _storage;
        private readonly ShortformService _shortform;
        private readonly ILogger<LibraryCleanupService> _logger;

        public LibraryCleanupService(IServiceScopeFactory scopeFactory, DownloadQueue queue, LibraryStorage storage, ShortformService shortform,
            ILogger<LibraryCleanupService> logger)
        {
            _shortform = shortform;
            _scopeFactory = scopeFactory;
            _queue = queue;
            _storage = storage;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Library cleanup failed.");
                }
            } while (await WaitForNextTickAsync(timer, stoppingToken));
        }

        private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
        {
            try
            {
                return await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

            await DeleteExpiredGeneratedFilesAsync(db, cancellationToken);
            await DeleteOrphanedFilesAsync(db, cancellationToken);

            var entryIds = (await db.Entries.Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
            _shortform.Cleanup(entryIds);
        }

        private async Task DeleteExpiredGeneratedFilesAsync(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var expired = await db.Entries
                .Where(x => x.GenerationStatus == GenerationStatus.Ready && x.GeneratedExpiresAtUtc <= now)
                .ToListAsync(cancellationToken);

            foreach (var entry in expired)
            {
                // If the file is open (e.g. still being downloaded by a user), try again next round
                if (!_storage.TryDelete(entry.GeneratedFilePath))
                    continue;

                entry.GenerationStatus = GenerationStatus.None;
                entry.GeneratedFilePath = null;
                entry.GeneratedFileSizeBytes = null;
                entry.GeneratedExpiresAtUtc = null;
                entry.UpdatedAtUtc = now;
                db.Audit(AuditActions.GeneratedFileExpired, entry.Id);
            }

            await db.SaveChangesAsync(cancellationToken);

            if (expired.Count > 0)
                _logger.LogInformation("Expired {Count} on-demand file(s).", expired.Count);
        }

        private async Task DeleteOrphanedFilesAsync(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var referenced = (await db.Entries
                    .Where(x => x.FilePath != null || x.GeneratedFilePath != null)
                    .Select(x => new { x.FilePath, x.GeneratedFilePath })
                    .ToListAsync(cancellationToken))
                .SelectMany(x => new[] { x.FilePath, x.GeneratedFilePath })
                .OfType<string>()
                .Select(_storage.Resolve)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Only look at files we name ourselves ("{entryId}.mp4") inside our two folders
            foreach (var folder in new[] { _storage.StoredFolder, _storage.GeneratedFolder })
            {
                if (!Directory.Exists(folder))
                    continue;

                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.mp4"))
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file.Name), out var entryId)
                        || referenced.Contains(file.FullName)
                        || _queue.IsRunning(entryId)
                        || DateTime.UtcNow - file.LastWriteTimeUtc < OrphanMinAge)
                    {
                        continue;
                    }

                    if (_storage.TryDelete(file.FullName))
                        _logger.LogInformation("Deleted orphaned file {Path}.", file.FullName);
                }
            }
        }
    }
}
