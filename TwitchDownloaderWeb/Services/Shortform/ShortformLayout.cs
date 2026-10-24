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
    /// </summary>
    public sealed record ShortformLayout(string? Mode, CropBox? Top, CropBox? Bottom, double? SplitRatio)
    {
        public const int OutputWidth = 1080;
        public const int OutputHeight = 1920;
        public const double MinSplitRatio = 0.2;
        public const double MaxSplitRatio = 0.8;
        private const double MinBoxSize = 0.02;
        private const double Tolerance = 0.001;

        [JsonIgnore]
        public bool IsSplit => string.Equals(Mode, "split", StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns an error message, or null if the layout can be rendered.</summary>
        public string? Validate()
        {
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
}
