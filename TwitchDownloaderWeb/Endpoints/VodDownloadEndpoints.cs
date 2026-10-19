using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Services;
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

    /// <summary>
    /// Phase 1 dev endpoint: downloads synchronously, outside the library, into Downloads:OutputFolder.
    /// The library uses /api/entries and the background queue instead.
    /// </summary>
    public static class VodDownloadEndpoints
    {
        public static IEndpointRouteBuilder MapVodDownloadEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/vods/download", DownloadVod);
            return app;
        }

        private static async Task<IResult> DownloadVod(VodDownloadRequest request, VodInfoService vodInfo, VodDownloadRunner runner, LibraryStorage storage,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var logger = loggerFactory.CreateLogger("TwitchDownloaderWeb.VodDownload");

            if (VodInfoService.ParseVodId(request.VodId) is not { } vodId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vodId"] = ["Unable to parse VOD ID/URL."] });

            var startTime = request.StartTime ?? TimeSpan.Zero;
            var endTime = request.EndTime;

            if (VodInfoService.IsAudioQuality(request.Quality))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["quality"] = ["Audio-only quality cannot produce an mp4."] });

            var vod = await vodInfo.GetVodAsync(vodId);
            if (vod is null)
                return Results.NotFound(new { error = $"VOD {vodId} was not found. It may be deleted, expired or private." });

            if (VodInfoService.ValidateTrim(startTime, endTime, vod.LengthSeconds) is { } trimErrors)
                return Results.ValidationProblem(trimErrors);

            // Core silently falls back to the best quality when the requested one doesn't exist, so check it up front
            if (!string.IsNullOrWhiteSpace(request.Quality))
            {
                var available = await vodInfo.GetQualitiesAsync(vodId);
                if (available is null)
                    return Results.Problem(detail: "This VOD's playlist is restricted (e.g. subscriber-only), which is not supported yet.", statusCode: StatusCodes.Status403Forbidden);

                if (available.GetQuality(request.Quality) is null)
                {
                    var names = VodInfoService.GetVideoQualityNames(available);
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["quality"] = [$"Quality \"{request.Quality}\" is not available. Available: {string.Join(", ", names)}."] });
                }
            }

            var title = string.IsNullOrWhiteSpace(request.Title) ? vod.Title : request.Title;
            var outputPath = Path.Combine(storage.RootFolder, GetSafeFileName(title, vodId) + ".mp4");
            Directory.CreateDirectory(storage.RootFolder);

            logger.LogInformation("Downloading VOD {VodId} ({Start} - {End}, quality {Quality}) to {Path}",
                vodId, startTime, endTime?.ToString() ?? "end", request.Quality ?? "best", outputPath);

            string writtenPath;
            try
            {
                writtenPath = await runner.DownloadAsync(vodId, request.Quality, VideoTrimMode.Exact, startTime, endTime,
                    outputPath, FilenameService.GetNonCollidingName, logger, cancellationToken);
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

            var outputFile = new FileInfo(writtenPath);
            return Results.Ok(new VodDownloadResponse(outputFile.Name, outputFile.FullName, outputFile.Length));
        }

        private static string GetSafeFileName(string? title, long vodId)
        {
            // Windows silently drops trailing dots and spaces, which could leave "" or ".." as the name
            var fileName = FilenameService.ReplaceInvalidFilenameChars(title)?.Trim().TrimEnd('.');
            return string.IsNullOrEmpty(fileName) ? vodId.ToString() : fileName;
        }
    }
}
