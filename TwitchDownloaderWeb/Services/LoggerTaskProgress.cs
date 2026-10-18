using System.Runtime.CompilerServices;
using TwitchDownloaderCore.Interfaces;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Forwards TwitchDownloaderCore progress and log messages to an <see cref="ILogger"/>.
    /// Progress percentages are only logged when they cross a 10% step to avoid flooding the log.
    /// </summary>
    public sealed class LoggerTaskProgress : ITaskProgress
    {
        private readonly ILogger _logger;
        private string? _statusTemplate;
        private int _lastLoggedStep = -1;

        public LoggerTaskProgress(ILogger logger)
        {
            _logger = logger;
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

            var step = percent / 10;
            if (step <= _lastLoggedStep)
                return;

            _lastLoggedStep = step;
            _logger.LogInformation("{Status}", string.Format(_statusTemplate, percent, time1, time2));
        }
    }
}
