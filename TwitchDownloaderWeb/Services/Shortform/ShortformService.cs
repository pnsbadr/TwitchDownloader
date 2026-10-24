using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services.Shortform
{
    public enum ShortformStatus
    {
        None,
        Rendering,
        Ready,
        Failed
    }

    public sealed record ShortformState(ShortformStatus Status, double Progress, string? Error, long? FileSizeBytes, ShortformLayout? Layout);

    /// <summary>
    /// Renders an entry's finished file into a 9:16 portrait video ("shortform/{id}.mp4") using a crop layout from the
    /// page. One render runs at a time; a new render for the same entry replaces a running one. The last layout is saved
    /// next to the file ("shortform/{id}.json") so the editor reopens where the user left off. Job state is in memory:
    /// after a restart an entry shows Ready if its file exists, otherwise None.
    /// </summary>
    public sealed class ShortformService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly LibraryStorage _storage;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ShortformService> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<int, Job> _jobs = new();

        public ShortformService(LibraryStorage storage, IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<ShortformService> logger)
        {
            _storage = storage;
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        private sealed class Job
        {
            public required CancellationTokenSource Cancellation { get; init; }
            public ShortformStatus Status { get; set; } = ShortformStatus.Rendering;
            public double Progress { get; set; }
            public string? Error { get; set; }
        }

        public ShortformState GetState(int entryId)
        {
            var layout = LoadLayout(entryId);
            if (_jobs.TryGetValue(entryId, out var job) && job.Status != ShortformStatus.Ready)
                return new ShortformState(job.Status, job.Progress, job.Error, null, layout);

            var file = new FileInfo(FilePath(entryId));
            return file.Exists
                ? new ShortformState(ShortformStatus.Ready, 1, null, file.Length, layout)
                : new ShortformState(ShortformStatus.None, 0, null, null, layout);
        }

        public string FilePath(int entryId) => _storage.Resolve(LibraryStorage.ShortformRelativePath(entryId, "mp4"));

        private string LayoutPath(int entryId) => _storage.Resolve(LibraryStorage.ShortformRelativePath(entryId, "json"));

        private ShortformLayout? LoadLayout(int entryId)
        {
            try
            {
                var path = LayoutPath(entryId);
                return File.Exists(path) ? JsonSerializer.Deserialize<ShortformLayout>(File.ReadAllText(path), JsonOptions) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return null;
            }
        }

        /// <summary>Starts rendering in the background. The layout must already be validated.</summary>
        public void Start(int entryId, string sourcePath, double clipLengthSeconds, ShortformLayout layout)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath(entryId))!);
            File.WriteAllText(LayoutPath(entryId), JsonSerializer.Serialize(layout, JsonOptions));

            var job = new Job { Cancellation = new CancellationTokenSource() };
            if (_jobs.TryRemove(entryId, out var previous))
                Cancel(previous);
            _jobs[entryId] = job;

            _ = Task.Run(() => RunAsync(entryId, job, sourcePath, clipLengthSeconds, layout));
        }

        /// <summary>Cancels a render and deletes the entry's portrait file and layout (used when an entry is deleted).</summary>
        public void Delete(int entryId)
        {
            if (_jobs.TryRemove(entryId, out var job))
                Cancel(job);

            // A cancelled ffmpeg may still hold the temp file briefly; the render cleans it up when it stops
            _storage.TryDelete(FilePath(entryId));
            _storage.TryDelete(LayoutPath(entryId));
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
            var output = FilePath(entryId);
            var temp = output + ".part.mp4";
            var acquired = false;
            try
            {
                await _gate.WaitAsync(token);
                acquired = true;

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

                await FfmpegRunner.RunAsync(ffmpegPath, args, Path.GetDirectoryName(output)!, _logger, token, line =>
                {
                    // "out_time_us=12345678" (also printed as out_time_ms, which is microseconds too)
                    if (clipLengthSeconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                        job.Progress = Math.Clamp(us / 1_000_000.0 / clipLengthSeconds, 0, 0.99);
                });

                File.Move(temp, output, overwrite: true);
                job.Progress = 1;
                job.Status = ShortformStatus.Ready;
                await AuditAsync(entryId, AuditActions.ShortformRendered, layout.IsSplit ? "split" : "single");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Replaced by a newer render or the entry was deleted
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Portrait render failed for entry {EntryId}.", entryId);
                job.Status = ShortformStatus.Failed;
                job.Error = "Rendering the portrait video failed. Check the server log for the ffmpeg error.";
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
