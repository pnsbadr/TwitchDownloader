using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Long export steps (ffmpeg runs, Core's download) report a step, percent and time left, and log as they go.</summary>
    public sealed class JobProgressTests
    {
        private static readonly string Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } p ? p : "ffmpeg";

        [Fact]
        public void FfmpegStatusLinesGiveStepPercentAndTimeLeft()
        {
            var job = new JobProgress();
            var log = new ListLogger();
            var progress = new FfmpegProgress("Re-encoding for the exact cut", null, log, job);
            progress.Started();
            Assert.Equal("Re-encoding for the exact cut", job.Read().Step);
            Assert.Null(job.Read().Percent);

            // The length comes from the first input's Duration line when none was given
            progress.OnLine("  Duration: 02:54:00.00, start: 0.000000, bitrate: 6000 kb/s");
            progress.OnLine("  Duration: 00:00:05.00, start: 0.000000, bitrate: 128 kb/s");
            progress.OnLine("frame=12345 fps=120 q=28.0 size=  102400KiB time=00:43:30.00 bitrate=6000.0kbits/s speed=2.5x");

            var read = job.Read();
            Assert.Equal(25, read.Percent);
            // 2:10:30 of video left at 2.5x
            Assert.Equal(3132, read.EtaSeconds);
            Assert.Contains(log.Lines, x => x.Contains("25%") && x.Contains("43:30 of 2:54:00") && x.Contains("about 52 min left"));

            // N/A at the start of a run is ignored
            progress.OnLine("frame=    0 fps=0.0 q=0.0 size=       0KiB time=N/A bitrate=N/A speed=N/A");
            Assert.Equal(25, job.Read().Percent);
        }

        [Fact]
        public void PrefixNamesThePartAndNewStepsResetProgress()
        {
            var job = new JobProgress();
            job.SetStep("Downloading from Twitch");
            job.Report(50);
            job.Prefix = "Segment 2 of 3";
            job.SetStep("Swapping in the saved clean audio");
            var read = job.Read();
            Assert.Equal("Segment 2 of 3 · Swapping in the saved clean audio", read.Step);
            Assert.Null(read.Percent);
            Assert.Null(read.EtaSeconds);
        }

        [Theory]
        [InlineData("Fetching Video Info [1/4]", "Reading the VOD from Twitch")]
        [InlineData("Downloading {0}% [2/4]", "Downloading from Twitch")]
        [InlineData("Verifying Parts {0}% [3/4]", "Checking the downloaded parts")]
        [InlineData("Finalizing Video {0}% [4/4]", "Joining the downloaded parts")]
        [InlineData("Something New {0}% [9/9]", "Something New [9/9]")]
        public void CoreStatusesInPlainWords(string status, string expected) =>
            Assert.Equal(expected, LoggerTaskProgress.StepName(status));

        [Fact]
        public async Task ExactTrimReportsIntoTheAmbientProgress()
        {
            if (!HasFfmpeg())
                Assert.Skip("ffmpeg not found; set FFMPEG_PATH.");
            var ct = TestContext.Current.CancellationToken;
            var work = Directory.CreateTempSubdirectory("jobprogress-").FullName;
            try
            {
                var source = Path.Combine(work, "in.mp4");
                await FfmpegRunner.RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=30",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "6", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", source],
                    work, new ListLogger(), ct);

                var job = new JobProgress();
                var log = new ListLogger();
                using (JobProgress.Use(job))
                    await ExactTrimService.TrimAsync(Ffmpeg, source, 1, 4, Path.Combine(work, "out.mp4"), work, "ultrafast", 23, log, ct);

                Assert.Equal("Re-encoding for the exact cut", job.Read().Step);
                Assert.Contains(log.Lines, x => x.StartsWith("Re-encoding for the exact cut: started (0:04 of video)"));
                Assert.Contains(log.Lines, x => x.StartsWith("Re-encoding for the exact cut: done in"));
                Assert.Null(JobProgress.Current);
            }
            finally
            {
                Directory.Delete(work, true);
            }
        }

        private static bool HasFfmpeg()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(Ffmpeg, "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
                process!.WaitForExit(10_000);
                return process.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        private sealed class ListLogger : ILogger
        {
            public List<string> Lines { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (Lines)
                    Lines.Add(formatter(state, exception));
            }
        }
    }
}
