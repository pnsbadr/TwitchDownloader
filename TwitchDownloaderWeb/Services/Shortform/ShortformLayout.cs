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
    /// <see cref="Keys"/> (optional, "moving boxes"): the boxes at given times (seconds into the entry's file). Between two keys the boxes
    /// glide from one to the other (eased in and out), before the first and after the last they stay put. Mode and split ratio don't change.
    /// </summary>
    public sealed record ShortformLayout(string? Mode, CropBox? Top, CropBox? Bottom, double? SplitRatio, double? TrimStart = null, double? TrimEnd = null,
        IReadOnlyList<ShortformPart>? Parts = null, IReadOnlyList<ShortformKey>? Keys = null)
    {
        /// <summary>Request only (<c>POST /shortform</c>): render a saved edit of the clip. Not stored in the layout (the render keeps it).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? EditId { get; init; }

        public const int MaxParts = 40;
        public const int MaxKeys = 30;
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

            return ValidateBoxes();
        }

        /// <summary>Checks the mode, boxes and split ratio only (a part's length is checked by <see cref="ValidateParts"/>).</summary>
        private string? ValidateBoxes()
        {
            if (!IsSplit && !string.Equals(Mode, "single", StringComparison.OrdinalIgnoreCase))
                return "Mode must be \"single\" or \"split\".";

            if (ValidateBox(Top, "top") is { } topError)
                return topError;

            if (!IsSplit)
                return ValidateKeys();

            if (ValidateBox(Bottom, "bottom") is { } bottomError)
                return bottomError;

            if (SplitRatio is not { } ratio || !double.IsFinite(ratio) || ratio < MinSplitRatio || ratio > MaxSplitRatio)
                return $"splitRatio must be between {MinSplitRatio} and {MaxSplitRatio}.";

            return ValidateKeys();
        }

        private string? ValidateKeys()
        {
            if (Keys is null)
                return null;
            if (Keys.Count > MaxKeys)
                return $"A part can have at most {MaxKeys} keyframes.";

            var previous = double.NegativeInfinity;
            for (var i = 0; i < Keys.Count; i++)
            {
                var key = Keys[i];
                if (key is null || !double.IsFinite(key.Time) || key.Time < 0)
                    return $"Keyframe {i + 1} needs a time in seconds.";
                if (key.Time <= previous + Tolerance)
                    return "Keyframes must be in time order, at different times.";
                if (ValidateBox(key.Top, "top") is { } topError)
                    return $"Keyframe {i + 1}: {topError}";
                if (IsSplit && ValidateBox(key.Bottom, "bottom") is { } bottomError)
                    return $"Keyframe {i + 1}: {bottomError}";
                previous = key.Time;
            }

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
                // Parts have their own minimum (MinPartSeconds), shorter than a whole render's MinTrimSeconds
                if (part.ToLayout().ValidateBoxes() is { } error)
                    return $"Part {i + 1}: {error}";
                previousEnd = part.End;
            }

            return GetDuration(clipLengthSeconds) < MinTrimSeconds ? $"The parts must add up to at least {MinTrimSeconds:0} second." : null;
        }

        /// <summary>
        /// The layout cut down to <paramref name="kept"/> (file seconds, in order): each part (or the plain layout's trimmed range) keeps only
        /// what lies inside them, split where they leave gaps; pieces under <see cref="MinPartSeconds"/> go. Null when nothing is left.
        /// Keyframe times are file seconds, so they still apply to the pieces. Validate the result (it may have too many parts).
        /// </summary>
        public static ShortformLayout? KeepOnly(ShortformLayout layout, IReadOnlyList<(double Start, double End)> kept, double fileSeconds)
        {
            var parts = layout.Parts is { Count: > 0 }
                ? layout.Parts
                : [new ShortformPart(layout.GetStart(), layout.TrimEnd is { } trimEnd ? Math.Min(trimEnd, fileSeconds) : fileSeconds, layout.Mode, layout.Top,
                    layout.Bottom, layout.SplitRatio, layout.Keys)];
            var pieces = new List<ShortformPart>();
            foreach (var part in parts)
            {
                foreach (var (start, end) in kept)
                {
                    var from = Math.Max(part.Start, start);
                    var to = Math.Min(part.End, end);
                    if (to - from >= MinPartSeconds - Tolerance)
                        pieces.Add(part with { Start = Math.Round(from, 3), End = Math.Round(to, 3) });
                }
            }

            return pieces.Count == 0 ? null : layout with { Parts = pieces, EditId = null };
        }

        /// <summary>With parts, sets the single-layout fields from them (first part's layout, first start, last end); otherwise returns itself.</summary>
        public ShortformLayout Normalize()
        {
            if (Parts is not { Count: > 0 })
                return this with { Parts = null };

            var first = Parts[0];
            return this with
            {
                Mode = first.Mode, Top = first.Top, Bottom = first.Bottom, SplitRatio = first.SplitRatio, Keys = first.Keys,
                TrimStart = first.Start > 0 ? first.Start : null, TrimEnd = Parts[^1].End,
            };
        }

        /// <summary>What the render is made of, in order: the parts, or this layout alone.</summary>
        public IReadOnlyList<ShortformLayout> Pieces() => Parts is { Count: > 0 } ? Parts.Select(x => x.ToLayout()).ToList() : [this];

        /// <summary>A short description for the audit log: "single", "split" or "3 parts".</summary>
        [JsonIgnore]
        public string Summary => (Parts is { Count: > 1 } ? $"{Parts.Count} parts" : Pieces()[0].IsSplit ? "split" : "single")
            + (Pieces().Any(x => x.Keys is { Count: > 1 }) ? ", moving boxes" : "");

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
            // Key times in the filter are seconds into this piece (the render seeks to its start, so t starts at 0 there)
            var start = GetStart();
            var keys = Keys is { Count: > 1 } ? Keys.OrderBy(x => x.Time).ToList() : null;
            List<(double T, CropBox Box)>? Track(Func<ShortformKey, CropBox?> pick) =>
                keys?.Select(x => (x.Time - start, pick(x)!)).ToList();

            if (!IsSplit)
                return $"[0:v]{CropScale(Top!, OutputHeight, Track(x => x.Top))},format=yuv420p[v]";

            var topHeight = TopHeight;
            return $"[0:v]split=2[a][b];[a]{CropScale(Top!, topHeight, Track(x => x.Top))}[top];"
                   + $"[b]{CropScale(Bottom!, OutputHeight - topHeight, Track(x => x.Bottom))}[bottom];"
                   + "[top][bottom]vstack=inputs=2,format=yuv420p[v]";
        }

        // Fractions of iw/ih keep this independent of the source resolution, so no ffprobe is needed.
        // Invariant culture: a decimal comma would break the filter graph.
        private static string CropScale(CropBox box, int height, List<(double T, CropBox Box)>? track)
        {
            if (track is { Count: > 1 } && track.Any(x => x.Box != track[0].Box))
                return MovingCropScale(track, height);
            if (track is { Count: > 0 })
                box = track[0].Box;
            return StaticCropScale(box, height);
        }

        private static string StaticCropScale(CropBox box, int height)
        {
            var x = Clamp01(box.X);
            var y = Clamp01(box.Y);
            var w = Math.Min(box.Width, 1 - x);
            var h = Math.Min(box.Height, 1 - y);
            return string.Create(CultureInfo.InvariantCulture,
                $"crop=iw*{w:0.######}:ih*{h:0.######}:iw*{x:0.######}:ih*{y:0.######},scale={OutputWidth}:{height},setsar=1");
        }

        private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

        private static CropBox Clip(CropBox box)
        {
            var x = Clamp01(box.X);
            var y = Clamp01(box.Y);
            return new CropBox(x, y, Math.Min(box.Width, 1 - x), Math.Min(box.Height, 1 - y));
        }

        /// <summary>
        /// A box that moves (and maybe grows or shrinks) over time. The part of the video any key's box covers is cropped first (a fixed
        /// crop). A box of the same size everywhere is then cropped at a moving x/y. A box that changes size: the frame is scaled every
        /// frame so the box comes out at the slot's size (scale's eval=frame), then the slot is cropped out of it.
        /// </summary>
        private static string MovingCropScale(List<(double T, CropBox Box)> track, int height)
        {
            var boxes = track.Select(x => Clip(x.Box)).ToList();
            var left = boxes.Min(b => b.X);
            var top = boxes.Min(b => b.Y);
            var ux = boxes.Max(b => b.X + b.Width) - left;
            var uy = boxes.Max(b => b.Y + b.Height) - top;
            // Boxes inside the union, as fractions of it
            var inside = boxes.Select(b => new CropBox((b.X - left) / ux, (b.Y - top) / uy, b.Width / ux, b.Height / uy)).ToList();
            var times = track.Select(x => x.T).ToList();
            string Expr(Func<CropBox, double> value) => Ease(times, inside.Select(value).ToList());

            var union = string.Create(CultureInfo.InvariantCulture,
                $"crop=iw*{ux:0.######}:ih*{uy:0.######}:iw*{left:0.######}:ih*{top:0.######}");
            var sameSize = inside.All(b => Math.Abs(b.Width - inside[0].Width) < 1e-6 && Math.Abs(b.Height - inside[0].Height) < 1e-6);
            if (sameSize)
            {
                var w = inside[0].Width;
                var h = inside[0].Height;
                return string.Create(CultureInfo.InvariantCulture,
                    $"{union},crop=iw*{w:0.######}:ih*{h:0.######}:x='iw*({Expr(b => b.X)})':y='ih*({Expr(b => b.Y)})',scale={OutputWidth}:{height},setsar=1");
            }

            // The union scaled so the box is OutputWidth x height, then the box cropped out (x/y kept inside the frame)
            return string.Create(CultureInfo.InvariantCulture,
                $"{union},scale=w='ceil({OutputWidth}/({Expr(b => b.Width)})/2)*2':h='ceil({height}/({Expr(b => b.Height)})/2)*2':eval=frame,"
                + $"crop={OutputWidth}:{height}:x='max(0,min(in_w-out_w,in_w*({Expr(b => b.X)})))':y='max(0,min(in_h-out_h,in_h*({Expr(b => b.Y)})))',setsar=1");
        }

        /// <summary>
        /// An ffmpeg expression of t: values[0] before times[0], values[^1] after the last, and between two keys an eased glide
        /// (smoothstep: slow, faster, slow). The page draws the preview with the same curve.
        /// </summary>
        public static string Ease(IReadOnlyList<double> times, IReadOnlyList<double> values)
        {
            static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            var expression = N(values[^1]);
            for (var i = times.Count - 2; i >= 0; i--)
            {
                var span = times[i + 1] - times[i];
                var delta = values[i + 1] - values[i];
                var u = $"((t-{N(times[i])})/{N(span)})";
                var segment = Math.Abs(delta) < 1e-9 ? N(values[i]) : $"{N(values[i])}+{N(delta)}*{u}*{u}*(3-2*{u})";
                expression = $"if(lt(t,{N(times[i + 1])}),{segment},{expression})";
            }

            return $"if(lt(t,{N(times[0])}),{N(values[0])},{expression})";
        }
    }

    /// <summary>One part of a vertical video: seconds <see cref="Start"/> to <see cref="End"/> of the entry's file with its own layout.</summary>
    public sealed record ShortformPart(double Start, double End, string? Mode, CropBox? Top, CropBox? Bottom, double? SplitRatio,
        IReadOnlyList<ShortformKey>? Keys = null)
    {
        public ShortformLayout ToLayout() => new(Mode, Top, Bottom, SplitRatio, Start, End, Keys: Keys);
    }

    /// <summary>A keyframe of moving boxes: the boxes at <see cref="Time"/> seconds into the entry's file (Top = the one box in "single" mode).</summary>
    public sealed record ShortformKey(double Time, CropBox? Top, CropBox? Bottom);
}
