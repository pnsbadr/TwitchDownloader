using Microsoft.Extensions.Options;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services
{
    public enum ClipFileSource
    {
        /// <summary>Twitch's own clip file, via Core's <see cref="ClipDownloader"/>.</summary>
        Clip,

        /// <summary>Cut from the source VOD with saved clean audio in place of muted parts.</summary>
        VodRestored,

        /// <summary>Cut from the source VOD because the clip itself is gone.</summary>
        Vod,

        /// <summary>Neither the clip nor its VOD is on Twitch any more; no file was made.</summary>
        Gone
    }

    /// <param name="Restore">The audio restore attempt, if the source VOD has an audio rescue; reports muted seconds even when the clip file was used.</param>
    public sealed record ClipFileResult(ClipFileSource Source, AudioRestoreResult? Restore);

    /// <summary>
    /// Makes the on-demand file of a clip entry. Twitch mutes a clip wherever its VOD is muted, so when we saved clean audio for
    /// muted parts of the clip's range the file is cut from the VOD (with <see cref="AudioRestoreService"/>) instead of
    /// fetched from Twitch. Otherwise it is Twitch's clip file, untouched.
    /// </summary>
    public sealed class ClipFileService
    {
        private readonly ClipInfoService _clips;
        private readonly VodInfoService _vodInfo;
        private readonly AudioRestoreService _restore;
        private readonly ExactTrimService _exactTrim;
        private readonly IConfiguration _configuration;
        private readonly IOptions<DownloadSettings> _settings;

        public ClipFileService(ClipInfoService clips, VodInfoService vodInfo, AudioRestoreService restore, ExactTrimService exactTrim,
            IConfiguration configuration, IOptions<DownloadSettings> settings)
        {
            _clips = clips;
            _vodInfo = vodInfo;
            _restore = restore;
            _exactTrim = exactTrim;
            _configuration = configuration;
            _settings = settings;
        }

        public async Task<ClipFileResult> MakeFileAsync(LibraryDbContext db, LibraryEntry entry, string outputPath, ILogger logger, CancellationToken cancellationToken)
        {
            var clipExists = await _clips.GetClipAsync(entry.ClipSlug!) is not null;
            var vodCut = entry.VodId > 0 && await _vodInfo.GetVodAsync(entry.VodId) is not null ? AsVodCut(entry) : null;

            AudioRestoreResult? restore = null;
            if (vodCut is not null)
            {
                try
                {
                    restore = await _restore.TryDownloadRestoredAsync(db, vodCut, outputPath, logger, cancellationToken);
                    if (restore is { Downloaded: true })
                        return new ClipFileResult(ClipFileSource.VodRestored, restore);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Restoring audio for clip entry {EntryId} failed; using the clip file.", entry.Id);
                    db.Audit(AuditActions.AudioRestoreFailed, entry.Id, ex.Message);
                }
            }

            if (clipExists)
            {
                await DownloadClipAsync(entry, outputPath, logger, cancellationToken);
                return new ClipFileResult(ClipFileSource.Clip, restore);
            }

            if (vodCut is not null)
            {
                await _exactTrim.DownloadAsync(vodCut, outputPath, logger, cancellationToken);
                return new ClipFileResult(ClipFileSource.Vod, restore);
            }

            return new ClipFileResult(ClipFileSource.Gone, null);
        }

        /// <summary>
        /// The clip's range as a VOD cut, for the VOD download services. Not saved. The clip's quality where the VOD has it
        /// (else the best, as Core does); Exact, so the file is the clip's length rather than whole segments.
        /// </summary>
        private static LibraryEntry AsVodCut(LibraryEntry clip) => new()
        {
            Id = clip.Id,
            Title = clip.Title,
            VodId = clip.VodId,
            Quality = clip.Quality,
            TrimMode = VideoTrimMode.Exact,
            StartTime = clip.StartTime,
            EndTime = clip.EndTime,
        };

        private async Task DownloadClipAsync(LibraryEntry entry, string outputPath, ILogger logger, CancellationToken cancellationToken)
        {
            var settings = _settings.Value;
            var options = new ClipDownloadOptions
            {
                Id = entry.ClipSlug!,
                Quality = entry.Quality,
                Filename = outputPath,
                ThrottleKib = -1,
                TempFolder = string.IsNullOrWhiteSpace(settings.TempFolder) ? null : settings.TempFolder,
                // Twitch's file as is; metadata would mean another ffmpeg pass for a chapter track nobody uses
                EncodeMetadata = false,
                FfmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } ffmpegPath ? ffmpegPath : "ffmpeg",
                // We own this path, so overwrite any leftover from an earlier attempt
                FileCollisionCallback = info => info,
            };

            await new ClipDownloader(options, new LoggerTaskProgress(logger)).DownloadAsync(cancellationToken);
        }
    }
}
