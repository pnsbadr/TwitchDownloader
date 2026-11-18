using System.Threading.Channels;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.ErrorReports
{
    /// <summary>
    /// Reports waiting to be saved by <see cref="ErrorReportWriter"/>. Logging must never wait or fail, so a full queue drops new reports,
    /// and an error storm is capped per minute (the rest are counted and saved as one "… more errors" report).
    /// </summary>
    public sealed class ErrorReportSink
    {
        public const int MaxPerMinute = 120;

        private readonly Channel<ErrorReport> _channel = Channel.CreateBounded<ErrorReport>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        private readonly Lock _lock = new();
        private DateTime _windowStart = DateTime.MinValue;
        private int _inWindow;
        private int _dropped;

        public ChannelReader<ErrorReport> Reader => _channel.Reader;

        public bool Add(ErrorReport report)
        {
            ErrorReport? droppedNote = null;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (now - _windowStart >= TimeSpan.FromMinutes(1))
                {
                    if (_dropped > 0)
                        droppedNote = new ErrorReport
                        {
                            CreatedAtUtc = now, Kind = ErrorReportKind.ServerError, Area = "Error reports",
                            Message = $"{_dropped} more errors in one minute weren't saved (more than {MaxPerMinute} a minute).",
                            AppVersion = ReportScrubber.AppVersion,
                        };
                    _windowStart = now;
                    _inWindow = 0;
                    _dropped = 0;
                }
                if (_inWindow >= MaxPerMinute)
                {
                    _dropped++;
                    return false;
                }
                _inWindow++;
            }
            if (droppedNote is not null)
            {
                droppedNote.Fingerprint = ReportScrubber.Fingerprint(droppedNote.Kind, null, droppedNote.Area, droppedNote.Message);
                _channel.Writer.TryWrite(droppedNote);
            }
            if (string.IsNullOrEmpty(report.Fingerprint))
                report.Fingerprint = ReportScrubber.Fingerprint(report.Kind, report.ErrorType, report.Area, report.Message);
            report.AppVersion ??= ReportScrubber.AppVersion;
            return _channel.Writer.TryWrite(report);
        }
    }
}
