using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>Makes the file for a cut of a VOD (<see cref="VodCutService"/> in the app; a fake in tests).</summary>
    public interface IVodCutter
    {
        /// <param name="cut">What to cut: VodId, Quality, TrimMode, StartTime and EndTime are used. Need not be saved (segment downloads aren't entries).</param>
        /// <param name="onRestoreFailed">Called with the error when swapping in saved audio failed and the cut was made without it.</param>
        /// <returns>The audio restore attempt, or null if the VOD has no audio rescue or the attempt failed.</returns>
        Task<AudioRestoreResult?> CutAsync(LibraryDbContext db, LibraryEntry cut, string outputPath, ILogger logger, Action<string> onRestoreFailed,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// The one way a VOD range becomes a file: swaps rescued clean audio into muted parts when we have any, otherwise (or if that fails)
    /// a normal download; Exact cuts are re-encoded so they start on the exact frame (<see cref="ExactTrimService"/>).
    /// Used by library downloads and by segment downloads.
    /// </summary>
    public sealed class VodCutService : IVodCutter
    {
        private readonly AudioRestoreService _restore;
        private readonly ExactTrimService _exactTrim;
        private readonly VodDownloadRunner _runner;

        public VodCutService(AudioRestoreService restore, ExactTrimService exactTrim, VodDownloadRunner runner)
        {
            _restore = restore;
            _exactTrim = exactTrim;
            _runner = runner;
        }

        public async Task<AudioRestoreResult?> CutAsync(LibraryDbContext db, LibraryEntry cut, string outputPath, ILogger logger, Action<string> onRestoreFailed,
            CancellationToken cancellationToken)
        {
            AudioRestoreResult? restore = null;
            try
            {
                restore = await _restore.TryDownloadRestoredAsync(db, cut, outputPath, logger, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Restoring audio for VOD {VodId} ({Start}-{End}) failed; downloading without it.", cut.VodId, cut.StartTime, cut.EndTime);
                onRestoreFailed(ex.Message);
            }

            if (restore is not { Downloaded: true })
            {
                if (cut.TrimMode == VideoTrimMode.Exact)
                    await _exactTrim.DownloadAsync(cut, outputPath, logger, cancellationToken);
                else
                    await _runner.DownloadAsync(cut.VodId, cut.Quality, cut.TrimMode, cut.StartTime, cut.EndTime,
                        outputPath, info => info, logger, cancellationToken);
            }

            return restore;
        }
    }
}
