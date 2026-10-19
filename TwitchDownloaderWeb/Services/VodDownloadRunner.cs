using Microsoft.Extensions.Options;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>Builds <see cref="VideoDownloadOptions"/> from app configuration and runs Core's <see cref="VideoDownloader"/>.</summary>
    public sealed class VodDownloadRunner
    {
        private readonly IConfiguration _configuration;
        private readonly IOptions<DownloadSettings> _settings;

        public VodDownloadRunner(IConfiguration configuration, IOptions<DownloadSettings> settings)
        {
            _configuration = configuration;
            _settings = settings;
        }

        /// <param name="fileCollisionCallback">Called if <paramref name="outputPath"/> already exists; returns the file to write instead.</param>
        /// <returns>The full path of the file that was written.</returns>
        public async Task<string> DownloadAsync(long vodId, string? quality, VideoTrimMode trimMode, TimeSpan startTime, TimeSpan? endTime,
            string outputPath, Func<FileInfo, FileInfo> fileCollisionCallback, ILogger logger, CancellationToken cancellationToken)
        {
            var settings = _settings.Value;
            var downloadOptions = new VideoDownloadOptions
            {
                Id = vodId,
                Quality = quality,
                Filename = outputPath,
                TrimBeginning = startTime > TimeSpan.Zero,
                TrimBeginningTime = startTime,
                TrimEnding = endTime.HasValue,
                TrimEndingTime = endTime ?? TimeSpan.Zero,
                TrimMode = trimMode,
                DownloadThreads = Math.Max(1, settings.Threads),
                ThrottleKib = -1,
                FfmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } ffmpegPath ? ffmpegPath : "ffmpeg",
                TempFolder = string.IsNullOrWhiteSpace(settings.TempFolder) ? null : settings.TempFolder,
                CacheCleanerCallback = _ => [],
                FileCollisionCallback = fileCollisionCallback,
            };

            var downloader = new VideoDownloader(downloadOptions, new LoggerTaskProgress(logger));
            await downloader.DownloadAsync(cancellationToken);

            // Core updates Filename if the collision callback picked a different file
            return downloadOptions.Filename;
        }
    }
}
