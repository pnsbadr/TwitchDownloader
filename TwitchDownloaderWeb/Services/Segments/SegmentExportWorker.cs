using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Segments
{
    /// <summary>
    /// Makes segment downloads, one at a time: each segment is cut from the VOD like a library entry (<see cref="KeptPartsCutter"/>: Exact, saved
    /// clean audio in muted parts, cut-out parts skipped) into its own file, then for a combined download the files are joined (<see cref="SegmentJoiner"/>).
    /// Segments already made are kept when the app restarts in the middle, so the job picks up where it stopped.
    /// </summary>
    public sealed class SegmentExportWorker : BackgroundService
    {
        public const string VodGoneMessage = "Twitch no longer has this VOD.";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SegmentExportQueue _queue;
        private readonly KeptPartsCutter _cutter;
        private readonly IVodDetailsSource _vods;
        private readonly LibraryStorage _storage;
        private readonly IConfiguration _configuration;
        private readonly IOptions<DownloadSettings> _settings;
        private readonly ILogger<SegmentExportWorker> _logger;

        public SegmentExportWorker(IServiceScopeFactory scopeFactory, SegmentExportQueue queue, KeptPartsCutter cutter, IVodDetailsSource vods, LibraryStorage storage,
            IConfiguration configuration, IOptions<DownloadSettings> settings, ILogger<SegmentExportWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _queue = queue;
            _cutter = cutter;
            _vods = vods;
            _storage = storage;
            _configuration = configuration;
            _settings = settings;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await RequeueInterruptedAsync(stoppingToken);
                await foreach (var exportId in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        await ProcessAsync(exportId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error in segment download {ExportId}.", exportId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down; the export stays Running and is queued again on the next start
            }
        }

        private async Task RequeueInterruptedAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var exports = await db.SegmentExports.Include(x => x.Items)
                .Where(x => x.Status == SegmentExportStatus.Queued || x.Status == SegmentExportStatus.Running)
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);

            foreach (var export in exports)
            {
                export.Status = SegmentExportStatus.Queued;
                foreach (var item in export.Items.Where(x => x.Status == SegmentExportItemStatus.Running))
                    item.Status = SegmentExportItemStatus.Queued;
                _queue.Enqueue(export.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (exports.Count > 0)
                _logger.LogInformation("Re-queued {Count} segment download(s).", exports.Count);
        }

        private async Task ProcessAsync(int exportId, CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var export = await db.SegmentExports.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == exportId, stoppingToken);
            if (export is null || export.Status != SegmentExportStatus.Queued)
                return;

            export.Status = SegmentExportStatus.Running;
            export.Error = null;
            export.UpdatedAtUtc = DateTime.UtcNow;
            if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                return;

            var folder = _storage.Resolve(LibraryStorage.SegmentExportRelativeFolder(export.Id));
            var jobCts = _queue.BeginJob(export.Id, stoppingToken);
            var token = jobCts.Token;
            try
            {
                Directory.CreateDirectory(folder);
                _logger.LogInformation("Starting segment download {ExportId}: {Count} segment(s) of VOD {VodId}.", export.Id, export.Items.Count, export.VodId);

                if (await _vods.GetVodAsync(export.VodId) is null)
                {
                    await FailAsync(db, export, VodGoneMessage, stoppingToken);
                    return;
                }

                foreach (var item in export.Items.OrderBy(x => x.Position))
                {
                    if (item.Status == SegmentExportItemStatus.Ready && item.FilePath is not null && File.Exists(_storage.Resolve(item.FilePath)))
                        continue;

                    item.Status = SegmentExportItemStatus.Running;
                    item.Error = null;
                    export.UpdatedAtUtc = DateTime.UtcNow;
                    if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                        return;

                    await CutItemAsync(db, export, item, token);
                    export.UpdatedAtUtc = DateTime.UtcNow;
                    if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                    {
                        DeleteFolder(folder);
                        return;
                    }
                }

                var ready = export.Items.Where(x => x.Status == SegmentExportItemStatus.Ready).OrderBy(x => x.Position).ToList();
                if (ready.Count == 0)
                {
                    var first = export.Items.OrderBy(x => x.Position).First();
                    await FailAsync(db, export, $"None of the segments could be made. {first.Error}".Trim(), stoppingToken);
                    return;
                }

                if (export.Combined)
                {
                    if (ready.Count < export.Items.Count)
                    {
                        // Joining what's left would look like the whole selection; keep the files that worked and say why there's no joined one
                        export.Error = $"Not joined into one file: {export.Items.Count - ready.Count} segment(s) failed.";
                    }
                    else
                    {
                        await JoinAsync(export, ready, token);
                    }
                }

                export.Status = SegmentExportStatus.Ready;
                export.UpdatedAtUtc = DateTime.UtcNow;
                export.ExpiresAtUtc = DateTime.UtcNow.AddHours(_settings.Value.GeneratedFileHours);
                var bytes = ready.Sum(x => x.FileSizeBytes ?? 0) + (export.CombinedFileSizeBytes ?? 0);
                db.Audit(AuditActions.SegmentsReady, export.EntryId,
                    $"Segment download {export.Id}: {ready.Count}/{export.Items.Count} segment(s){(export.CombinedFilePath is null ? "" : ", joined")}, {bytes} bytes",
                    teamId: export.TeamId);
                if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                    DeleteFolder(folder);

                _logger.LogInformation("Finished segment download {ExportId} ({Bytes} bytes).", export.Id, bytes);
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Deleted while running: the request already removed the row
                _logger.LogInformation("Segment download {ExportId} was cancelled.", export.Id);
                DeleteFolder(folder);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Segment download {ExportId} failed.", export.Id);
                await FailAsync(db, export, ex.Message, stoppingToken);
            }
            finally
            {
                _queue.EndJob(export.Id);
            }
        }

        private async Task CutItemAsync(LibraryDbContext db, SegmentExport export, SegmentExportItem item, CancellationToken token)
        {
            var relativePath = LibraryStorage.SegmentFileRelativePath(export.Id, item.Position);
            var fullPath = _storage.Resolve(relativePath);
            // A transient entry: the cut code works on entries, but a segment never becomes one
            var cut = new LibraryEntry
            {
                Title = item.Label,
                VodId = export.VodId,
                Quality = export.Quality,
                TrimMode = VideoTrimMode.Exact,
                StartTime = TimeSpan.FromSeconds(item.StartSeconds),
                EndTime = TimeSpan.FromSeconds(item.EndSeconds),
                TeamId = export.TeamId,
            };

            try
            {
                var restore = await _cutter.CutAsync(db, cut, CutOuts.Read(item.Cuts), item.EndSeconds, fullPath, _logger,
                    error => db.Audit(AuditActions.AudioRestoreFailed, export.EntryId, $"Segment download {export.Id}, segment {item.Position}: {error}", teamId: export.TeamId),
                    token);
                item.Status = SegmentExportItemStatus.Ready;
                item.FilePath = relativePath;
                item.FileSizeBytes = new FileInfo(fullPath).Length;
                item.AudioRestoredSeconds = restore is { Downloaded: true, RestoredSeconds: > 0 } ? restore.RestoredSeconds : null;
                item.AudioMutedSeconds = restore is { StillMutedSeconds: > 0 } ? restore.StillMutedSeconds : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // One bad segment doesn't stop the others
                _logger.LogWarning(ex, "Segment {Position} of segment download {ExportId} failed.", item.Position, export.Id);
                item.Status = SegmentExportItemStatus.Failed;
                item.Error = ex.Message;
                _storage.TryDelete(fullPath);
            }
        }

        private async Task JoinAsync(SegmentExport export, IReadOnlyList<SegmentExportItem> ready, CancellationToken token)
        {
            var relativePath = LibraryStorage.CombinedSegmentsRelativePath(export.Id);
            var fullPath = _storage.Resolve(relativePath);
            var work = _storage.CreateWorkFolder($"join-{export.Id}");
            try
            {
                var settings = _settings.Value;
                var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
                await SegmentJoiner.JoinAsync(ffmpegPath, ready.Select(x => _storage.Resolve(x.FilePath!)).ToList(), fullPath, work,
                    settings.ExactTrimPreset, settings.ExactTrimCrf, _logger, token);
                export.CombinedFilePath = relativePath;
                export.CombinedFileSizeBytes = new FileInfo(fullPath).Length;
            }
            finally
            {
                DeleteFolder(work);
            }
        }

        private async Task FailAsync(LibraryDbContext db, SegmentExport export, string message, CancellationToken cancellationToken)
        {
            export.Status = SegmentExportStatus.Failed;
            export.Error = message;
            export.UpdatedAtUtc = DateTime.UtcNow;
            // Failed downloads are cleared like finished ones (the segments that worked can still be downloaded until then)
            export.ExpiresAtUtc = DateTime.UtcNow.AddHours(_settings.Value.GeneratedFileHours);
            db.Audit(AuditActions.SegmentsFailed, export.EntryId, $"Segment download {export.Id}: {message}", teamId: export.TeamId);
            await SaveIgnoringDeletedAsync(db, cancellationToken);
        }

        private void DeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Couldn't delete {Path}: {Message}; the cleanup sweep will retry.", folder, ex.Message);
            }
        }

        /// <returns>False if the export was deleted by a request while the job ran.</returns>
        private static async Task<bool> SaveIgnoringDeletedAsync(LibraryDbContext db, CancellationToken cancellationToken)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                foreach (var tracked in db.ChangeTracker.Entries().Where(x => x.Entity is SegmentExport or SegmentExportItem).ToList())
                    tracked.State = EntityState.Detached;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }
        }
    }
}
