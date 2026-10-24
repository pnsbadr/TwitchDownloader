using Microsoft.Extensions.Options;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Where library files live. Paths saved in the database are relative to Downloads:OutputFolder, so the folder can move.
    /// Stored clips go in "library/{id}.mp4" and on-demand files in "generated/{id}.mp4"; naming by entry ID avoids
    /// collisions between entries with the same title. The title is used as the file name when serving the file.
    /// </summary>
    public sealed class LibraryStorage
    {
        public const string StoredFolderName = "library";
        public const string GeneratedFolderName = "generated";
        public const string RescueFolderName = "rescue";
        public const string WorkFolderName = "work";

        private readonly ILogger<LibraryStorage> _logger;

        public LibraryStorage(IOptions<DownloadSettings> settings, IWebHostEnvironment environment, ILogger<LibraryStorage> logger)
        {
            _logger = logger;
            RootFolder = Path.GetFullPath(settings.Value.OutputFolder, environment.ContentRootPath);
        }

        public string RootFolder { get; }

        public string StoredFolder => Path.Combine(RootFolder, StoredFolderName);

        public string GeneratedFolder => Path.Combine(RootFolder, GeneratedFolderName);

        public static string StoredRelativePath(int entryId) => Path.Combine(StoredFolderName, $"{entryId}.mp4");

        public static string GeneratedRelativePath(int entryId) => Path.Combine(GeneratedFolderName, $"{entryId}.mp4");

        /// <summary>Rescued clean audio for a VOD segment. Kept until pruned, independent of library entries.</summary>
        public static string RescuedSegmentRelativePath(long vodId, string segmentKey) => Path.Combine(RescueFolderName, vodId.ToString(), $"{segmentKey}.ts");

        public string RescueVodFolder(long vodId) => Path.Combine(RootFolder, RescueFolderName, vodId.ToString());

        /// <summary>A fresh scratch folder for one job (e.g. an audio restore). The caller deletes it.</summary>
        public string CreateWorkFolder(string name)
        {
            var path = Path.Combine(RootFolder, WorkFolderName, $"{name}-{DateTime.UtcNow.Ticks}");
            Directory.CreateDirectory(path);
            return path;
        }

        public string Resolve(string relativePath) => Path.GetFullPath(relativePath, RootFolder);

        /// <summary>Deletes a file if it exists. Failures (e.g. the file is open) are logged and left for the cleanup sweep.</summary>
        public bool TryDelete(string? relativeOrFullPath)
        {
            if (string.IsNullOrEmpty(relativeOrFullPath))
                return true;

            var fullPath = Resolve(relativeOrFullPath);
            try
            {
                File.Delete(fullPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete {Path}; the cleanup sweep will retry.", fullPath);
                return false;
            }
        }
    }
}
