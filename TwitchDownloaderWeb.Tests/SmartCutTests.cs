using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Segments;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>
    /// Smart cuts (only the edges re-encoded) against a fake Twitch-like VOD: 60 fps H.264 with B-frames and a keyframe every 2 s, made as
    /// MPEG-TS and copied into an mp4 like Core's Safe download. White flashes with beeps at the same moments sit in the re-encoded head,
    /// the copied middle and the re-encoded tail; each must keep its sound within a frame, and every frame must be the source's.
    /// Needs ffmpeg (FFMPEG_PATH, or ffmpeg on PATH).
    /// </summary>
    public sealed partial class SmartCutTests : IDisposable
    {
        private static readonly string Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } p ? p : "ffmpeg";
        // Between frames (a frame starts every 1/60 s), so each flash starts on the next frame the same way
        private static readonly double[] Events = [5.005, 15.005, 28.105, 36.005, 50.005];

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-web-smartcut-" + Guid.NewGuid().ToString("N"));

        public SmartCutTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Fact]
        public async Task SmartCutKeepsEveryFrameAndTheSync()
        {
            SkipIfNoFfmpeg();
            var ct = TestContext.Current.CancellationToken;
            var source = await BuildSourceAsync(ct);

            var log = new ListLogger();
            var output = Path.Combine(_dir, "smart.mp4");
            await ExactTrimService.TrimAsync(Ffmpeg, source, 4.3, 24, output, _dir, "ultrafast", 18, log, ct);
            Assert.Contains(log.Lines, x => x.StartsWith("Smart cut:"));
            Assert.DoesNotContain(log.Lines, x => x.Contains("re-encoding the whole cut"));

            await AssertCutAsync(source, output, 4.3, 24, [5, 15, 28.1], ct);
        }

        [Fact]
        public async Task SmartCutsJoinWithoutReencoding()
        {
            SkipIfNoFfmpeg();
            var ct = TestContext.Current.CancellationToken;
            var source = await BuildSourceAsync(ct);

            var log = new ListLogger();
            var first = Path.Combine(_dir, "a.mp4");
            var second = Path.Combine(_dir, "b.mp4");
            await ExactTrimService.TrimAsync(Ffmpeg, source, 4.3, 24, first, _dir, "ultrafast", 18, log, ct);
            // Starts on a keyframe (the source has one every 2 s from 0.021 s): no head, so the file starts with the copied stream
            await ExactTrimService.TrimAsync(Ffmpeg, source, 34.021, 20.5, second, _dir, "ultrafast", 18, log, ct);
            Assert.Equal(2, log.Lines.Count(x => x.StartsWith("Smart cut:")));

            var joined = Path.Combine(_dir, "joined.mp4");
            await SegmentJoiner.JoinAsync(Ffmpeg, [first, second], joined, _dir, "ultrafast", 18, log, ct);
            Assert.DoesNotContain(log.Lines, x => x.Contains("re-encoding."));

            // Decodes cleanly and every flash keeps its beep across the join
            Assert.Equal("", (await Ff(ct, "-v", "error", "-i", joined, "-f", "null", "-")).Trim());
            var flashes = await FlashesAsync(joined, ct);
            var end = (await FrameTimesAsync(joined, ct))[^1];
            var beeps = (await BeepsAsync(joined, ct)).Where(x => x < end - 0.3).ToList(); // silencedetect also ends the silence at the end
            Assert.True(flashes.Count == 5, "flashes " + string.Join(", ", flashes) + " beeps " + string.Join(", ", beeps));
            Assert.True(beeps.Count == 5, "beeps " + string.Join(", ", beeps) + " flashes " + string.Join(", ", flashes));
            var gap = (await BeepsAsync(source, ct))[0] - (await FlashesAsync(source, ct))[0];
            for (var i = 0; i < flashes.Count; i++)
                Assert.True(Math.Abs(beeps[i] - flashes[i] - gap) < 0.005, $"event {i}: flash at {flashes[i]:0.###}, beep at {beeps[i]:0.###}, source gap {gap:0.###}");
        }

        [Fact]
        public async Task ShortCutsAreReencodedWhole()
        {
            SkipIfNoFfmpeg();
            var ct = TestContext.Current.CancellationToken;
            var source = await BuildSourceAsync(ct);

            var log = new ListLogger();
            var output = Path.Combine(_dir, "short.mp4");
            await ExactTrimService.TrimAsync(Ffmpeg, source, 4.3, 12, output, _dir, "ultrafast", 18, log, ct);
            Assert.DoesNotContain(log.Lines, x => x.StartsWith("Smart cut:"));
            Assert.Contains(log.Lines, x => x.StartsWith("Re-encoding for the exact cut: done"));
        }

        /// <summary>Every frame is the source's frame at the same place, and each flash and beep sits where it does in the source.</summary>
        private async Task AssertCutAsync(string source, string output, double start, double length, double[] events, CancellationToken ct)
        {
            // The source's own timeline (its first frame needn't be at 0): the cut starts on the first frame at or after the start
            var sourceFrames = await FrameTimesAsync(source, ct);
            var cutStart = sourceFrames.First(x => x >= start - 0.0005);
            var expected = sourceFrames.Count(x => x >= cutStart && x < start + length - 0.0005);
            var times = await FrameTimesAsync(output, ct);
            Assert.InRange(times.Count, expected - 1, expected + 1);
            Assert.True(times[0] < 0.001, $"first frame at {times[0]} s");

            // Frame by frame against the source from the cut's first frame (by index, so a dropped or doubled frame shows)
            var stderr = await Ff(ct, "-i", output, "-ss", Num(start), "-t", Num(length), "-i", source,
                "-lavfi", "[0:v]setpts=N/60/TB[a];[1:v]setpts=N/60/TB[b];[a][b]psnr=stats_file=psnr.txt", "-f", "null", "-");
            Assert.Contains("PSNR", stderr);
            var worst = File.ReadAllLines(Path.Combine(_dir, "psnr.txt"))
                .Select(x => PsnrAvg().Match(x).Groups["v"].Value)
                .Select(v => v == "inf" ? double.PositiveInfinity : double.Parse(v, CultureInfo.InvariantCulture)).Min();
            Assert.True(worst > 30, $"worst frame PSNR {worst}");

            var sourceFlashes = (await FlashesAsync(source, ct)).Where(x => x >= start && x < start + length).ToList();
            var sourceBeeps = (await BeepsAsync(source, ct)).Where(x => x >= start && x < start + length).ToList();
            var flashes = await FlashesAsync(output, ct);
            var beeps = await BeepsAsync(output, ct);
            Assert.Equal(events.Length, sourceFlashes.Count);
            Assert.Equal(events.Length, flashes.Count);
            Assert.Equal(events.Length, beeps.Count);
            for (var i = 0; i < events.Length; i++)
            {
                Assert.True(Math.Abs(flashes[i] - (sourceFlashes[i] - cutStart)) < 0.002, $"flash {i} at {flashes[i]:0.###}, source {sourceFlashes[i] - cutStart:0.###}");
                Assert.True(Math.Abs(beeps[i] - (sourceBeeps[i] - cutStart)) < 0.005, $"beep {i} at {beeps[i]:0.###}, source {sourceBeeps[i] - cutStart:0.###}");
            }
        }

        private async Task<string> BuildSourceAsync(CancellationToken ct)
        {
            var on = string.Join('+', Events.Select(e => $"between(t,{Num(e)},{Num(e + 0.05)})"));
            await Ff(ct,
                "-f", "lavfi", "-i", $"testsrc2=s=320x180:r=60:d=60,drawbox=c=white:t=fill:enable='{on}'",
                "-f", "lavfi", "-i", $"aevalsrc=exprs='if({on},sin(2*PI*1000*t),0)':s=48000:d=60",
                "-c:v", "libx264", "-preset", "ultrafast", "-bf", "2", "-g", "120", "-keyint_min", "120", "-sc_threshold", "0",
                "-c:a", "aac", "-b:a", "128k", "-f", "mpegts", "src.ts");
            // Core's Safe download: the stream's segments copied into an mp4
            await Ff(ct, "-i", "src.ts", "-c", "copy", "src.mp4");
            return Path.Combine(_dir, "src.mp4");
        }

        private async Task<List<double>> FrameTimesAsync(string file, CancellationToken ct) =>
            ShowInfoPts().Matches(await Ff(ct, "-i", file, "-map", "0:v", "-vf", "showinfo", "-f", "null", "-"))
                .Select(m => double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture)).ToList();

        /// <summary>When each white flash starts on the file's timeline.</summary>
        private async Task<List<double>> FlashesAsync(string file, CancellationToken ct)
        {
            var text = await Ff(ct, "-i", file, "-map", "0:v", "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-");
            var result = new List<double>();
            double? time = null;
            var lit = false;
            foreach (var line in text.Split('\n'))
            {
                if (MetadataPts().Match(line) is { Success: true } m)
                    time = double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture);
                else if (YAvg().Match(line) is { Success: true } y && time is { } t)
                {
                    var bright = double.Parse(y.Groups["v"].Value, CultureInfo.InvariantCulture) > 200;
                    if (bright && !lit)
                        result.Add(t);
                    lit = bright;
                }
            }
            return result;
        }

        private async Task<List<double>> BeepsAsync(string file, CancellationToken ct) =>
            SilenceEnd().Matches(await Ff(ct, "-i", file, "-map", "0:a", "-af", "silencedetect=n=-30dB:d=0.2", "-f", "null", "-"))
                .Select(m => double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture)).ToList();

        private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        [GeneratedRegex(@"\bpts_time:(?<t>-?[0-9.]+)")]
        private static partial Regex ShowInfoPts();

        [GeneratedRegex(@"frame:\d+\s+pts:\d+\s+pts_time:(?<t>-?[0-9.]+)")]
        private static partial Regex MetadataPts();

        [GeneratedRegex(@"lavfi\.signalstats\.YAVG=(?<v>[0-9.]+)")]
        private static partial Regex YAvg();

        [GeneratedRegex(@"silence_end: (?<t>-?[0-9.]+)")]
        private static partial Regex SilenceEnd();

        [GeneratedRegex(@"psnr_avg:(?<v>inf|[0-9.]+)")]
        private static partial Regex PsnrAvg();

        private async Task<string> Ff(CancellationToken ct, params string[] args)
        {
            var psi = new ProcessStartInfo(Ffmpeg) { WorkingDirectory = _dir, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-hide_banner", "-nostdin", "-y" }.Concat(args))
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync(ct);
            _ = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var text = await stderr;
            Assert.True(process.ExitCode == 0, $"ffmpeg {string.Join(' ', args)} failed: {text[^Math.Min(text.Length, 800)..]}");
            return text;
        }

        private static void SkipIfNoFfmpeg()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Ffmpeg, "-version") { RedirectStandardOutput = true, UseShellExecute = false });
                p!.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip($"ffmpeg not found ({Ffmpeg}); set FFMPEG_PATH to run the smart cut tests.");
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
