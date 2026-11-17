using System.Runtime.CompilerServices;
using TwitchDownloaderCore.Interfaces;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Forwards TwitchDownloaderCore progress and log messages to an <see cref="ILogger"/>.
    /// Progress percentages are only logged when they cross a 10% step to avoid flooding the log, with the time left in the step.
    /// Steps and percents also go to the ambient <see cref="JobProgress"/> (the export card's step), in plain words.
    /// </summary>
    public sealed class LoggerTaskProgress : ITaskProgress
    {
        private readonly ILogger _logger;
        private string? _statusTemplate;
        private int _lastLoggedStep = -1;
        private DateTime _stepStartedUtc = DateTime.UtcNow;
        // Captured here: Core reports from its own tasks and threads
        private readonly JobProgress? _job = JobProgress.Current;

        public LoggerTaskProgress(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>Core's status ("Downloading {0}% [2/4]") in the page's words.</summary>
        public static string StepName(string status)
        {
            var text = status.Replace("{0}%", "").Replace("{0}", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\{\d+[^}]*\}", "").Trim();
            return text switch
            {
                _ when text.StartsWith("Fetching Video Info", StringComparison.Ordinal) => "Reading the VOD from Twitch",
                _ when text.StartsWith("Downloading", StringComparison.Ordinal) => "Downloading from Twitch",
                _ when text.StartsWith("Verifying Parts", StringComparison.Ordinal) => "Checking the downloaded parts",
                _ when text.StartsWith("Redownloading Missing Parts", StringComparison.Ordinal) => "Downloading missing parts again",
                _ when text.StartsWith("Finalizing Video", StringComparison.Ordinal) => "Joining the downloaded parts",
                _ => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " "),
            };
        }

        public void LogVerbose(string logMessage) => _logger.LogDebug("{Message}", logMessage);

        public void LogVerbose(DefaultInterpolatedStringHandler logMessage) => LogVerbose(logMessage.ToStringAndClear());

        public void LogInfo(string logMessage) => _logger.LogInformation("{Message}", logMessage);

        public void LogInfo(DefaultInterpolatedStringHandler logMessage) => LogInfo(logMessage.ToStringAndClear());

        public void LogWarning(string logMessage) => _logger.LogWarning("{Message}", logMessage);

        public void LogWarning(DefaultInterpolatedStringHandler logMessage) => LogWarning(logMessage.ToStringAndClear());

        public void LogError(string logMessage) => _logger.LogError("{Message}", logMessage);

        public void LogError(DefaultInterpolatedStringHandler logMessage) => LogError(logMessage.ToStringAndClear());

        public void LogFfmpeg(string logMessage) => _logger.LogTrace("[ffmpeg] {Message}", logMessage);

        public void SetStatus(string status)
        {
            _statusTemplate = null;
            _job?.SetStep(StepName(status));
            _logger.LogInformation("{Status}", status);
        }

        public void SetTemplateStatus(string statusTemplate, int initialPercent)
        {
            SetTemplateStatus(statusTemplate, initialPercent, TimeSpan.Zero, TimeSpan.Zero);
        }

        public void SetTemplateStatus(string statusTemplate, int initialPercent, TimeSpan initialTime1, TimeSpan initialTime2)
        {
            _statusTemplate = statusTemplate;
            _lastLoggedStep = -1;
            _stepStartedUtc = DateTime.UtcNow;
            _job?.SetStep(StepName(statusTemplate));
            ReportProgress(initialPercent, initialTime1, initialTime2);
        }

        public void ReportProgress(int percent)
        {
            ReportProgress(percent, TimeSpan.Zero, TimeSpan.Zero);
        }

        public void ReportProgress(int percent, TimeSpan time1, TimeSpan time2)
        {
            if (_statusTemplate is null)
                return;

            _job?.Report(percent);
            var step = percent / 10;
            if (step <= _lastLoggedStep)
                return;

            _lastLoggedStep = step;
            var left = JobProgress.EstimateLeft(percent, DateTime.UtcNow - _stepStartedUtc);
            _logger.LogInformation("{Status}{Left}", string.Format(_statusTemplate, percent, time1, time2),
                left is { } l ? $" ({JobProgress.FormatLeft(l)})" : "");
        }
    }
}
