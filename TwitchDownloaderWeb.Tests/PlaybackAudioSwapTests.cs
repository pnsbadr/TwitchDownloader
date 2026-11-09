using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Playback;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>
    /// Swapping saved clean audio into a muted segment for the player must keep both tracks' timestamps, since the player
    /// places them by PTS. Builds a Twitch-like segment (timestamps far from zero) with ffmpeg. Needs ffmpeg (FFMPEG_PATH or PATH).
    /// </summary>
    public sealed class PlaybackAudioSwapTests : IDisposable
    {
        private static readonly string Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } p ? p : "ffmpeg";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-web-swap-" + Guid.NewGuid().ToString("N"));

        public PlaybackAudioSwapTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Fact]
        public void FindsMutedKeys()
        {
            Assert.Equal("123", PlaybackAudioSwap.MutedKey("123-muted.ts"));
            Assert.Null(PlaybackAudioSwap.MutedKey("123.ts"));
            Assert.Null(PlaybackAudioSwap.MutedKey("123-unmuted.ts"));
            Assert.Null(PlaybackAudioSwap.MutedKey("123-muted.mp4"));
        }

        [Theory]
        [InlineData(48000, 0)] // same format: audio copied, timestamps exact
        [InlineData(44100, 25)] // different sample rate: re-encoded to match the video segment's (AAC priming shifts it ~21 ms)
        public async Task KeepsTimestampsAndTheSegmentsAudioFormat(int cleanSampleRate, double toleranceMs)
        {
            SkipIfNoFfmpeg();
            var ct = TestContext.Current.CancellationToken;

            // A 10 s live segment from 1000 s into the stream: keyframes every 2 s, a tone for audio
            await Ff(ct, "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=10", "-f", "lavfi", "-i", "sine=f=440:d=10:sample_rate=48000",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-c:a", "aac", "-output_ts_offset", "1000", "-f", "mpegts", "source.ts");
            // Twitch's muted copy (same video, silenced audio) and the saved clean audio (the audio-only rendition)
            await Ff(ct, "-copyts", "-i", "source.ts", "-map", "0:v", "-map", "0:a", "-c:v", "copy", "-af", "volume=0", "-c:a", "aac",
                "-mpegts_copyts", "1", "-f", "mpegts", "muted.ts");
            string[] cleanCodec = cleanSampleRate == 48000 ? ["-c:a", "copy"] : ["-c:a", "aac", "-ar", cleanSampleRate.ToString()];
            await Ff(ct, ["-copyts", "-i", "source.ts", "-map", "0:a", .. cleanCodec, "-mpegts_copyts", "1", "-f", "mpegts", "clean.ts"]);

            var output = Path.Combine(_dir, "out.ts");
            await PlaybackAudioSwap.SwapAudioAsync(Ffmpeg, Path.Combine(_dir, "muted.ts"), Path.Combine(_dir, "clean.ts"), output, NullLogger.Instance, ct);

            var muted = TsTimestamps.Read(await File.ReadAllBytesAsync(Path.Combine(_dir, "muted.ts"), ct));
            var clean = TsTimestamps.Read(await File.ReadAllBytesAsync(Path.Combine(_dir, "clean.ts"), ct));
            var bytes = await File.ReadAllBytesAsync(output, ct);
            var result = TsTimestamps.Read(bytes);

            Assert.Equal(muted.MinVideoPts, result.MinVideoPts);
            Assert.InRange(TsTimestamps.DiffSeconds(result.FirstAudioPts!.Value, clean.FirstAudioPts!.Value) * 1000, -toleranceMs, toleranceMs);
            Assert.InRange(TsTimestamps.DiffSeconds(result.FirstAudioPts.Value, result.MinVideoPts!.Value), -1, 1);
            Assert.Equal(48000, AdtsFormat.Read(bytes)?.SampleRate);
            Assert.Equal(cleanSampleRate, AdtsFormat.Read(await File.ReadAllBytesAsync(Path.Combine(_dir, "clean.ts"), ct))?.SampleRate);

            // The audio is the clean tone, not the muted silence
            var volume = await Ff(ct, "-i", "out.ts", "-map", "0:a", "-af", "volumedetect", "-f", "null", "-");
            Assert.DoesNotContain("mean_volume: -91", volume);
        }

        private async Task<string> Ff(CancellationToken ct, params string[] args)
        {
            var info = new ProcessStartInfo(Ffmpeg) { WorkingDirectory = _dir, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-hide_banner");
            info.ArgumentList.Add("-y");
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var stderr = process.StandardError.ReadToEndAsync(ct);
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
                Assert.Skip($"ffmpeg not found ({Ffmpeg}); set FFMPEG_PATH to run this test.");
            }
        }
    }
}
