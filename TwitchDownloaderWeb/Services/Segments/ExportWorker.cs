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
        private readonly Projects.ProjectRenderer _projects;
        private readonly LibraryStorage _storage;
        private readonly IConfiguration _configuration;
        private readonly IOptions<DownloadSettings> _settings;
        private readonly ILogger<ExportWorker> _logger;

        public ExportWorker(IServiceScopeFactory scopeFactory, ExportQueue queue, KeptPartsCutter cutter, IVodDetailsSource vods, ShortformService shortform,
            Projects.ProjectRenderer projects, LibraryStorage storage, IConfiguration configuration, IOptions<DownloadSettings> settings, ILogger<ExportWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _queue = queue;
            _cutter = cutter;
            _vods = vods;
            _shortform = shortform;
            _projects = projects;
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

                    var progress = _queue.StartItem(batch.Id, item.Position);
                    progress.SetStep("Starting");
                    using var ambient = JobProgress.Use(progress);
                    var itemClock = System.Diagnostics.Stopwatch.StartNew();
                    _logger.LogInformation("Export {BatchId}, item {Position} of {Count}: {Kind} \"{FileName}\"{Details}.", batch.Id, item.Position,
                        batch.Items.Count, item.Kind, item.FileName,
                        item.EndSeconds > item.StartSeconds
                            ? $" (VOD {item.VodId}, {JobProgress.FormatTime(item.StartSeconds)}-{JobProgress.FormatTime(item.EndSeconds)}{(item.Quality is null ? "" : $", {item.Quality}")})"
                            : "");
                    try
                    {
                        // A clip made from its stored file doesn't need the VOD (nor Twitch to answer)
                        if (item.Kind is not (ExportItemKind.ClipVertical or ExportItemKind.Project or ExportItemKind.ProjectVertical)
                            && !(item.Kind == ExportItemKind.Clip && await UsesStoredFileAsync(db, item, token))
                            && await IsVodGoneAsync(item.VodId, vodGone))
                            throw new InvalidOperationException(SegmentExportWorker.VodGoneMessage);

                        await MakeItemAsync(db, batch, item, vodGone, () => SaveIgnoringDeletedAsync(db, stoppingToken), token);
                        item.Status = ExportStatus.Ready;
                        item.PartsDone = item.PartsTotal;
                        _logger.LogInformation("Export {BatchId}, item {Position} is ready ({Elapsed}, {Megabytes} MB).", batch.Id, item.Position,
                            JobProgress.FormatTime(itemClock.Elapsed.TotalSeconds), (item.FileSizeBytes ?? 0) / 1_000_000);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
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

        private async Task MakeItemAsync(LibraryDbContext db, ExportBatch batch, ExportItem item, Dictionary<long, bool> vodGone, Func<Task<bool>> saveProgress,
            CancellationToken token)
        {
            var relativePath = LibraryStorage.ExportItemRelativePath(batch.Id, item.Position);
            var fullPath = _storage.Resolve(relativePath);
            AudioRestoreResult? restore = null;
            var copied = false;
            void Step(string step)
            {
                JobProgress.Current?.SetStep(step);
                _logger.LogInformation("{Step}.", step);
            }

            switch (item.Kind)
            {
                case ExportItemKind.ClipVertical:
                    item.PartsTotal = 1;
                    Step("Copying the vertical render");
                    if (item.EntryId is not { } verticalOf || item.RenderId is null || _shortform.RenderPath(verticalOf, item.RenderId, "mp4") is not { } render
                        || !File.Exists(render))
                        throw new InvalidOperationException("That vertical render has expired or was deleted.");
                    File.Copy(render, fullPath, overwrite: true);
                    copied = true;
                    break;

                case ExportItemKind.Clip when await CopyableClipFileAsync(db, item, token) is { } stored:
                    item.PartsTotal = 1;
                    Step("Copying the stored clip");
                    File.Copy(stored.Path, fullPath, overwrite: true);
                    item.AudioRestoredSeconds = stored.Entry.AudioRestoredSeconds;
                    item.AudioMutedSeconds = stored.Entry.AudioMutedSeconds;
                    copied = true;
                    break;

                case ExportItemKind.Clip when await WholeStoredFileAsync(db, item, token) is { } whole:
                    // Its stored file holds the whole clip: cut what's kept out of it, nothing is downloaded
                    item.PartsTotal = 1;
                    await CutFromFileAsync(whole.Path, item, null, fullPath, $"export-{batch.Id}-{item.Position}", token);
                    item.AudioRestoredSeconds = whole.Entry.AudioRestoredSeconds;
                    item.AudioMutedSeconds = whole.Entry.AudioMutedSeconds;
                    copied = true;
                    break;

                case ExportItemKind.Clip when await ExactStoredFileAsync(db, item, token) is { } kept && await IsVodGoneAsync(item.VodId, vodGone):
                    // The VOD is gone from Twitch (badrfly 2026-11-16: stored clips always stay exportable): cut what's kept out of the stored
                    // file, at the file's quality whatever was asked; parts an older file already left out can't come back
                    item.PartsTotal = 1;
                    item.Quality = kept.Entry.Quality;
                    await CutFromFileAsync(kept.Path, item, CutOuts.Read(kept.Entry.FileCuts), fullPath, $"export-{batch.Id}-{item.Position}", token);
                    item.AudioRestoredSeconds = kept.Entry.AudioRestoredSeconds;
                    item.AudioMutedSeconds = kept.Entry.AudioMutedSeconds;
                    copied = true;
                    break;

                case ExportItemKind.VodRange or ExportItemKind.Segment or ExportItemKind.Clip:
                    item.PartsTotal = 1;
                    restore = await CutAsync(db, batch, item, item.StartSeconds, item.EndSeconds, CutOuts.Read(item.Cuts), fullPath, token);
                    break;

                case ExportItemKind.Project or ExportItemKind.ProjectVertical:
                {
                    var content = ProjectContent.Read(item.Parts);
                    foreach (var vodId in Projects.ProjectTimeline.Flatten(content).Where(x => !x.IsGap).Select(x => x.Piece!.VodId).Distinct())
                        if (await IsVodGoneAsync(vodId, vodGone))
                            throw new InvalidOperationException($"VOD {vodId} in the project is gone from Twitch.");
                    var partsFolder = item.Separate ? LibraryStorage.ExportItemRelativeFolder(batch.Id, item.Position) : null;
                    (_, restore) = await _projects.RenderAsync(db, batch.TeamId, content, item.Kind == ExportItemKind.ProjectVertical, item.Quality,
                        partsFolder is null ? fullPath : _storage.Resolve(partsFolder),
                        $"export-{batch.Id}-{item.Position}", _logger,
                        error => db.Audit(AuditActions.AudioRestoreFailed, null, $"Export {batch.Id}, item {item.Position}: {error}", teamId: batch.TeamId),
                        async (done, total) =>
                        {
                            item.PartsDone = done;
                            item.PartsTotal = total;
                            await saveProgress();
                        }, token, separate: partsFolder is not null);
                    if (partsFolder is not null)
                    {
                        // separate files: a folder, zipped when served (like a video's segments)
                        item.FilePath = partsFolder;
                        item.FileSizeBytes = new DirectoryInfo(_storage.Resolve(partsFolder)).EnumerateFiles().Sum(x => x.Length);
                        SetRestore(item, restore);
                        return;
                    }
                    break;
                }

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

        private async Task<bool> UsesStoredFileAsync(LibraryDbContext db, ExportItem item, CancellationToken token) =>
            await CopyableClipFileAsync(db, item, token) is not null || await WholeStoredFileAsync(db, item, token) is not null
            || await ExactStoredFileAsync(db, item, token) is not null;

        /// <summary>
        /// The clip's stored file when it was cut Exact over the item's range, at any quality and whatever cut-out parts it already leaves
        /// out: what's left of a clip whose VOD is gone. (A Safe file starts at a segment boundary before the clip, at an offset only the
        /// VOD's playlist can tell, so it can't be cut.)
        /// </summary>
        private async Task<(string Path, LibraryEntry Entry)?> ExactStoredFileAsync(LibraryDbContext db, ExportItem item, CancellationToken token)
        {
            if (item.EntryId is not { } entryId)
                return null;
            var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == entryId, token);
            if (entry is not { Status: EntryStatus.Ready, FilePath: { } path, TrimMode: TwitchDownloaderCore.Models.VideoTrimMode.Exact }
                || Math.Abs(entry.StartTime.TotalSeconds - item.StartSeconds) > 0.005
                || Math.Abs((entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds) - item.EndSeconds) > 0.005)
                return null;
            var full = _storage.Resolve(path);
            return File.Exists(full) ? (full, entry) : null;
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

        /// <summary>
        /// Cuts the item's kept parts out of a file that starts at the item's start (re-encoded like Exact trims) and joins them.
        /// <paramref name="fileCuts"/>: parts the file already leaves out (VOD seconds), so later parts sit earlier in it.
        /// </summary>
        private async Task CutFromFileAsync(string source, ExportItem item, IReadOnlyList<CutRange>? fileCuts, string outputPath, string workName,
            CancellationToken token)
        {
            var parts = FileParts(item.StartSeconds, item.EndSeconds, CutOuts.Read(item.Cuts), fileCuts);
            if (parts.Count == 0)
                throw new InvalidOperationException("The cut-out parts leave nothing.");

            var settings = _settings.Value;
            var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
            var work = _storage.CreateWorkFolder(workName);
            try
            {
                if (parts.Count == 1)
                {
                    await ExactTrimService.TrimAsync(ffmpegPath, source, parts[0].Start, parts[0].End - parts[0].Start, outputPath, work,
                        settings.ExactTrimPreset, settings.ExactTrimCrf, _logger, token);
                    return;
                }

                var files = new List<string>();
                for (var i = 0; i < parts.Count; i++)
                {
                    var file = Path.Combine(work, $"{i + 1}.mp4");
                    await ExactTrimService.TrimAsync(ffmpegPath, source, parts[i].Start, parts[i].End - parts[i].Start, file, work,
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

        /// <summary>
        /// The item's kept parts in seconds of a file that holds [start, end] without <paramref name="fileCuts"/> (joined end to end).
        /// Parts the file doesn't have are left out; pieces under 0.1 s are dropped.
        /// </summary>
        public static List<(double Start, double End)> FileParts(double start, double end, IReadOnlyList<CutRange> itemCuts, IReadOnlyList<CutRange>? fileCuts)
        {
            var inFile = CutOuts.Kept(start, end, fileCuts ?? []);
            var result = new List<(double Start, double End)>();
            foreach (var part in CutOuts.Kept(start, end, itemCuts))
            {
                var fileAt = 0.0;
                foreach (var have in inFile)
                {
                    var (from, to) = (Math.Max(part.Start, have.Start), Math.Min(part.End, have.End));
                    if (to - from >= 0.1)
                    {
                        var at = fileAt + (from - have.Start);
                        // Touching the previous piece (only a file cut lay between): one piece
                        if (result.Count > 0 && Math.Abs(result[^1].End - at) < 0.001)
                            result[^1] = (result[^1].Start, at + (to - from));
                        else
                            result.Add((at, at + (to - from)));
                    }
                    fileAt += have.End - have.Start;
                }
            }
            return result;
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
                JobProgress.SetPrefix($"Segment {i + 1} of {parts.Count}");
                _logger.LogInformation("Export {BatchId}, item {Position}: segment {Number} of {Count} ({Start}-{End}).", batch.Id, item.Position, i + 1,
                    parts.Count, JobProgress.FormatTime(parts[i].Start), JobProgress.FormatTime(parts[i].End));
                if (await CutAsync(db, batch, item, parts[i].Start, parts[i].End, parts[i].Cuts, file, token) is { } restore)
                    restores.Add(restore);
                files.Add(file);
                item.PartsDone = i + 1;
                await saveProgress();
            }
            JobProgress.SetPrefix(null);

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
