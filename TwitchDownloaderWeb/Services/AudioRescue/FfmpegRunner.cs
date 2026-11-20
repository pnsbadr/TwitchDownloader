using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>Runs ffmpeg with an argument list. Paths go in as separate arguments, so no quoting is needed.</summary>
    public static class FfmpegRunner
    {
        /// <param name="onOutputLine">Receives each stdout line, e.g. for "-progress pipe:1" output.</param>
        /// <param name="step">
        /// What the run does, in words ("Re-encoding for the exact cut"). When set, the run is logged (start, progress with speed and time left,
        /// end) and becomes the current step of the ambient <see cref="JobProgress"/>, so a long run is never silent.
        /// </param>
        /// <param name="expectedSeconds">How long the output will be; null reads the first input's "Duration:" from ffmpeg.</param>
        /// <param name="onErrorLine">Receives each stderr line (ffmpeg's input description, warnings).</param>
        public static async Task RunAsync(string ffmpegPath, IReadOnlyList<string> args, string workingDirectory, ILogger logger, CancellationToken cancellationToken,
            Action<string>? onOutputLine = null, string? step = null, double? expectedSeconds = null, Action<string>? onErrorLine = null)
        {
            var progress = step is null ? null : new FfmpegProgress(step, expectedSeconds, logger, JobProgress.Current);
            using var process = new Process
            {
                StartInfo =
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    // ffmpeg reads keys from stdin ("q" stops it); never give it the app's console
                    RedirectStandardInput = true,
                    WorkingDirectory = workingDirectory,
                }
            };
            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            // Keep the tail of stderr for the error message; ffmpeg writes progress there continuously
            var tail = new Queue<string>();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                progress?.OnLine(e.Data);
                onErrorLine?.Invoke(e.Data);
                lock (tail)
                {
                    tail.Enqueue(e.Data);
                    if (tail.Count > 15) tail.Dequeue();
                }
            };

            logger.LogDebug("Running ffmpeg {Args}", string.Join(' ', args));
            process.Start();
            progress?.Started();
            process.StandardInput.Close();
            process.BeginErrorReadLine();
            if (onOutputLine is null)
            {
                _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            }
            else
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null) onOutputLine(e.Data);
                };
                process.BeginOutputReadLine();
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                    // Let it exit so its output file is closed: on Windows the caller's cleanup can't delete a file ffmpeg still has open
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
                catch (InvalidOperationException) { }
                throw;
            }

            if (process.ExitCode != 0)
            {
                string details;
                lock (tail) details = string.Join(" | ", tail);
                progress?.Failed(process.ExitCode);
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {details}");
            }
            progress?.Finished();
        }
    }

    /// <summary>
    /// Reads ffmpeg's stderr status lines ("frame=… time=00:12:34.56 … speed=3.1x") and its first "Duration:", logs a line at every 5 %
    /// (or every minute when the length isn't known) with speed and time left, and reports into a <see cref="JobProgress"/>.
    /// </summary>
    public sealed partial class FfmpegProgress
    {
        private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(1);

        private readonly string _step;
        private readonly ILogger _logger;
        private readonly JobProgress? _job;
        private readonly Stopwatch _clock = new();
        private double? _expectedSeconds;
        private int _lastLoggedStep = -1;
        private TimeSpan _lastLoggedAt;

        public FfmpegProgress(string step, double? expectedSeconds, ILogger logger, JobProgress? job)
        {
            _step = step;
            _expectedSeconds = expectedSeconds is > 0 ? expectedSeconds : null;
            _logger = logger;
            _job = job;
        }

        public void Started()
        {
            _clock.Start();
            _job?.SetStep(_step);
            if (_expectedSeconds is { } length)
                _logger.LogInformation("{Step}: started ({Length} of video).", _step, JobProgress.FormatTime(length));
            else
                _logger.LogInformation("{Step}: started.", _step);
        }

        public void Finished() =>
            _logger.LogInformation("{Step}: done in {Elapsed}.", _step, JobProgress.FormatTime(_clock.Elapsed.TotalSeconds));

        public void Failed(int exitCode) =>
            _logger.LogWarning("{Step}: ffmpeg failed (exit code {ExitCode}) after {Elapsed}.", _step, exitCode, JobProgress.FormatTime(_clock.Elapsed.TotalSeconds));

        /// <summary>One stderr line. Called on ffmpeg's output thread, one line at a time.</summary>
        public void OnLine(string line)
        {
            if (_expectedSeconds is null && DurationPattern().Match(line) is { Success: true } duration && TryParseTime(duration.Groups[1].Value, out var total) && total > 0)
            {
                _expectedSeconds = total;
                return;
            }

            var time = TimePattern().Match(line);
            if (!time.Success || !TryParseTime(time.Groups[1].Value, out var done))
                return;
            var speedMatch = SpeedPattern().Match(line);
            double? speed = speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
                ? s : null;
            Report(done, speed, _clock.Elapsed);
        }

        /// <summary>Progress at <paramref name="doneSeconds"/> of output (separate from parsing so tests can drive it).</summary>
        public void Report(double doneSeconds, double? speed, TimeSpan elapsed)
        {
            var speedText = speed is { } x ? $", {x.ToString("0.##", CultureInfo.InvariantCulture)}x" : "";
            if (_expectedSeconds is not { } total)
            {
                if (elapsed - _lastLoggedAt < LogEvery)
                    return;
                _lastLoggedAt = elapsed;
                _logger.LogInformation("{Step}: {Done} done{Speed}.", _step, JobProgress.FormatTime(doneSeconds), speedText);
                return;
            }

            var percent = Math.Clamp(doneSeconds / total * 100, 0, 100);
            // ffmpeg's speed is seconds of video per second; it settles quickly, so it beats the average pace early on
            double? left = speed is { } v ? Math.Max(0, total - doneSeconds) / v : JobProgress.EstimateLeft(percent, elapsed);
            _job?.Report(percent, left);

            var step = (int)(percent / 5);
            if (step <= _lastLoggedStep && elapsed - _lastLoggedAt < LogEvery)
                return;
            _lastLoggedStep = Math.Max(step, _lastLoggedStep);
            _lastLoggedAt = elapsed;
            _logger.LogInformation("{Step}: {Percent}% ({Done} of {Total}{Speed}{Left}).", _step, (int)percent, JobProgress.FormatTime(doneSeconds),
                JobProgress.FormatTime(total), speedText, left is { } l ? $", {JobProgress.FormatLeft(l)}" : "");
        }

        private static bool TryParseTime(string text, out double seconds)
        {
            seconds = 0;
            var parts = text.Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                return false;
            seconds = h * 3600 + m * 60 + sec;
            return true;
        }

        [GeneratedRegex(@"^\s*Duration:\s*(\d+:\d{2}:\d{2}(?:\.\d+)?)")]
        private static partial Regex DurationPattern();

        // "N/A" at the start of a run doesn't match
        [GeneratedRegex(@"\btime=\s*(\d+:\d{2}:\d{2}(?:\.\d+)?)")]
        private static partial Regex TimePattern();

        [GeneratedRegex(@"\bspeed=\s*([\d.]+)x")]
        private static partial Regex SpeedPattern();
    }
}
