using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Segments;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Makes the file of something with cut-out parts (<see cref="CutOuts"/>): each kept part is cut like any range (<see cref="IVodCutter"/>,
    /// Exact so the parts meet on the right frame, saved clean audio swapped in), then the parts are joined (<see cref="SegmentJoiner"/>).
    /// With one part it is just that cut, in the item's own trim mode.
    /// </summary>
    public sealed class KeptPartsCutter
    {
        private readonly IVodCutter _cutter;
        private readonly LibraryStorage _storage;
        private readonly IConfiguration _configuration;
        private readonly IOptions<DownloadSettings> _settings;

        public KeptPartsCutter(IVodCutter cutter, LibraryStorage storage, IConfiguration configuration, IOptions<DownloadSettings> settings)
        {
            _cutter = cutter;
            _storage = storage;
            _configuration = configuration;
            _settings = settings;
        }

        /// <param name="cut">VodId, Quality, TrimMode, StartTime and EndTime (and Id/TeamId for logs) are used; it is never changed.</param>
        /// <param name="cuts">The cut-out parts; normalized against the cut's range here.</param>
        /// <param name="vodLengthSeconds">Where an open end (EndTime null) stops.</param>
        public async Task<AudioRestoreResult?> CutAsync(LibraryDbContext db, LibraryEntry cut, IReadOnlyList<CutRange> cuts, double vodLengthSeconds,
            string outputPath, ILogger logger, Action<string> onRestoreFailed, CancellationToken cancellationToken)
        {
            var start = cut.StartTime.TotalSeconds;
            var end = cut.EndTime?.TotalSeconds ?? vodLengthSeconds;
            var parts = CutOuts.Kept(start, end, cuts);
            if (parts.Count == 0)
                throw new InvalidOperationException("The cut-out parts leave nothing to download.");

            if (parts.Count == 1 && parts[0].Start == start && parts[0].End == end)
                return await _cutter.CutAsync(db, cut, outputPath, logger, onRestoreFailed, cancellationToken);

            if (parts.Count == 1)
                return await _cutter.CutAsync(db, Part(cut, parts[0]), outputPath, logger, onRestoreFailed, cancellationToken);

            var work = _storage.CreateWorkFolder($"parts-{cut.Id}");
            try
            {
                var files = new List<string>();
                var restores = new List<AudioRestoreResult>();
                var outerPrefix = JobProgress.Current?.Prefix;
                for (var i = 0; i < parts.Count; i++)
                {
                    var file = Path.Combine(work, $"{i + 1}.mp4");
                    var name = $"Kept part {i + 1} of {parts.Count}";
                    JobProgress.SetPrefix(outerPrefix is null ? name : $"{outerPrefix} · {name}");
                    logger.LogInformation("{Part} ({Start}-{End}).", name, JobProgress.FormatTime(parts[i].Start), JobProgress.FormatTime(parts[i].End));
                    try
                    {
                        if (await _cutter.CutAsync(db, Part(cut, parts[i]), file, logger, onRestoreFailed, cancellationToken) is { } restore)
                            restores.Add(restore);
                    }
                    finally
                    {
                        JobProgress.SetPrefix(outerPrefix);
                    }
                    files.Add(file);
                }

                var settings = _settings.Value;
                var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
                await SegmentJoiner.JoinAsync(ffmpegPath, files, outputPath, work, settings.ExactTrimPreset, settings.ExactTrimCrf, logger, cancellationToken);

                return restores.Count == 0
                    ? null
                    : new AudioRestoreResult(restores.Sum(x => x.RestoredSeconds), restores.Sum(x => x.StillMutedSeconds), restores.Any(x => x.Downloaded));
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Couldn't delete {Path}: {Message}; it is cleared at the next start.", work, ex.Message);
                }
            }
        }

        /// <summary>A transient copy of the cut for one kept part (never saved).</summary>
        private static LibraryEntry Part(LibraryEntry cut, (double Start, double End) part) => new()
        {
            Id = cut.Id,
            TeamId = cut.TeamId,
            Title = cut.Title,
            VodId = cut.VodId,
            Quality = cut.Quality,
            TrimMode = VideoTrimMode.Exact,
            StartTime = TimeSpan.FromSeconds(part.Start),
            EndTime = TimeSpan.FromSeconds(part.End),
        };
    }
}
