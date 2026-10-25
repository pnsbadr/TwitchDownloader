using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Runs queued downloads outside of HTTP requests: stored entries go to "library/", on-demand files for
    /// reference-only entries go to "generated/" and expire after Downloads:GeneratedFileHours.
    /// </summary>
    public sealed class DownloadWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DownloadQueue _queue;
        private readonly VodInfoService _vodInfo;
        private readonly VodDownloadRunner _runner;
        private readonly AudioRestoreService _restore;
        private readonly ExactTrimService _exactTrim;
        private readonly LibraryStorage _storage;
        private readonly IOptions<DownloadSettings> _settings;
        private readonly ILogger<DownloadWorker> _logger;

        public DownloadWorker(IServiceScopeFactory scopeFactory, DownloadQueue queue, VodInfoService vodInfo, VodDownloadRunner runner,
            AudioRestoreService restore, ExactTrimService exactTrim, LibraryStorage storage, IOptions<DownloadSettings> settings, ILogger<DownloadWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _queue = queue;
            _vodInfo = vodInfo;
            _runner = runner;
            _restore = restore;
            _exactTrim = exactTrim;
            _storage = storage;
            _settings = settings;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            DeleteLeftoverWorkFolders();
            await RequeueInterruptedJobsAsync(stoppingToken);

            var consumers = Enumerable.Range(0, Math.Max(1, _settings.Value.MaxConcurrentJobs))
                .Select(_ => ConsumeAsync(stoppingToken));
            await Task.WhenAll(consumers);
        }

        /// <summary>Scratch folders (e.g. audio restores) left by a job that was running when the app stopped. No job is running yet.</summary>
        private void DeleteLeftoverWorkFolders()
        {
            var folder = Path.Combine(_storage.RootFolder, LibraryStorage.WorkFolderName);
            if (!Directory.Exists(folder))
                return;

            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories())
            {
                try
                {
                    dir.Delete(true);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning("Couldn't delete leftover work folder {Path}: {Message}", dir.FullName, ex.Message);
                }
            }
        }

        /// <summary>Jobs are only held in memory, so re-queue anything the database says was queued or running at shutdown.</summary>
        private async Task RequeueInterruptedJobsAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

            var entries = await db.Entries
                .Where(x => x.Status == EntryStatus.Queued || x.Status == EntryStatus.Downloading
                            || x.GenerationStatus == GenerationStatus.Queued || x.GenerationStatus == GenerationStatus.Generating)
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);

            foreach (var entry in entries)
            {
                if (entry.Status == EntryStatus.Downloading || entry.GenerationStatus == GenerationStatus.Generating)
                    db.Audit(AuditActions.DownloadInterrupted, entry.Id, "App stopped during the download; re-queued");

                if (entry.Status is EntryStatus.Queued or EntryStatus.Downloading)
                {
                    entry.Status = EntryStatus.Queued;
                    _queue.Enqueue(entry.Id, DownloadJobKind.Store);
                }
                else
                {
                    entry.GenerationStatus = GenerationStatus.Queued;
                    _queue.Enqueue(entry.Id, DownloadJobKind.Generate);
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            if (entries.Count > 0)
                _logger.LogInformation("Re-queued {Count} interrupted download job(s).", entries.Count);
        }

        private async Task ConsumeAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        await ProcessAsync(job, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error processing {Kind} job for entry {EntryId}.", job.Kind, job.EntryId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down. Interrupted entries stay Downloading/Generating and are re-queued on the next start.
            }
        }

        private async Task ProcessAsync(DownloadJob job, CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

            var entry = await db.Entries.FindAsync([job.EntryId], stoppingToken);
            if (entry is null)
                return;

            var isGenerate = job.Kind == DownloadJobKind.Generate;

            // Skip duplicate or stale jobs (e.g. the entry was re-queued at startup and also by a request)
            if (isGenerate ? entry.GenerationStatus != GenerationStatus.Queued : entry.Status != EntryStatus.Queued)
                return;

            if (isGenerate)
                entry.GenerationStatus = GenerationStatus.Generating;
            else
                entry.Status = EntryStatus.Downloading;

            entry.Error = null;
            entry.UpdatedAtUtc = DateTime.UtcNow;
            db.Audit(AuditActions.DownloadStarted, entry.Id, isGenerate ? "On-demand file for reference-only entry" : "Stored clip");
            if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                return;

            var relativePath = isGenerate ? LibraryStorage.GeneratedRelativePath(entry.Id) : LibraryStorage.StoredRelativePath(entry.Id);
            var fullPath = _storage.Resolve(relativePath);
            var jobCts = _queue.BeginJob(entry.Id, stoppingToken);

            try
            {
                _logger.LogInformation("Starting {Kind} job for entry {EntryId} (VOD {VodId}).", job.Kind, entry.Id, entry.VodId);

                if (await _vodInfo.GetVodAsync(entry.VodId) is null)
                {
                    entry.Status = EntryStatus.VodUnavailable;
                    entry.GenerationStatus = GenerationStatus.None;
                    entry.Error = "The VOD is no longer available on Twitch.";
                    entry.UpdatedAtUtc = DateTime.UtcNow;
                    db.Audit(AuditActions.DownloadFailed, entry.Id, entry.Error);
                    await SaveIgnoringDeletedAsync(db, stoppingToken);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                // Swap rescued clean audio into muted parts if we have any; otherwise (or if that fails) a normal download.
                // We own this path, so overwrite any leftover from an earlier attempt.
                AudioRestoreResult? restore = null;
                try
                {
                    restore = await _restore.TryDownloadRestoredAsync(db, entry, fullPath, _logger, jobCts.Token);
                    if (restore is { Downloaded: true })
                        db.Audit(AuditActions.AudioRestored, entry.Id, $"{restore.RestoredSeconds:0.#}s of muted audio replaced");
                    if (restore is { StillMutedSeconds: > 0 })
                        db.Audit(AuditActions.AudioNotRestored, entry.Id, $"{restore.StillMutedSeconds:0.#}s of muted audio had no saved clean copy");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Restoring audio for entry {EntryId} failed; downloading without it.", entry.Id);
                    db.Audit(AuditActions.AudioRestoreFailed, entry.Id, ex.Message);
                }

                if (restore is not { Downloaded: true })
                {
                    if (entry.TrimMode == VideoTrimMode.Exact)
                        await _exactTrim.DownloadAsync(entry, fullPath, _logger, jobCts.Token);
                    else
                        await _runner.DownloadAsync(entry.VodId, entry.Quality, entry.TrimMode, entry.StartTime, entry.EndTime,
                            fullPath, info => info, _logger, jobCts.Token);
                }

                entry.AudioRestoredSeconds = restore is { Downloaded: true, RestoredSeconds: > 0 } ? restore.RestoredSeconds : null;
                entry.AudioMutedSeconds = restore is { StillMutedSeconds: > 0 } ? restore.StillMutedSeconds : null;

                var size = new FileInfo(fullPath).Length;
                if (isGenerate)
                {
                    entry.GenerationStatus = GenerationStatus.Ready;
                    entry.GeneratedFilePath = relativePath;
                    entry.GeneratedFileSizeBytes = size;
                    entry.GeneratedExpiresAtUtc = DateTime.UtcNow.AddHours(_settings.Value.GeneratedFileHours);
                }
                else
                {
                    entry.Status = EntryStatus.Ready;
                    entry.FilePath = relativePath;
                    entry.FileSizeBytes = size;
                }

                entry.UpdatedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.DownloadCompleted, entry.Id, $"{size} bytes");

                if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                {
                    // The entry was deleted while the last step ran
                    _storage.TryDelete(fullPath);
                }

                _logger.LogInformation("Finished {Kind} job for entry {EntryId} ({Size} bytes).", job.Kind, entry.Id, size);
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Cancelled because the entry was deleted. Core removes its partial output; this covers anything left over.
                _logger.LogInformation("{Kind} job for entry {EntryId} was cancelled.", job.Kind, entry.Id);
                _storage.TryDelete(fullPath);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "{Kind} job for entry {EntryId} failed.", job.Kind, entry.Id);

                if (isGenerate)
                    entry.GenerationStatus = GenerationStatus.Failed;
                else
                    entry.Status = EntryStatus.Failed;

                entry.Error = ex.Message;
                entry.UpdatedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.DownloadFailed, entry.Id, ex.Message);
                await SaveIgnoringDeletedAsync(db, stoppingToken);
            }
            finally
            {
                _queue.EndJob(entry.Id);
            }
        }

        /// <returns>False if the entry was deleted by a request while the job ran.</returns>
        private static async Task<bool> SaveIgnoringDeletedAsync(LibraryDbContext db, CancellationToken cancellationToken)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The entry row is gone. Save the audit event on its own so the outcome is still recorded.
                foreach (var tracked in db.ChangeTracker.Entries<LibraryEntry>().ToList())
                    tracked.State = EntityState.Detached;

                await db.SaveChangesAsync(cancellationToken);
                return false;
            }
        }
    }
}
