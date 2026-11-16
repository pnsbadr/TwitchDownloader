using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Services.Segments
{
    /// <summary>
    /// Makes exports (<see cref="ExportBatch"/>), one at a time and their items in list order. VOD ranges, segments and clips are cut like
    /// segment downloads (<see cref="KeptPartsCutter"/>: Exact, saved clean audio, cut-out parts skipped); a stored clip whose file already is
    /// what was asked for is copied instead, and so is a vertical render. A video becomes one file per segment (served as a .zip) or, joined,
    /// one file (<see cref="SegmentJoiner"/>). One item failing doesn't stop the others; items already made are kept across a restart.
    /// </summary>
    public sealed class ExportWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ExportQueue _queue;
        private readonly KeptPartsCutter _cutter;
        private readonly IVodDetailsSource _vods;
        private readonly ShortformService _shortform;
        private readonly LibraryStorage _storage;
        private readonly IConfiguration _configuration;
        private readonly IOptions<DownloadSettings> _settings;
        private readonly ILogger<ExportWorker> _logger;

        public ExportWorker(IServiceScopeFactory scopeFactory, ExportQueue queue, KeptPartsCutter cutter, IVodDetailsSource vods, ShortformService shortform,
            LibraryStorage storage, IConfiguration configuration, IOptions<DownloadSettings> settings, ILogger<ExportWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _queue = queue;
            _cutter = cutter;
            _vods = vods;
            _shortform = shortform;
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
                await foreach (var batchId in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        await ProcessAsync(batchId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error in export {BatchId}.", batchId);
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
            var batches = await db.ExportBatches.Include(x => x.Items)
                .Where(x => x.Status == ExportStatus.Queued || x.Status == ExportStatus.Running)
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);

            foreach (var batch in batches)
            {
                batch.Status = ExportStatus.Queued;
                foreach (var item in batch.Items.Where(x => x.Status == ExportStatus.Running))
                {
                    item.Status = ExportStatus.Queued;
                    item.PartsDone = 0;
                }
                _queue.Enqueue(batch.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (batches.Count > 0)
                _logger.LogInformation("Re-queued {Count} export(s).", batches.Count);
        }

        private async Task ProcessAsync(int batchId, CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var batch = await db.ExportBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == batchId, stoppingToken);
            if (batch is null || batch.Status != ExportStatus.Queued)
                return;

            batch.Status = ExportStatus.Running;
            batch.Error = null;
            batch.UpdatedAtUtc = DateTime.UtcNow;
            if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                return;

            var folder = _storage.Resolve(LibraryStorage.ExportRelativeFolder(batch.Id));
            var jobCts = _queue.BeginJob(batch.Id, stoppingToken);
            var token = jobCts.Token;
            var vodGone = new Dictionary<long, bool>();
            try
            {
                Directory.CreateDirectory(folder);
                _logger.LogInformation("Starting export {BatchId}: {Count} item(s).", batch.Id, batch.Items.Count);

                foreach (var item in batch.Items.OrderBy(x => x.Position))
                {
                    if (item.Status == ExportStatus.Ready && item.FilePath is not null && Path.Exists(_storage.Resolve(item.FilePath)))
                        continue;

                    item.Status = ExportStatus.Running;
                    item.Error = null;
                    item.PartsDone = 0;
                    batch.UpdatedAtUtc = DateTime.UtcNow;
                    if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                        return;

                    try
                    {
                        if (item.Kind != ExportItemKind.ClipVertical && await IsVodGoneAsync(item.VodId, vodGone))
                            throw new InvalidOperationException(SegmentExportWorker.VodGoneMessage);

                        await MakeItemAsync(db, batch, item, () => SaveIgnoringDeletedAsync(db, stoppingToken), token);
                        item.Status = ExportStatus.Ready;
                        item.PartsDone = item.PartsTotal;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Item {Position} of export {BatchId} failed.", item.Position, batch.Id);
                        item.Status = ExportStatus.Failed;
                        item.Error = ex.Message;
                        DeleteItemFiles(batch.Id, item.Position);
                        item.FilePath = null;
                        item.FileSizeBytes = null;
                    }

                    batch.UpdatedAtUtc = DateTime.UtcNow;
                    if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                    {
                        DeleteFolder(folder);
                        return;
                    }
                }

                var ready = batch.Items.Count(x => x.Status == ExportStatus.Ready);
                var now = DateTime.UtcNow;
                batch.Status = ready > 0 ? ExportStatus.Ready : ExportStatus.Failed;
                batch.Error = ready == batch.Items.Count ? null
                    : ready == 0 ? $"None of the items could be made. {batch.Items.OrderBy(x => x.Position).First().Error}".Trim()
                    : $"{batch.Items.Count - ready} of {batch.Items.Count} items failed.";
                batch.UpdatedAtUtc = now;
                batch.ExpiresAtUtc = now.AddHours(_settings.Value.GeneratedFileHours);
                var bytes = batch.Items.Sum(x => x.FileSizeBytes ?? 0);
                db.Audit(ready > 0 ? AuditActions.ExportReady : AuditActions.ExportFailed, null,
                    $"Export {batch.Id}: {ready}/{batch.Items.Count} item(s), {bytes} bytes{(batch.Error is null ? "" : $"; {batch.Error}")}", teamId: batch.TeamId);
                if (!await SaveIgnoringDeletedAsync(db, stoppingToken))
                    DeleteFolder(folder);

                _logger.LogInformation("Finished export {BatchId} ({Bytes} bytes).", batch.Id, bytes);
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Deleted while running: the request already removed the row
                _logger.LogInformation("Export {BatchId} was cancelled.", batch.Id);
                DeleteFolder(folder);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Export {BatchId} failed.", batch.Id);
                batch.Status = ExportStatus.Failed;
                batch.Error = ex.Message;
                batch.UpdatedAtUtc = DateTime.UtcNow;
                batch.ExpiresAtUtc = DateTime.UtcNow.AddHours(_settings.Value.GeneratedFileHours);
                db.Audit(AuditActions.ExportFailed, null, $"Export {batch.Id}: {ex.Message}", teamId: batch.TeamId);
                await SaveIgnoringDeletedAsync(db, stoppingToken);
            }
            finally
            {
                _queue.EndJob(batch.Id);
            }
        }

        private async Task<bool> IsVodGoneAsync(long vodId, Dictionary<long, bool> known)
        {
            if (!known.TryGetValue(vodId, out var gone))
                known[vodId] = gone = await _vods.GetVodAsync(vodId) is null;
            return gone;
        }

        private async Task MakeItemAsync(LibraryDbContext db, ExportBatch batch, ExportItem item, Func<Task<bool>> saveProgress, CancellationToken token)
        {
            var relativePath = LibraryStorage.ExportItemRelativePath(batch.Id, item.Position);
            var fullPath = _storage.Resolve(relativePath);
            AudioRestoreResult? restore = null;
            var copied = false;

            switch (item.Kind)
            {
                case ExportItemKind.ClipVertical:
                    item.PartsTotal = 1;
                    if (item.EntryId is not { } verticalOf || item.RenderId is null || _shortform.RenderPath(verticalOf, item.RenderId, "mp4") is not { } render
                        || !File.Exists(render))
                        throw new InvalidOperationException("That vertical render has expired or was deleted.");
                    File.Copy(render, fullPath, overwrite: true);
                    copied = true;
                    break;

                case ExportItemKind.Clip when await CopyableClipFileAsync(db, item, token) is { } stored:
                    item.PartsTotal = 1;
                    File.Copy(stored.Path, fullPath, overwrite: true);
                    item.AudioRestoredSeconds = stored.Entry.AudioRestoredSeconds;
                    item.AudioMutedSeconds = stored.Entry.AudioMutedSeconds;
                    copied = true;
                    break;

                case ExportItemKind.Clip when await WholeStoredFileAsync(db, item, token) is { } whole:
                    // Its stored file holds the whole clip: cut what's kept out of it, nothing is downloaded
                    item.PartsTotal = 1;
                    await CutFromFileAsync(whole.Path, item, fullPath, $"export-{batch.Id}-{item.Position}", token);
                    item.AudioRestoredSeconds = whole.Entry.AudioRestoredSeconds;
                    item.AudioMutedSeconds = whole.Entry.AudioMutedSeconds;
                    copied = true;
                    break;

                case ExportItemKind.VodRange or ExportItemKind.Segment or ExportItemKind.Clip:
                    item.PartsTotal = 1;
                    restore = await CutAsync(db, batch, item, item.StartSeconds, item.EndSeconds, CutOuts.Read(item.Cuts), fullPath, token);
                    break;

                case ExportItemKind.Video:
                    restore = await MakeVideoAsync(db, batch, item, saveProgress, token);
                    if (!item.Joined)
                    {
                        var partsFolder = LibraryStorage.ExportItemRelativeFolder(batch.Id, item.Position);
                        item.FilePath = partsFolder;
                        item.FileSizeBytes = new DirectoryInfo(_storage.Resolve(partsFolder)).EnumerateFiles().Sum(x => x.Length);
                        SetRestore(item, restore);
                        return;
                    }
                    break;
            }

            item.FilePath = relativePath;
            item.FileSizeBytes = new FileInfo(fullPath).Length;
            if (!copied)
                SetRestore(item, restore);
        }

        private static void SetRestore(ExportItem item, AudioRestoreResult? restore)
        {
            item.AudioRestoredSeconds = restore is { Downloaded: true, RestoredSeconds: > 0 } ? restore.RestoredSeconds : null;
            item.AudioMutedSeconds = restore is { StillMutedSeconds: > 0 } ? restore.StillMutedSeconds : null;
        }

        /// <summary>
        /// The clip's stored file, if it is exactly what the item asks for (same cut, quality, and the file leaves out exactly the item's
        /// cut-out parts: files hold the whole range, so that is a clip without any, or a file made before 2026-11-16 with the same ones).
        /// </summary>
        private async Task<(string Path, LibraryEntry Entry)?> CopyableClipFileAsync(LibraryDbContext db, ExportItem item, CancellationToken token)
        {
            if (item.EntryId is not { } entryId)
                return null;
            var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == entryId, token);
            if (entry is not { Status: EntryStatus.Ready, FilePath: { } path } || entry.Quality != item.Quality || entry.TrimMode != item.TrimMode
                || Math.Abs(entry.StartTime.TotalSeconds - item.StartSeconds) > 0.005
                || Math.Abs((entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds) - item.EndSeconds) > 0.005
                || CutOuts.Write(CutOuts.Normalize(CutOuts.Read(entry.FileCuts), item.StartSeconds, item.EndSeconds)) != item.Cuts)
                return null;
            var full = _storage.Resolve(path);
            return File.Exists(full) ? (full, entry) : null;
        }

        /// <summary>
        /// The clip's stored file when it holds the item's whole range at its quality, cut Exact (a Safe cut starts a little early, so file
        /// seconds wouldn't line up), so the item's kept parts can be cut out of it.
        /// </summary>
        private async Task<(string Path, LibraryEntry Entry)?> WholeStoredFileAsync(LibraryDbContext db, ExportItem item, CancellationToken token)
        {
            if (item.EntryId is not { } entryId)
                return null;
            var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == entryId, token);
            if (entry is not { Status: EntryStatus.Ready, FilePath: { } path, FileCuts: null, TrimMode: TwitchDownloaderCore.Models.VideoTrimMode.Exact }
                || entry.Quality != item.Quality
                || Math.Abs(entry.StartTime.TotalSeconds - item.StartSeconds) > 0.005
                || Math.Abs((entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds) - item.EndSeconds) > 0.005)
                return null;
            var full = _storage.Resolve(path);
            return File.Exists(full) ? (full, entry) : null;
        }

        /// <summary>Cuts the item's kept parts out of a file that starts at the item's start (re-encoded like Exact trims) and joins them.</summary>
        private async Task CutFromFileAsync(string source, ExportItem item, string outputPath, string workName, CancellationToken token)
        {
            var parts = CutOuts.Kept(item.StartSeconds, item.EndSeconds, CutOuts.Read(item.Cuts));
            if (parts.Count == 0)
                throw new InvalidOperationException("The cut-out parts leave nothing.");

            var settings = _settings.Value;
            var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
            var work = _storage.CreateWorkFolder(workName);
            try
            {
                if (parts.Count == 1)
                {
                    await ExactTrimService.TrimAsync(ffmpegPath, source, parts[0].Start - item.StartSeconds, parts[0].End - parts[0].Start, outputPath, work,
                        settings.ExactTrimPreset, settings.ExactTrimCrf, _logger, token);
                    return;
                }

                var files = new List<string>();
                for (var i = 0; i < parts.Count; i++)
                {
                    var file = Path.Combine(work, $"{i + 1}.mp4");
                    await ExactTrimService.TrimAsync(ffmpegPath, source, parts[i].Start - item.StartSeconds, parts[i].End - parts[i].Start, file, work,
                        settings.ExactTrimPreset, settings.ExactTrimCrf, _logger, token);
                    files.Add(file);
                }
                await SegmentJoiner.JoinAsync(ffmpegPath, files, outputPath, work, settings.ExactTrimPreset, settings.ExactTrimCrf, _logger, token);
            }
            finally
            {
                DeleteFolder(work);
            }
        }

        /// <summary>One file per segment in the item's folder; joined, those are then joined into the item's file and the folder goes.</summary>
        private async Task<AudioRestoreResult?> MakeVideoAsync(LibraryDbContext db, ExportBatch batch, ExportItem item, Func<Task<bool>> saveProgress,
            CancellationToken token)
        {
            var parts = ExportVideoPart.Read(item.Parts);
            if (parts.Count == 0)
                throw new InvalidOperationException("The video has no segments.");

            item.PartsTotal = parts.Count + (item.Joined ? 1 : 0);
            var partsFolder = _storage.Resolve(LibraryStorage.ExportItemRelativeFolder(batch.Id, item.Position));
            Directory.CreateDirectory(partsFolder);
            var files = new List<string>();
            var restores = new List<AudioRestoreResult>();
            for (var i = 0; i < parts.Count; i++)
            {
                var file = Path.Combine(partsFolder, $"{i + 1}.mp4");
                if (await CutAsync(db, batch, item, parts[i].Start, parts[i].End, parts[i].Cuts, file, token) is { } restore)
                    restores.Add(restore);
                files.Add(file);
                item.PartsDone = i + 1;
                await saveProgress();
            }

            if (item.Joined)
            {
                var work = _storage.CreateWorkFolder($"export-{batch.Id}-{item.Position}");
                try
                {
                    var settings = _settings.Value;
                    var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
                    await SegmentJoiner.JoinAsync(ffmpegPath, files, _storage.Resolve(LibraryStorage.ExportItemRelativePath(batch.Id, item.Position)), work,
                        settings.ExactTrimPreset, settings.ExactTrimCrf, _logger, token);
                }
                finally
                {
                    DeleteFolder(work);
                }
                DeleteFolder(partsFolder);
            }

            return restores.Count == 0
                ? null
                : new AudioRestoreResult(restores.Sum(x => x.RestoredSeconds), restores.Sum(x => x.StillMutedSeconds), restores.Any(x => x.Downloaded));
        }

        private Task<AudioRestoreResult?> CutAsync(LibraryDbContext db, ExportBatch batch, ExportItem item, double start, double end,
            IReadOnlyList<CutRange> cuts, string outputPath, CancellationToken token)
        {
            // A transient entry: the cut code works on entries, but an export never becomes one
            var cut = new LibraryEntry
            {
                Title = item.FileName,
                VodId = item.VodId,
                Quality = item.Quality,
                TrimMode = item.TrimMode,
                StartTime = TimeSpan.FromSeconds(start),
                EndTime = TimeSpan.FromSeconds(end),
                TeamId = batch.TeamId,
            };
            return _cutter.CutAsync(db, cut, cuts, end, outputPath, _logger,
                error => db.Audit(AuditActions.AudioRestoreFailed, item.EntryId, $"Export {batch.Id}, item {item.Position}: {error}", teamId: batch.TeamId),
                token);
        }

        private void DeleteItemFiles(int batchId, int position)
        {
            _storage.TryDelete(LibraryStorage.ExportItemRelativePath(batchId, position));
            DeleteFolder(_storage.Resolve(LibraryStorage.ExportItemRelativeFolder(batchId, position)));
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
                foreach (var tracked in db.ChangeTracker.Entries().Where(x => x.Entity is ExportBatch or ExportItem).ToList())
                    tracked.State = EntityState.Detached;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }
        }
    }
}
