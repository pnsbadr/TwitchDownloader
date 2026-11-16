using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Projects
{
    /// <summary>
    /// One stretch of what a project plays, in order: a piece's VOD range (<see cref="Piece"/> set) or black, silent time (a gap).
    /// <see cref="Muted"/>: the piece's track is muted, so it plays without sound.
    /// </summary>
    public sealed record ProjectStretch(double At, double Duration, ProjectPiece? Piece, double VodStart, double VodEnd, bool Muted)
    {
        public bool IsGap => Piece is null;
    }

    /// <summary>
    /// What a project plays from 0:00 to its end (<see cref="ProjectContent"/>): at every moment the piece on the highest visible track
    /// covering it, else black. The page's preview follows the same rule.
    /// </summary>
    public static class ProjectTimeline
    {
        /// <summary>Stretches shorter than this (where pieces on different tracks almost meet) go to the stretch before them.</summary>
        public const double MinStretchSeconds = 0.1;

        public static IReadOnlyList<ProjectStretch> Flatten(ProjectContent content)
        {
            var tracks = content.Tracks ?? [];
            var order = tracks.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
            var hidden = tracks.Where(x => x.Hidden).Select(x => x.Id).ToHashSet();
            var muted = tracks.Where(x => x.Muted).Select(x => x.Id).ToHashSet();
            var pieces = (content.Pieces ?? []).Where(x => !hidden.Contains(x.TrackId) && order.ContainsKey(x.TrackId)).ToList();
            if (pieces.Count == 0)
                return [];

            static double EndOf(ProjectPiece piece) => piece.At + (piece.End - piece.Start);

            var cuts = pieces.SelectMany(x => new[] { x.At, EndOf(x) }).Append(0).Distinct().OrderBy(x => x).ToList();
            var raw = new List<ProjectStretch>();
            for (var i = 0; i + 1 < cuts.Count; i++)
            {
                var (from, to) = (cuts[i], cuts[i + 1]);
                if (to - from < 1e-6)
                    continue;
                var middle = (from + to) / 2;
                var top = pieces.Where(x => x.At <= middle && EndOf(x) > middle).MinBy(x => order[x.TrackId]);
                raw.Add(top is null
                    ? new ProjectStretch(from, to - from, null, 0, 0, false)
                    : new ProjectStretch(from, to - from, top, top.Start + (from - top.At), top.Start + (to - top.At), muted.Contains(top.TrackId)));
            }

            // Join neighbors that continue each other (the same piece, or two gaps)
            var joined = new List<ProjectStretch>();
            foreach (var stretch in raw)
            {
                if (joined.Count > 0 && joined[^1] is var last && last.Piece == stretch.Piece
                    && (last.IsGap || Math.Abs(last.VodEnd - stretch.VodStart) < 1e-6))
                    joined[^1] = last with { Duration = last.Duration + stretch.Duration, VodEnd = stretch.VodEnd };
                else
                    joined.Add(stretch);
            }

            // Slivers go to the stretch before (or after, at the very start), so nothing is cut too short to encode
            var result = new List<ProjectStretch>();
            foreach (var stretch in joined)
            {
                if (stretch.Duration >= MinStretchSeconds || (result.Count == 0 && joined.Count == 1))
                    result.Add(stretch);
                else if (result.Count > 0)
                    result[^1] = Extend(result[^1], stretch.Duration);
                else
                    result.Add(stretch); // merged into the next one below
            }
            if (result.Count > 1 && result[0].Duration < MinStretchSeconds)
            {
                var first = result[0];
                result.RemoveAt(0);
                result[0] = result[0].IsGap
                    ? result[0] with { At = first.At, Duration = result[0].Duration + first.Duration }
                    : result[0] with { At = first.At, Duration = result[0].Duration + first.Duration, VodStart = result[0].VodStart - first.Duration };
            }

            return result;
        }

        private static ProjectStretch Extend(ProjectStretch stretch, double seconds) =>
            stretch.IsGap ? stretch with { Duration = stretch.Duration + seconds } : stretch with { Duration = stretch.Duration + seconds, VodEnd = stretch.VodEnd + seconds };
    }
}
