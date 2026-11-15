using System.Globalization;
using System.Text.Json.Serialization;

namespace TwitchDownloaderWeb.Services.Shortform
{
    /// <summary>A rectangle in the source video, as fractions (0..1) of its width and height.</summary>
    public sealed record CropBox(double X, double Y, double Width, double Height);

    /// <summary>
    /// How a landscape file is turned into a 9:16 portrait video, like Twitch's "Create Clip" portrait editor.
    /// "single": one box fills the frame. "split": the top box fills the top <see cref="SplitRatio"/> of the frame and
    /// the bottom box the rest (e.g. facecam over gameplay). Each box is scaled to its slot, so the page keeps the box's
    /// shape matching the slot; a mismatch only stretches the picture slightly.
    /// <see cref="TrimStart"/> / <see cref="TrimEnd"/> are seconds from the start of the entry's file (null = its start / end).
    /// <see cref="Parts"/> (optional): the render is these parts of the file, in order, each with its own layout, joined back to back
    /// (gaps between them are left out). With parts, the other fields mirror them after <see cref="Normalize"/> (the first part's layout,
    /// the first start and last end), so code that only knows one layout (the page's older views) still sees a sensible one.
    /// </summary>
    public sealed record ShortformLayout(string? Mode, CropBox? Top, CropBox? Bottom, double? SplitRatio, double? TrimStart = null, double? TrimEnd = null,
        IReadOnlyList<ShortformPart>? Parts = null)
    {
        public const int MaxParts = 40;
        public const double MinPartSeconds = 0.5;
        public const double MinTrimSeconds = 1;
        public const int OutputWidth = 1080;
        public const int OutputHeight = 1920;
        public const double MinSplitRatio = 0.2;
        public const double MaxSplitRatio = 0.8;
        private const double MinBoxSize = 0.02;
        private const double Tolerance = 0.001;

        [JsonIgnore]
        public bool IsSplit => string.Equals(Mode, "split", StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns an error message, or null if the layout can be rendered from a file this long.</summary>
        public string? Validate(double clipLengthSeconds)
        {
            if (Parts is not null)
                return ValidateParts(clipLengthSeconds);

            if (TrimStart is { } start && (!double.IsFinite(start) || start < 0))
                return "trimStart must be 0 or more seconds.";

            if (TrimEnd is { } end && !double.IsFinite(end))
                return "trimEnd must be a number of seconds.";

            var length = GetDuration(clipLengthSeconds);
            if (length < MinTrimSeconds)
                return $"The trimmed part must be at least {MinTrimSeconds:0} second long.";

            if (!IsSplit && !string.Equals(Mode, "single", StringComparison.OrdinalIgnoreCase))
                return "Mode must be \"single\" or \"split\".";

            if (ValidateBox(Top, "top") is { } topError)
                return topError;

            if (!IsSplit)
                return null;

            if (ValidateBox(Bottom, "bottom") is { } bottomError)
                return bottomError;

            if (SplitRatio is not { } ratio || !double.IsFinite(ratio) || ratio < MinSplitRatio || ratio > MaxSplitRatio)
                return $"splitRatio must be between {MinSplitRatio} and {MaxSplitRatio}.";

            return null;
        }

        private string? ValidateParts(double clipLengthSeconds)
        {
            if (Parts!.Count == 0)
                return "parts must have at least one part (or leave it out).";
            if (Parts.Count > MaxParts)
                return $"A vertical video can have at most {MaxParts} parts.";

            var previousEnd = 0.0;
            for (var i = 0; i < Parts.Count; i++)
            {
                var part = Parts[i];
                if (part is null || !double.IsFinite(part.Start) || !double.IsFinite(part.End) || part.Start < 0)
                    return $"Part {i + 1} needs a start and end in seconds.";
                if (part.End - part.Start < MinPartSeconds - Tolerance)
                    return $"Part {i + 1} must be at least {MinPartSeconds} seconds long.";
                if (part.Start < previousEnd - Tolerance)
                    return "Parts must be in order and must not overlap.";
                if (part.Start >= clipLengthSeconds)
                    return $"Part {i + 1} starts after the end of the clip.";
                if (part.ToLayout().Validate(clipLengthSeconds) is { } error)
                    return $"Part {i + 1}: {error}";
                previousEnd = part.End;
            }

            return GetDuration(clipLengthSeconds) < MinTrimSeconds ? $"The parts must add up to at least {MinTrimSeconds:0} second." : null;
        }

        /// <summary>With parts, sets the single-layout fields from them (first part's layout, first start, last end); otherwise returns itself.</summary>
        public ShortformLayout Normalize()
        {
            if (Parts is not { Count: > 0 })
                return this with { Parts = null };

            var first = Parts[0];
            return this with
            {
                Mode = first.Mode, Top = first.Top, Bottom = first.Bottom, SplitRatio = first.SplitRatio,
                TrimStart = first.Start > 0 ? first.Start : null, TrimEnd = Parts[^1].End,
            };
        }

        /// <summary>What the render is made of, in order: the parts, or this layout alone.</summary>
        public IReadOnlyList<ShortformLayout> Pieces() => Parts is { Count: > 0 } ? Parts.Select(x => x.ToLayout()).ToList() : [this];

        /// <summary>A short description for the audit log: "single", "split" or "3 parts".</summary>
        [JsonIgnore]
        public string Summary => Parts is { Count: > 1 } ? $"{Parts.Count} parts" : Pieces()[0].IsSplit ? "split" : "single";

        private static string? ValidateBox(CropBox? box, string name)
        {
            if (box is null)
                return $"The {name} box is required.";

            double[] values = [box.X, box.Y, box.Width, box.Height];
            if (values.Any(v => !double.IsFinite(v))
                || box.X < -Tolerance || box.Y < -Tolerance
                || box.Width < MinBoxSize || box.Height < MinBoxSize
                || box.X + box.Width > 1 + Tolerance || box.Y + box.Height > 1 + Tolerance)
                return $"The {name} box must be inside the video (fractions of its size, at least {MinBoxSize} wide and tall).";

            return null;
        }

        /// <summary>Start of the rendered part, in seconds from the start of the file.</summary>
        public double GetStart() => TrimStart is > 0 ? TrimStart.Value : 0;

        /// <summary>
        /// Length of the rendered part. A trim end isn't capped at <paramref name="clipLengthSeconds"/>: Safe-mode files run a
        /// little longer than the entry's length, and ffmpeg simply stops at the end of the file.
        /// </summary>
        public double GetDuration(double clipLengthSeconds) =>
            Parts is { Count: > 0 } ? Parts.Sum(x => x.End - x.Start) : (TrimEnd ?? clipLengthSeconds) - GetStart();

        /// <summary>True if the render covers less than the whole file.</summary>
        [JsonIgnore]
        public bool IsTrimmed => GetStart() > 0 || TrimEnd is not null;

        /// <summary>Output height of the top slot in split mode (even, as H.264 with 4:2:0 needs).</summary>
        [JsonIgnore]
        public int TopHeight => IsSplit ? (int)Math.Round(OutputHeight * SplitRatio!.Value / 2) * 2 : OutputHeight;

        /// <summary>The ffmpeg filter graph, with "[v]" as the output pad. Call only on a validated layout.</summary>
        public string BuildFilterGraph()
        {
            if (!IsSplit)
                return $"[0:v]{CropScale(Top!, OutputHeight)},format=yuv420p[v]";

            var topHeight = TopHeight;
            return $"[0:v]split=2[a][b];[a]{CropScale(Top!, topHeight)}[top];[b]{CropScale(Bottom!, OutputHeight - topHeight)}[bottom];"
                   + "[top][bottom]vstack=inputs=2,format=yuv420p[v]";
        }

        // Fractions of iw/ih keep this independent of the source resolution, so no ffprobe is needed.
        // Invariant culture: a decimal comma would break the filter graph.
        private static string CropScale(CropBox box, int height)
        {
            var x = Clamp01(box.X);
            var y = Clamp01(box.Y);
            var w = Math.Min(box.Width, 1 - x);
            var h = Math.Min(box.Height, 1 - y);
            return string.Create(CultureInfo.InvariantCulture,
                $"crop=iw*{w:0.######}:ih*{h:0.######}:iw*{x:0.######}:ih*{y:0.######},scale={OutputWidth}:{height},setsar=1");
        }

        private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    }

    /// <summary>One part of a vertical video: seconds <see cref="Start"/> to <see cref="End"/> of the entry's file with its own layout.</summary>
    public sealed record ShortformPart(double Start, double End, string? Mode, CropBox? Top, CropBox? Bottom, double? SplitRatio)
    {
        public ShortformLayout ToLayout() => new(Mode, Top, Bottom, SplitRatio, Start, End);
    }
}
