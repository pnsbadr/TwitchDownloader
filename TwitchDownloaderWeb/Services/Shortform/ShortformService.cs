using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services.Shortform
{
    public enum ShortformStatus
    {
        None,
        Rendering,
        Failed
    }

    /// <summary>A finished portrait render, kept until it expires or is pushed out by newer ones.</summary>
    public sealed record ShortformRender(string Id, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, long SizeBytes, ShortformLayout Layout,
        bool HasThumbnail);

    /// <summary>The entry's current job (None when idle) plus its kept renders, newest first.</summary>
    public sealed record ShortformState(ShortformStatus Status, double Progress, string? Error, ShortformLayout? Layout,
        IReadOnlyList<ShortformRender> Renders);

    /// <summary>
    /// Renders an entry's finished file into 9:16 portrait videos using crop layouts from the page. Each render is kept in
    /// "shortform/{entryId}/{renderId}.mp4" with its layout ("{renderId}.json") and a thumbnail ("{renderId}.jpg"), shared by
    /// the team, until it is <see cref="DownloadSettings.GeneratedFileHours"/> old or more than <see cref="MaxRendersPerEntry"/>
    /// newer renders exist. One render runs at a time; a new render for the same entry replaces a running one. Job state
    /// is in memory: a render interrupted by a restart is simply gone.
    /// </summary>
    public sealed class ShortformService
    {
        public const int MaxRendersPerEntry = 5;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly LibraryStorage _storage;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ShortformService> _logger;
        private readonly TimeSpan _keepFor;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<int, Job> _jobs = new();

        public ShortformService(LibraryStorage storage, IServiceScopeFactory scopeFactory, IConfiguration configuration,
            IOptions<DownloadSettings> settings, ILogger<ShortformService> logger)
        {
            _storage = storage;
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
            _keepFor = TimeSpan.FromHours(settings.Value.GeneratedFileHours);
        }

        private sealed class Job
        {
            public required CancellationTokenSource Cancellation { get; init; }
            public required ShortformLayout Layout { get; init; }
            public ShortformStatus Status { get; set; } = ShortformStatus.Rendering;
            public double Progress { get; set; }
            public string? Error { get; set; }
        }

        /// <summary>What a render's metadata file holds.</summary>
        private sealed record RenderInfo(DateTime CreatedAtUtc, ShortformLayout Layout);

        private string EntryFolder(int entryId) => _storage.Resolve(Path.Combine(LibraryStorage.ShortformFolderName, entryId.ToString(CultureInfo.InvariantCulture)));

        /// <summary>Full path of a render's file, or null if the ID isn't one we generate (keeps request input out of paths).</summary>
        public string? RenderPath(int entryId, string renderId, string extension) =>
            IsValidRenderId(renderId) ? Path.Combine(EntryFolder(entryId), $"{renderId}.{extension}") : null;

        private static bool IsValidRenderId(string renderId) => renderId.Length is > 0 and <= 32 && renderId.All(char.IsAsciiLetterOrDigit);

        public ShortformState GetState(int entryId)
        {
            var renders = ListRenders(entryId);
            if (_jobs.TryGetValue(entryId, out var job))
                return new ShortformState(job.Status, job.Progress, job.Error, job.Layout, renders);

            return new ShortformState(ShortformStatus.None, 0, null, renders.FirstOrDefault()?.Layout, renders);
        }

        public IReadOnlyList<ShortformRender> ListRenders(int entryId)
        {
            var folder = EntryFolder(entryId);
            if (!Directory.Exists(folder))
                return [];

            var renders = new List<ShortformRender>();
            foreach (var json in new DirectoryInfo(folder).EnumerateFiles("*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(json.Name);
                var video = new FileInfo(Path.ChangeExtension(json.FullName, ".mp4"));
                if (!IsValidRenderId(id) || !video.Exists || ReadInfo(json.FullName) is not { } info)
                    continue;

                var expires = info.CreatedAtUtc + _keepFor;
                if (expires <= DateTime.UtcNow)
                    continue;

                renders.Add(new ShortformRender(id, info.CreatedAtUtc, expires, video.Length, info.Layout,
                    File.Exists(Path.ChangeExtension(json.FullName, ".jpg"))));
            }

            // Older ones are deleted by the cleanup service
            return renders.OrderByDescending(x => x.CreatedAtUtc).Take(MaxRendersPerEntry).ToList();
        }

        private static RenderInfo? ReadInfo(string path)
        {
            try
            {
                var info = JsonSerializer.Deserialize<RenderInfo>(File.ReadAllText(path), JsonOptions);
                return info?.Layout is null ? null : info with { CreatedAtUtc = DateTime.SpecifyKind(info.CreatedAtUtc, DateTimeKind.Utc) };
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Starts rendering in the background. The layout must already be validated.</summary>
        public void Start(int entryId, string sourcePath, double clipLengthSeconds, ShortformLayout layout)
        {
            var job = new Job { Cancellation = new CancellationTokenSource(), Layout = layout };
            if (_jobs.TryRemove(entryId, out var previous))
                Cancel(previous);
            _jobs[entryId] = job;

            _ = Task.Run(() => RunAsync(entryId, job, sourcePath, clipLengthSeconds, layout));
        }

        /// <summary>Deletes one render. Returns false if it doesn't exist.</summary>
        public bool DeleteRender(int entryId, string renderId)
        {
            if (RenderPath(entryId, renderId, "json") is not { } json || !File.Exists(json))
                return false;

            // Metadata last: a render whose video couldn't be deleted (open in a download) stays listed and can be retried
            var deleted = _storage.TryDelete(RenderPath(entryId, renderId, "mp4"));
            _storage.TryDelete(RenderPath(entryId, renderId, "jpg"));
            if (deleted)
                _storage.TryDelete(json);
            return true;
        }

        /// <summary>Cancels a render and deletes all of the entry's renders (used when an entry is deleted).</summary>
        public void Delete(int entryId)
        {
            if (_jobs.TryRemove(entryId, out var job))
                Cancel(job);

            var folder = EntryFolder(entryId);
            if (!Directory.Exists(folder))
                return;

            foreach (var file in Directory.EnumerateFiles(folder))
                _storage.TryDelete(file);

            TryDeleteEmptyFolder(folder);
        }

        /// <summary>
        /// Deletes expired renders, renders beyond the newest <see cref="MaxRendersPerEntry"/>, leftovers of interrupted
        /// renders, and folders of entries that no longer exist. Called by the cleanup service.
        /// </summary>
        public void Cleanup(IReadOnlySet<int> existingEntryIds)
        {
            var root = _storage.Resolve(LibraryStorage.ShortformFolderName);
            if (!Directory.Exists(root))
                return;

            // Single-file renders from before renders were kept per entry ("shortform/{entryId}.mp4" / ".json")
            foreach (var file in new DirectoryInfo(root).EnumerateFiles())
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(file.Name), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    _storage.TryDelete(file.FullName);
            }

            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (!int.TryParse(folder.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var entryId)
                    || (_jobs.TryGetValue(entryId, out var job) && job.Status == ShortformStatus.Rendering))
                    continue;

                if (!existingEntryIds.Contains(entryId))
                {
                    Delete(entryId);
                    continue;
                }

                var keep = ListRenders(entryId).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var file in folder.EnumerateFiles())
                {
                    // Anything not belonging to a kept render: expired or old renders, "*.part.mp4" from a crash
                    var id = file.Name.Split('.')[0];
                    if (!keep.Contains(id) && DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromMinutes(10) && _storage.TryDelete(file.FullName))
                        _logger.LogInformation("Deleted old portrait render file {Path}.", file.FullName);
                }

                TryDeleteEmptyFolder(folder.FullName);
            }
        }

        private void TryDeleteEmptyFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not delete {Folder}.", folder);
            }
        }

        private static void Cancel(Job job)
        {
            try
            {
                job.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RunAsync(int entryId, Job job, string sourcePath, double clipLengthSeconds, ShortformLayout layout)
        {
            var token = job.Cancellation.Token;
            var renderId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            var output = RenderPath(entryId, renderId, "mp4")!;
            var temp = RenderPath(entryId, renderId, "part.mp4")!;
            var thumbnail = RenderPath(entryId, renderId, "jpg")!;
            var acquired = false;
            try
            {
                await _gate.WaitAsync(token);
                acquired = true;
                Directory.CreateDirectory(EntryFolder(entryId));

                var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
                string[] args =
                [
                    "-hide_banner", "-y", "-nostats", "-progress", "pipe:1",
                    "-i", sourcePath,
                    "-filter_complex", layout.BuildFilterGraph(),
                    "-map", "[v]", "-map", "0:a?",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                    "-c:a", "copy",
                    "-movflags", "+faststart",
                    temp
                ];

                await FfmpegRunner.RunAsync(ffmpegPath, args, EntryFolder(entryId), _logger, token, line =>
                {
                    // "out_time_us=12345678" (also printed as out_time_ms, which is microseconds too)
                    if (clipLengthSeconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                        job.Progress = Math.Clamp(us / 1_000_000.0 / clipLengthSeconds, 0, 0.99);
                });

                File.Move(temp, output, overwrite: true);
                await TryMakeThumbnailAsync(ffmpegPath, output, thumbnail, clipLengthSeconds, token);

                // Written last: a render only shows up once its metadata exists
                var info = new RenderInfo(DateTime.UtcNow, layout);
                await File.WriteAllTextAsync(RenderPath(entryId, renderId, "json")!, JsonSerializer.Serialize(info, JsonOptions), CancellationToken.None);

                _jobs.TryRemove(new KeyValuePair<int, Job>(entryId, job));
                await AuditAsync(entryId, AuditActions.ShortformRendered, $"{renderId} ({(layout.IsSplit ? "split" : "single")})");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Replaced by a newer render or the entry was deleted
                _storage.TryDelete(output);
                _storage.TryDelete(thumbnail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Portrait render failed for entry {EntryId}.", entryId);
                job.Status = ShortformStatus.Failed;
                job.Error = "Rendering the portrait video failed. Check the server log for the ffmpeg error.";
                _storage.TryDelete(output);
                _storage.TryDelete(thumbnail);
                await AuditAsync(entryId, AuditActions.ShortformFailed, ex.Message);
            }
            finally
            {
                if (acquired)
                    _gate.Release();
                _storage.TryDelete(temp);
                job.Cancellation.Dispose();
            }
        }

        /// <summary>A small portrait still from the middle of the render for the page's list. Optional: failures are only logged.</summary>
        private async Task TryMakeThumbnailAsync(string ffmpegPath, string video, string thumbnail, double clipLengthSeconds, CancellationToken token)
        {
            var at = (clipLengthSeconds > 2 ? clipLengthSeconds / 2 : 0).ToString("0.###", CultureInfo.InvariantCulture);
            try
            {
                await FfmpegRunner.RunAsync(ffmpegPath,
                    ["-hide_banner", "-y", "-ss", at, "-i", video, "-frames:v", "1", "-vf", "scale=180:320", "-q:v", "4", thumbnail],
                    Path.GetDirectoryName(video)!, _logger, token);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Could not make a thumbnail for {Path}.", video);
            }
        }

        private async Task AuditAsync(int entryId, string action, string details)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                db.Audit(action, entryId, details);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write the {Action} audit event for entry {EntryId}.", action, entryId);
            }
        }
    }
}
