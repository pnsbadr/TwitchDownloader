using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>
    /// End-to-end sync test without Twitch: builds a fake VOD with beeps at known times, split into 10 s segments like
    /// Twitch's, mutes two segments, then restores them from a separate audio-only rendition and checks every beep
    /// lands where it does in an unmuted download. Needs ffmpeg (FFMPEG_PATH, or ffmpeg on PATH).
    /// </summary>
    public sealed partial class AudioSpliceSyncTests : IDisposable
    {
        private const double ToleranceMs = 25;
        private static readonly string Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } p ? p : "ffmpeg";

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-web-sync-" + Guid.NewGuid().ToString("N"));

        public AudioSpliceSyncTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Theory]
        [InlineData(48000, "Exact")]
        [InlineData(48000, "Safe")]
        [InlineData(44100, "Exact")] // clean audio at a different sample rate than the video rendition
        public async Task RestoredAudioMatchesUnmutedDownload(int cleanSampleRate, string trimMode)
        {
            SkipIfNoFfmpeg();
            var ct = TestContext.Current.CancellationToken;

            await BuildFakeVodAsync(cleanSampleRate, ct);

            var playlist = await File.ReadAllTextAsync(Path.Combine(_dir, "muted", "index.m3u8"), ct);
            var segments = VodPlaylist.ParseSegments(playlist);
            Assert.Equal([2, 3], segments.Where(x => x.IsMuted).Select(x => x.Index));

            var trimStart = TimeSpan.FromSeconds(15);
            var trimEnd = TimeSpan.FromSeconds(45);
            var range = VodPlaylist.GetTrimRange(segments, trimStart, trimEnd);
            Assert.Equal(1, range.Start.Value);
            Assert.Equal(5, range.End.Value);

            // What Core's Safe mode writes, from the muted and (for reference) the clean video rendition
            var muted = await CoreSafeConcatAsync("muted", segments, range, ct);
            var reference = await CoreSafeConcatAsync("clean", segments, range, ct);

            var cleanFiles = segments.Where(x => x.IsMuted).ToDictionary(x => x.Key, x =>
            {
                var file = Path.Combine(_dir, "audio", x.Key + ".ts");
                return new CleanAudioFile(file, TsTimestamps.Read(File.ReadAllBytes(file)).FirstAudioPts!.Value);
            });

            var runs = RestorePlanner.FindRuns(segments, range, cleanFiles.ContainsKey);
            Assert.Single(runs);

            var head = (VodSegment s) => TsTimestamps.Read(ReadHead(Path.Combine(_dir, "muted", s.Path)));
            var (audioStart, placed) = RestorePlanner.Place(segments, range, runs, k => cleanFiles[k],
                head(segments[range.Start.Value]), runs.Select(r => head(r[0])).ToArray());

            var restored = Path.Combine(_dir, "restored.mp4");
            await AudioSplicer.SpliceAsync(Ffmpeg, muted, audioStart, placed, restored, Path.Combine(_dir, "work"), NullLogger.Instance, ct);

            if (trimMode == "Exact")
            {
                var start = (trimStart - TimeSpan.FromSeconds((double)segments[range.Start.Value].Offset)).TotalSeconds;
                var length = (trimEnd - trimStart).TotalSeconds;
                reference = await TrimAsync(reference, start, length, ct);
                restored = await TrimAsync(restored, start, length, ct);
            }

            var expected = await BeepOnsetsAsync(reference, ct);
            var actual = await BeepOnsetsAsync(restored, ct);
            var mutedBeeps = await BeepOnsetsAsync(muted, ct);

            Assert.True(mutedBeeps.Count < expected.Count, "the muted download should be missing beeps");
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var diffMs = Math.Abs(actual[i] - expected[i]) * 1000;
                Assert.True(diffMs <= ToleranceMs, $"beep {i} at {expected[i]:0.000}s is off by {diffMs:0.0} ms");
            }
        }

        private async Task BuildFakeVodAsync(int cleanSampleRate, CancellationToken ct)
        {
            // An 80 ms 1 kHz beep every 3.7 s from 0.5 s, so beeps fall at different places in each segment
            await Ff(ct, "-f", "lavfi", "-i", "sine=f=1000:r=48000:d=60",
                "-af", "volume='if(lt(mod(t-0.5,3.7),0.08)*gte(t,0.5),1,0)':eval=frame", "-c:a", "pcm_s16le", "beeps.wav");
            // Twitch's muting: silence over two segments
            await Ff(ct, "-i", "beeps.wav", "-af", "volume=enable='between(t,20,40)':volume=0", "muted.wav");

            foreach (var (name, wav) in new[] { ("clean", "beeps.wav"), ("muted", "muted.wav") })
            {
                Directory.CreateDirectory(Path.Combine(_dir, name));
                await Ff(ct, "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=60", "-i", wav,
                    "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0",
                    "-c:a", "aac", "-b:a", "160k", "-ar", "48000",
                    "-f", "hls", "-hls_time", "10", "-hls_playlist_type", "vod", "-hls_segment_filename", $"{name}/%d.ts", $"{name}/index.m3u8");
            }

            Directory.CreateDirectory(Path.Combine(_dir, "audio"));
            await Ff(ct, "-i", "beeps.wav", "-c:a", "aac", "-b:a", "160k", "-ar", cleanSampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "hls", "-hls_time", "10", "-hls_playlist_type", "vod", "-hls_segment_filename", "audio/%d.ts", "audio/index.m3u8");

            // Rename the silenced segments the way Twitch does
            var playlistPath = Path.Combine(_dir, "muted", "index.m3u8");
            var playlist = await File.ReadAllTextAsync(playlistPath, ct);
            foreach (var n in new[] { 2, 3 })
            {
                File.Move(Path.Combine(_dir, "muted", $"{n}.ts"), Path.Combine(_dir, "muted", $"{n}-muted.ts"));
                playlist = Regex.Replace(playlist, $@"(?m)^{n}\.ts$", $"{n}-muted.ts");
            }

            await File.WriteAllTextAsync(playlistPath, playlist, ct);
        }

        /// <summary>Concatenates like Core's VideoDownloader in Safe mode (same concat list format and ffmpeg flags).</summary>
        private async Task<string> CoreSafeConcatAsync(string rendition, IReadOnlyList<VodSegment> segments, Range range, CancellationToken ct)
        {
            var (offset, length) = range.GetOffsetAndLength(segments.Count);
            var list = "ffconcat version 1.0\n" + string.Concat(segments.Skip(offset).Take(length).Select(s =>
                $"file '{(rendition == "clean" ? s.UnmutedPath : s.Path)}'\nstream\nexact_stream_id 0x100\nstream\nexact_stream_id 0x101\nstream\nexact_stream_id 0x102\nduration {s.Duration.ToString(CultureInfo.InvariantCulture)}\n"));
            await File.WriteAllTextAsync(Path.Combine(_dir, rendition, "concat.txt"), list, ct);

            var output = Path.Combine(_dir, $"{rendition}-safe.mp4");
            await Ff(ct, "-avoid_negative_ts", "make_zero", "-analyzeduration", $"{int.MaxValue}", "-probesize", $"{int.MaxValue}",
                "-f", "concat", "-max_streams", $"{int.MaxValue}", "-i", $"{rendition}/concat.txt", "-c", "copy", output);
            return output;
        }

        private async Task<string> TrimAsync(string input, double start, double length, CancellationToken ct)
        {
            var output = Path.ChangeExtension(input, ".exact.mp4");
            await ExactTrimService.TrimAsync(Ffmpeg, input, start, length, output, _dir, "ultrafast", 18, NullLogger.Instance, ct);
            return output;
        }

        /// <summary>
        /// An Exact cut between keyframes must start on the cut frame. Core's stream-copy cut started the video at the next
        /// keyframe, so the first ~1 s showed a frozen frame while the audio played.
        /// </summary>
        [Fact]
        public async Task ExactTrimStartsOnTheCutFrame()
        {
            SkipIfNoFfmpeg();
            var ct = TestContext.Current.CancellationToken;

            // Keyframes every 2 s, like Twitch
            await Ff(ct, "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=10", "-f", "lavfi", "-i", "sine=f=440:d=10",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-c:a", "aac", "src.mp4");

            var output = Path.Combine(_dir, "exact.mp4");
            await ExactTrimService.TrimAsync(Ffmpeg, Path.Combine(_dir, "src.mp4"), 0.5, 5, output, _dir, "ultrafast", 18, NullLogger.Instance, ct);

            var frames = ShowInfoPtsRegex.Matches(await Ff(ct, "-i", output, "-map", "0:v", "-vf", "showinfo", "-f", "null", "-"))
                .Select(m => double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(150, frames.Count);
            Assert.True(frames[0] < 0.001, $"first frame at {frames[0]} s");

            // The first frame is the source frame at 0.5 s, not a keyframe (0 s or 2 s)
            var atCut = await FirstFramePsnrAsync(output, 0.5, ct);
            Assert.True(atCut > 40, $"PSNR vs 0.5 s: {atCut}");
            Assert.True(atCut > await FirstFramePsnrAsync(output, 0, ct) + 10);
            Assert.True(atCut > await FirstFramePsnrAsync(output, 2, ct) + 10);
        }

        private async Task<double> FirstFramePsnrAsync(string file, double sourceSeconds, CancellationToken ct)
        {
            var stderr = await Ff(ct, "-i", file, "-ss", sourceSeconds.ToString(CultureInfo.InvariantCulture), "-i", "src.mp4",
                "-lavfi", "[0:v][1:v]psnr", "-frames:v", "1", "-f", "null", "-");
            var value = PsnrRegex.Match(stderr).Groups["v"].Value;
            return value == "inf" ? double.PositiveInfinity : double.Parse(value, CultureInfo.InvariantCulture);
        }

        [GeneratedRegex(@"\bpts_time:(?<t>-?[0-9.]+)")]
        private static partial Regex ShowInfoPtsRegex { get; }

        [GeneratedRegex(@"average:(?<v>inf|[0-9.]+)")]
        private static partial Regex PsnrRegex { get; }

        [GeneratedRegex(@"silence_end: (?<t>-?[0-9.]+)")]
        private static partial Regex SilenceEndRegex { get; }

        /// <summary>Beep start times on the file's own timeline (silencedetect reports timestamps, not sample counts).</summary>
        private async Task<List<double>> BeepOnsetsAsync(string file, CancellationToken ct)
        {
            var stderr = await Ff(ct, "-i", file, "-map", "0:a", "-af", "silencedetect=n=-30dB:d=0.05", "-f", "null", "-");
            return SilenceEndRegex.Matches(stderr).Select(m => double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture)).ToList();
        }

        private static byte[] ReadHead(string path)
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[Math.Min(fs.Length, 256 * 1024)];
            fs.ReadExactly(buffer);
            return buffer;
        }

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
                Assert.Skip($"ffmpeg not found ({Ffmpeg}); set FFMPEG_PATH to run the sync test.");
            }
        }
    }
}
