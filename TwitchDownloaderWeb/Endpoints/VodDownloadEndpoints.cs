using Microsoft.Extensions.Options;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Interfaces;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="VodId">A VOD ID or twitch.tv/videos URL.</param>
    /// <param name="Title">Output file name (without extension). Blank uses the VOD's Twitch title.</param>
    /// <param name="Quality">e.g. "1080p60", "720p30". Blank or unknown uses the best available quality.</param>
    /// <param name="StartTime">Trim start, as an offset from the start of the VOD ("hh:mm:ss").</param>
    /// <param name="EndTime">Trim end, as an offset from the start of the VOD ("hh:mm:ss"), not a duration.</param>
    public sealed record VodDownloadRequest(string VodId, string? Title, string? Quality, TimeSpan? StartTime, TimeSpan? EndTime);

    public sealed record VodDownloadResponse(string FileName, string Path, long SizeBytes);

    public static class VodDownloadEndpoints
    {
        public static IEndpointRouteBuilder MapVodDownloadEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/vods/download", DownloadVod);
            return app;
        }

        private static async Task<IResult> DownloadVod(VodDownloadRequest request, IConfiguration configuration, IOptions<DownloadSettings> settings,
            IWebHostEnvironment environment, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var logger = loggerFactory.CreateLogger("TwitchDownloaderWeb.VodDownload");

            var idMatch = IdParse.MatchVideoId(request.VodId ?? "");
            if (idMatch is not { Success: true })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vodId"] = ["Unable to parse VOD ID/URL."] });

            var vodId = long.Parse(idMatch.ValueSpan);
            var startTime = request.StartTime ?? TimeSpan.Zero;
            var endTime = request.EndTime;

            if (startTime < TimeSpan.Zero)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startTime"] = ["Start time cannot be negative."] });

            if (endTime is { } end && end <= startTime)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endTime"] = ["End time must be after start time."] });

            if (request.Quality is { } quality && quality.Contains("audio", StringComparison.OrdinalIgnoreCase))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["quality"] = ["Audio-only quality cannot produce an mp4."] });

            var videoInfo = (await TwitchHelper.GetVideoInfo(vodId)).data.video;
            if (videoInfo is null)
                return Results.NotFound(new { error = $"VOD {vodId} was not found. It may be deleted, expired or private." });

            var vodLength = TimeSpan.FromSeconds(videoInfo.lengthSeconds);
            if (startTime >= vodLength)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startTime"] = [$"Start time is past the end of the VOD ({vodLength})."] });

            if (endTime > vodLength)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endTime"] = [$"End time is past the end of the VOD ({vodLength})."] });

            // Core silently falls back to the best quality when the requested one doesn't exist, so check it up front
            if (!string.IsNullOrWhiteSpace(request.Quality))
            {
                var available = await GetAvailableQualities(vodId);
                if (available is null)
                    return Results.Problem(detail: "This VOD's playlist is restricted (e.g. subscriber-only), which is not supported yet.", statusCode: StatusCodes.Status403Forbidden);

                if (available.GetQuality(request.Quality) is null)
                {
                    var names = available.Qualities.Select(x => x.Name).Where(x => !x.Contains("audio", StringComparison.OrdinalIgnoreCase));
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["quality"] = [$"Quality \"{request.Quality}\" is not available. Available: {string.Join(", ", names)}."] });
                }
            }

            var title = string.IsNullOrWhiteSpace(request.Title) ? videoInfo.title : request.Title;
            var outputFolder = Path.GetFullPath(settings.Value.OutputFolder, environment.ContentRootPath);
            var outputPath = Path.Combine(outputFolder, GetSafeFileName(title, vodId) + ".mp4");
            Directory.CreateDirectory(outputFolder);

            var downloadOptions = new VideoDownloadOptions
            {
                Id = vodId,
                Quality = request.Quality,
                Filename = outputPath,
                TrimBeginning = startTime > TimeSpan.Zero,
                TrimBeginningTime = startTime,
                TrimEnding = endTime.HasValue,
                TrimEndingTime = endTime ?? TimeSpan.Zero,
                TrimMode = VideoTrimMode.Exact,
                DownloadThreads = Math.Max(1, settings.Value.Threads),
                ThrottleKib = -1,
                FfmpegPath = configuration["Ffmpeg:Path"] is { Length: > 0 } ffmpegPath ? ffmpegPath : "ffmpeg",
                TempFolder = string.IsNullOrWhiteSpace(settings.Value.TempFolder) ? null : settings.Value.TempFolder,
                CacheCleanerCallback = _ => [],
                FileCollisionCallback = FilenameService.GetNonCollidingName,
            };

            logger.LogInformation("Downloading VOD {VodId} ({Start} - {End}, quality {Quality}) to {Path}",
                vodId, startTime, endTime?.ToString() ?? "end", request.Quality ?? "best", outputPath);

            try
            {
                var downloader = new VideoDownloader(downloadOptions, new LoggerTaskProgress(logger));
                await downloader.DownloadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Download of VOD {VodId} was cancelled by the client.", vodId);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download of VOD {VodId} failed.", vodId);
                return Results.Problem(title: "Download failed.", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            // Core may have renamed the file to avoid a collision
            var outputFile = new FileInfo(downloadOptions.Filename);
            return Results.Ok(new VodDownloadResponse(outputFile.Name, outputFile.FullName, outputFile.Length));
        }

        /// <returns>The VOD's qualities, or null if the playlist is restricted.</returns>
        private static async Task<IVideoQualities<M3U8.Stream>?> GetAvailableQualities(long vodId)
        {
            var accessToken = (await TwitchHelper.GetVideoToken(vodId, null)).data.videoPlaybackAccessToken;
            if (accessToken is null)
                return null;

            var playlist = await TwitchHelper.GetVideoPlaylist(vodId, accessToken.value, accessToken.signature);
            if (playlist.Contains("vod_manifest_restricted") || playlist.Contains("unauthorized_entitlements"))
                return null;

            return VideoQualities.FromM3U8(M3U8.Parse(playlist));
        }

        private static string GetSafeFileName(string? title, long vodId)
        {
            // Windows silently drops trailing dots and spaces, which could leave "" or ".." as the name
            var fileName = FilenameService.ReplaceInvalidFilenameChars(title)?.Trim().TrimEnd('.');
            return string.IsNullOrEmpty(fileName) ? vodId.ToString() : fileName;
        }
    }
}
