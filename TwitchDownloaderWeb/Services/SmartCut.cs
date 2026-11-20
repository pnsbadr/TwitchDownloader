using System.Globalization;
using System.Text.RegularExpressions;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Exact cuts that re-encode only their edges (badrfly 2026-11-20: app-wide, so every Exact cut, export and download is faster).
    /// Copied video can only start on a keyframe, so the stretch from the cut's first frame to the first keyframe (the head) and from the
    /// last keyframe to the cut's end (the tail) are re-encoded, and everything between is copied as it is. Audio is always re-encoded
    /// (cheap) as one piece from the cut's first frame, so it stays in sync whatever the parts are.
    /// <para>
    /// Parts are made as MPEG-TS, which carries the video's settings in every keyframe, and joined with ffmpeg's concat demuxer into the mp4,
    /// so the copied middle keeps the stream's own settings next to the re-encoded edges. Anything unexpected (not H.264, no keyframes
    /// found, a part of the wrong length) returns false or throws, and the caller re-encodes the whole cut as before.
    /// </para>
    /// </summary>
    public static partial class SmartCut
    {
        /// <summary>Shorter cuts are re-encoded whole: it's quick, and there's little to copy.</summary>
        public const double MinSeconds = 20;

        /// <summary>The copied middle must be at least this long to be worth it.</summary>
        private const double MinCopySeconds = 8;

        /// <summary>How far around each edge keyframes are looked for (Twitch puts one every ~2 s).</summary>
        private const double ProbeSeconds = 12;

        /// <summary>Times within this of each other are the same frame (well under a frame at 60 fps).</summary>
        private const double Same = 0.0005;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>
        /// Cuts <paramref name="lengthSeconds"/> from <paramref name="startSeconds"/> of <paramref name="inputFile"/> into
        /// <paramref name="outputFile"/> (mp4). Returns false, having written nothing, when the cut isn't suited; the caller then re-encodes.
        /// </summary>
        public static async Task<bool> TryCutAsync(string ffmpegPath, string inputFile, double startSeconds, double lengthSeconds, string outputFile,
            string workDirectory, string preset, int crf, ILogger logger, CancellationToken cancellationToken)
        {
            if (lengthSeconds < MinSeconds)
                return false;

            var end = startSeconds + lengthSeconds;
            var head = await ProbeAsync(ffmpegPath, inputFile, Math.Max(0, startSeconds), ProbeSeconds, workDirectory, logger, cancellationToken);
            if (head is null || !string.Equals(head.Codec, "h264", StringComparison.OrdinalIgnoreCase))
                return false;
            // The cut's first frame: what an input seek to the start keeps (frames at or after it)
            var first = head.Frames.Where(x => x.Time >= startSeconds - Same).Select(x => (double?)x.Time).Min();
            var keyAfterStart = head.Frames.Where(x => x.Key && x.Time >= (first ?? startSeconds) - Same).Select(x => (double?)x.Time).Min();
            if (first is not { } cutStart || keyAfterStart is not { } k1)
                return false;

            var tail = await ProbeAsync(ffmpegPath, inputFile, Math.Max(0, end - ProbeSeconds), ProbeSeconds + 0.5, workDirectory, logger, cancellationToken);
            if (tail is null)
                return false;
            // Frames before the end are kept; the last keyframe at or before it starts the tail
            var keyBeforeEnd = tail.Frames.Where(x => x.Key && x.Time <= end + Same).Select(x => (double?)x.Time).Max();
            if (keyBeforeEnd is not { } k2 || k2 - k1 < MinCopySeconds)
                return false;
            var keyBeforeK1 = head.Frames.Where(x => x.Key && x.Time < k1 - Same).Select(x => (double?)x.Time).Max();
            var keyBeforeK2 = tail.Frames.Where(x => x.Key && x.Time < k2 - Same).Select(x => (double?)x.Time).Max();

            var dir = Path.Combine(workDirectory, "smart-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var parts = new List<(string File, double? Seconds)>();
                var common = new[] { "-hide_banner", "-nostdin", "-y" };

                if (k1 - cutStart > Same)
                {
                    // Seeking just before the first frame keeps it; the length stops just before the keyframe, which the middle starts with
                    await EncodeEdgeAsync(ffmpegPath, inputFile, cutStart - Same, k1 - cutStart, Path.Combine(dir, "head.ts"), dir, preset, crf, logger,
                        cancellationToken);
                    parts.Add(("head.ts", k1 - cutStart));
                }

                // The middle, copied: split the video at its two keyframes. The segment muxer splits at the first keyframe at or after each
                // time, so each time sits between that keyframe and the one before it (its decode time is a little earlier than its own).
                var splitA = k1 - Math.Min(0.3, keyBeforeK1 is { } a ? (k1 - a) / 2 : 0.3);
                var splitB = k2 - Math.Min(0.3, keyBeforeK2 is { } b ? (k2 - b) / 2 : 0.3);
                var copyArgs = new List<string>(common)
                {
                    "-t", Num(k2 + 1), "-i", inputFile,
                    "-map", "0:v:0", "-c", "copy",
                    "-f", "segment", "-segment_times", $"{Num(Math.Max(0.001, splitA))},{Num(splitB)}",
                    "-segment_format", "mpegts", "-reset_timestamps", "1",
                    "-segment_list", "middle.csv", "-segment_list_type", "csv",
                    "middle%03d.ts",
                };
                await FfmpegRunner.RunAsync(ffmpegPath, copyArgs, dir, logger, cancellationToken,
                    step: "Copying the middle of the cut", expectedSeconds: k2 + 1);
                var middle = FindMiddle(Path.Combine(dir, "middle.csv"), k2 - k1);
                if (middle is null)
                {
                    logger.LogWarning("Smart cut: the copied middle didn't come out {Length:0.###} s long; re-encoding the whole cut.", k2 - k1);
                    return false;
                }
                parts.Add((middle, k2 - k1));

                if (end - k2 > Same)
                {
                    await EncodeEdgeAsync(ffmpegPath, inputFile, k2 - Same, end - k2 + Same, Path.Combine(dir, "tail.ts"), dir, preset, crf, logger,
                        cancellationToken);
                    parts.Add(("tail.ts", null));
                }

                var audio = head.HasAudio ? Path.Combine(dir, "audio.m4a") : null;
                if (audio is not null)
                {
                    await FfmpegRunner.RunAsync(ffmpegPath, [
                        .. common, "-ss", Num(cutStart), "-i", inputFile, "-t", Num(end - cutStart),
                        "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "192k", audio,
                    ], dir, logger, cancellationToken, step: "Encoding the cut's audio", expectedSeconds: end - cutStart);
                }

                // Durations place each part right after the one before (concat would otherwise read them from the files)
                var list = string.Join('\n', parts.Select(x => $"file '{x.File}'" + (x.Seconds is { } s ? $"\nduration {Num(s)}" : "")));
                await File.WriteAllTextAsync(Path.Combine(dir, "parts.txt"), list + "\n", cancellationToken);
                var joinArgs = new List<string>(common) { "-f", "concat", "-safe", "0", "-i", "parts.txt" };
                if (audio is not null)
                    joinArgs.AddRange(["-i", audio]);
                joinArgs.AddRange(["-i", inputFile, "-map", "0:v:0"]);
                if (audio is not null)
                    joinArgs.AddRange(["-map", "1:a:0"]);
                joinArgs.AddRange([
                    "-map_metadata", audio is null ? "1" : "2", "-c", "copy", "-movflags", "+faststart", outputFile,
                ]);
                await FfmpegRunner.RunAsync(ffmpegPath, joinArgs, dir, logger, cancellationToken,
                    step: "Joining the cut", expectedSeconds: end - cutStart);
                logger.LogInformation("Smart cut: {Copied} of {Length} copied, {Encoded} re-encoded.", JobProgress.FormatTime(k2 - k1),
                    JobProgress.FormatTime(end - cutStart), JobProgress.FormatTime(end - cutStart - (k2 - k1)));
                return true;
            }
            catch
            {
                try { File.Delete(outputFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Couldn't delete smart cut folder {Path}: {Message}", dir, ex.Message);
                }
            }
        }

        private static Task EncodeEdgeAsync(string ffmpegPath, string inputFile, double from, double seconds, string output, string dir, string preset, int crf,
            ILogger logger, CancellationToken cancellationToken) =>
            FfmpegRunner.RunAsync(ffmpegPath, [
                "-hide_banner", "-nostdin", "-y", "-ss", Num(Math.Max(0, from)), "-i", inputFile, "-t", Num(seconds),
                "-map", "0:v:0", "-an", "-sn", "-dn", "-fps_mode", "passthrough",
                "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(Invariant), "-pix_fmt", "yuv420p",
                "-f", "mpegts", output,
            ], dir, logger, cancellationToken, step: "Re-encoding the cut's edge", expectedSeconds: seconds);

        /// <summary>The segment file that runs from the first split to the second: the one of the expected length.</summary>
        private static string? FindMiddle(string csvPath, double seconds)
        {
            if (!File.Exists(csvPath))
                return null;
            foreach (var line in File.ReadAllLines(csvPath))
            {
                var cells = line.Split(',');
                if (cells.Length < 3
                    || !double.TryParse(cells[^2], NumberStyles.Float, Invariant, out var from)
                    || !double.TryParse(cells[^1], NumberStyles.Float, Invariant, out var to))
                    continue;
                if (Math.Abs(to - from - seconds) < 0.002)
                    return string.Join(',', cells[..^2]);
            }
            return null;
        }

        private sealed record Frame(double Time, bool Key);

        private sealed record Probe(string Codec, bool HasAudio, List<Frame> Frames);

        /// <summary>
        /// The video packets from the keyframe before <paramref name="from"/> for <paramref name="seconds"/>, in seconds from the file's start
        /// (as input seeks count them), read with a stream copy (fast: nothing is decoded).
        /// </summary>
        private static async Task<Probe?> ProbeAsync(string ffmpegPath, string inputFile, double from, double seconds, string dir, ILogger logger,
            CancellationToken cancellationToken)
        {
            var frames = new List<(long Pts, bool Key)>();
            long num = 0, den = 0;
            double? start = null;
            string? codec = null;
            var hasAudio = false;
            var gate = new object();
            await FfmpegRunner.RunAsync(ffmpegPath, [
                "-hide_banner", "-nostdin", "-ss", Num(from), "-t", Num(seconds), "-copyts", "-i", inputFile,
                "-map", "0:v:0", "-c", "copy", "-f", "framecrc", "-",
            ], dir, logger, cancellationToken, onOutputLine: line =>
            {
                lock (gate)
                {
                    if (TimeBase().Match(line) is { Success: true } tb)
                    {
                        num = long.Parse(tb.Groups[1].Value, Invariant);
                        den = long.Parse(tb.Groups[2].Value, Invariant);
                        return;
                    }
                    if (line.StartsWith('#'))
                        return;
                    var cells = line.Split(',', StringSplitOptions.TrimEntries);
                    if (cells.Length < 6 || cells[0] != "0" || !long.TryParse(cells[2], NumberStyles.Integer, Invariant, out var pts))
                        return;
                    var flags = cells.Skip(6).FirstOrDefault(x => x.StartsWith("F=0x", StringComparison.Ordinal));
                    var key = flags is null || (int.TryParse(flags[4..], NumberStyles.HexNumber, Invariant, out var f) && (f & 1) == 1);
                    frames.Add((pts, key));
                }
            }, onErrorLine: line =>
            {
                lock (gate)
                {
                    if (start is null && StartLine().Match(line) is { Success: true } s)
                        start = double.Parse(s.Groups[1].Value, Invariant);
                    else if (codec is null && VideoLine().Match(line) is { Success: true } v)
                        codec = v.Groups[1].Value;
                    else if (AudioLine().IsMatch(line))
                        hasAudio = true;
                }
            });

            lock (gate)
            {
                if (codec is null || den == 0 || frames.Count == 0)
                    return null;
                var offset = start ?? 0;
                return new Probe(codec, hasAudio, frames.Select(x => new Frame((double)x.Pts * num / den - offset, x.Key)).OrderBy(x => x.Time).ToList());
            }
        }

        private static string Num(double value) => value.ToString("0.######", Invariant);

        [GeneratedRegex(@"^#tb 0: (\d+)/(\d+)")]
        private static partial Regex TimeBase();

        [GeneratedRegex(@"^\s*Duration: .*, start: (-?[0-9.]+)")]
        private static partial Regex StartLine();

        // Only the input's streams ("Stream #0:0..." before "Output #0"), which come first
        [GeneratedRegex(@"^\s*Stream #0:\d+.*: Video: (\w+)")]
        private static partial Regex VideoLine();

        [GeneratedRegex(@"^\s*Stream #0:\d+.*: Audio: ")]
        private static partial Regex AudioLine();
    }
}
