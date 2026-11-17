namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Where a background job is now, for the page: the step it's on ("Downloading from Twitch", "Re-encoding for the exact cut"), that
    /// step's percent and time left. A job makes one the ambient progress (<see cref="Use"/>, an <see cref="AsyncLocal{T}"/>), so the cut
    /// code deep below (Core's download, ffmpeg runs) reports into it without passing it around. Kept in memory only.
    /// </summary>
    public sealed class JobProgress
    {
        private static readonly AsyncLocal<JobProgress?> Ambient = new();

        private readonly object _lock = new();
        private string? _step;
        private string? _prefix;
        private double? _percent;
        private double? _etaSeconds;
        private DateTime _stepStartedUtc = DateTime.UtcNow;

        /// <summary>The job's progress in this flow, or null when nothing is listening (e.g. a library download).</summary>
        public static JobProgress? Current => Ambient.Value;

        /// <summary>Makes <paramref name="progress"/> the current one in the calling flow until disposed.</summary>
        public static IDisposable Use(JobProgress? progress)
        {
            var previous = Ambient.Value;
            Ambient.Value = progress;
            return new Scope(previous);
        }

        /// <summary>
        /// Put in front of every step until cleared ("Part 2 of 3"), for a job that runs the same steps once per part. Takes effect from the
        /// next step.
        /// </summary>
        public string? Prefix
        {
            get { lock (_lock) return _prefix; }
            set { lock (_lock) _prefix = value; }
        }

        /// <summary>Sets the ambient progress's prefix (see <see cref="Prefix"/>), if there is one.</summary>
        public static void SetPrefix(string? prefix)
        {
            if (Current is { } progress)
                progress.Prefix = prefix;
        }

        /// <summary>Starts a new step: its percent and time left are unknown until reported.</summary>
        public void SetStep(string step)
        {
            lock (_lock)
            {
                _step = _prefix is null ? step : $"{_prefix} · {step}";
                _percent = null;
                _etaSeconds = null;
                _stepStartedUtc = DateTime.UtcNow;
            }
        }

        /// <param name="percent">0–100 of the current step.</param>
        /// <param name="etaSeconds">Time left in the step; null works it out from how long the step has taken so far.</param>
        public void Report(double percent, double? etaSeconds = null)
        {
            percent = Math.Clamp(percent, 0, 100);
            lock (_lock)
            {
                _percent = percent;
                _etaSeconds = etaSeconds ?? EstimateLeft(percent, DateTime.UtcNow - _stepStartedUtc);
            }
        }

        public JobProgressSnapshot Read()
        {
            lock (_lock)
                return new JobProgressSnapshot(_step, _percent is { } p ? Math.Round(p, 1) : null, _etaSeconds is { } e ? Math.Round(e) : null);
        }

        /// <summary>Time left at the pace so far; null until there's enough to go on (1 % and 5 s in).</summary>
        public static double? EstimateLeft(double percent, TimeSpan elapsed) =>
            percent >= 1 && percent < 100 && elapsed.TotalSeconds >= 5 ? elapsed.TotalSeconds * (100 - percent) / percent : null;

        /// <summary>"about 1 h 12 min left" style text for logs.</summary>
        public static string FormatLeft(double seconds) =>
            seconds >= 3600 ? $"about {(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min left"
            : seconds >= 90 ? $"about {(int)Math.Round(seconds / 60)} min left"
            : $"about {(int)Math.Max(1, Math.Round(seconds))} s left";

        /// <summary>"1:02:03" / "2:03" for logs.</summary>
        public static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        private sealed class Scope(JobProgress? previous) : IDisposable
        {
            public void Dispose() => Ambient.Value = previous;
        }
    }

    /// <param name="Step">What the job is doing now, in words for the page.</param>
    /// <param name="Percent">Of that step, when known.</param>
    /// <param name="EtaSeconds">Time left in that step, when known.</param>
    public sealed record JobProgressSnapshot(string? Step, double? Percent, double? EtaSeconds);
}
