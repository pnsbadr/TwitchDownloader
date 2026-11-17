using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Projects
{
    /// <summary>
    /// Player 1 changes flow into Player 2 edits (badrfly 2026-11-17, thread "Sync Player 1 trims into Player 2 edits"). When a clip's or
    /// segment's kept range changes (a trim, cut-out parts, an undo on Player 1, an apply/revert, a live markers entry moving), every edit
    /// (project) of the team with pieces copied from it follows:
    /// <list type="bullet">
    /// <item>what the item no longer keeps is cut out of those pieces, and later pieces on the same row slide left by what was removed (gaps the
    /// member made stay as they are);</item>
    /// <item>what the item keeps again (an undo, or a trim made longer) grows the piece that ends (or starts) right where it was cut, and later
    /// pieces slide right. Two pieces that a cut had split, still next to each other, become one again.</item>
    /// </list>
    /// A range moved to somewhere else of the VOD (nothing in common with before) leaves edits alone. A video's pieces follow its segments
    /// (with the video's own cut-out parts left out). The page mirrors <see cref="Apply"/> in <c>ui/project.js</c> (<c>applySourceSync</c>)
    /// to keep its undo history in step, and learns about each sync from the <c>X-Source-Sync</c> response header.
    /// </summary>
    public static class ProjectSourceSync
    {
        public const string HeaderName = "X-Source-Sync";

        /// <summary>How close two times must be to count as the same point (piece times keep 3 decimals, cut-out parts 2).</summary>
        public const double Touch = 0.05;

        private const double Sliver = 0.01;

        /// <summary>One change of what a source keeps: pieces from <see cref="Kind"/> <see cref="Id"/> (for a video only those within
        /// <see cref="Within"/>, the segment's range before) lose <see cref="Removed"/> and may grow into <see cref="Added"/> (VOD seconds).</summary>
        public sealed record Rule(
            [property: JsonPropertyName("kind")] string Kind,
            [property: JsonPropertyName("id")] long Id,
            [property: JsonPropertyName("vodId")] long VodId,
            [property: JsonPropertyName("within")] Span? Within,
            [property: JsonPropertyName("removed")] IReadOnlyList<Span> Removed,
            [property: JsonPropertyName("added")] IReadOnlyList<Span> Added);

        /// <param name="Inner">An added range between two parts the item kept before (a cut-out part taken back): it only joins two pieces
        /// a cut had split and that are still next to each other, never fills a gap the member left. Ranges past the old ends grow the piece
        /// at that end.</param>
        public sealed record Span(
            [property: JsonPropertyName("start")] double Start,
            [property: JsonPropertyName("end")] double End,
            [property: JsonPropertyName("inner")] bool Inner = false);

        /// <summary>What one request synced: the rules, and the edits changed with their new revisions (the page's open edit takes it).</summary>
        public sealed record Change(
            [property: JsonPropertyName("rules")] IReadOnlyList<Rule> Rules,
            [property: JsonPropertyName("projects")] IReadOnlyList<ProjectRevision> Projects);

        public sealed record ProjectRevision(
            [property: JsonPropertyName("id")] int Id,
            [property: JsonPropertyName("revision")] int Revision);

        /// <summary>A source whose kept range changed, read from the change tracker before saving.</summary>
        public sealed record Pending(int TeamId, string Kind, int Id, long VodId, IReadOnlyList<Span> OldKept, IReadOnlyList<Span> NewKept);

        // ---------- ranges ----------

        public static IReadOnlyList<Span> Kept(double start, double end, string? cutsJson) =>
            CutOuts.Kept(start, end, CutOuts.Read(cutsJson)).Select(x => new Span(x.Start, x.End)).ToList();

        /// <summary>a minus b (both sorted, non-overlapping); parts under <see cref="Sliver"/> are dropped.</summary>
        public static List<Span> Minus(IReadOnlyList<Span> a, IReadOnlyList<Span> b)
        {
            var result = new List<Span>();
            foreach (var span in a)
            {
                var pieces = new List<Span> { span };
                foreach (var cut in b)
                {
                    var next = new List<Span>();
                    foreach (var p in pieces)
                    {
                        if (cut.End <= p.Start || cut.Start >= p.End)
                        {
                            next.Add(p);
                            continue;
                        }
                        if (cut.Start > p.Start) next.Add(new Span(p.Start, cut.Start));
                        if (cut.End < p.End) next.Add(new Span(cut.End, p.End));
                    }
                    pieces = next;
                }
                result.AddRange(pieces.Where(x => x.End - x.Start >= Sliver));
            }
            return result.OrderBy(x => x.Start).ToList();
        }

        private static bool Overlaps(IReadOnlyList<Span> a, IReadOnlyList<Span> b) =>
            a.Any(x => b.Any(y => x.Start < y.End - Sliver && y.Start < x.End - Sliver));

        /// <summary>The rule for a source's change, or null when there's nothing to follow (no change, or moved somewhere else entirely).</summary>
        public static Rule? RuleFor(string kind, long id, long vodId, IReadOnlyList<Span> oldKept, IReadOnlyList<Span> newKept, Span? within = null)
        {
            if (oldKept.Count == 0 || newKept.Count == 0 || !Overlaps(oldKept, newKept))
                return null;
            var removed = Minus(oldKept, newKept);
            var added = Minus(newKept, oldKept)
                .Select(r => r with { Inner = r.Start > oldKept[0].Start + Touch && r.End < oldKept[^1].End - Touch }).ToList();
            return removed.Count == 0 && added.Count == 0 ? null : new Rule(kind, id, vodId, within, removed, added);
        }

        // ---------- the content ----------

        private static double Round(double x) => Math.Round(x * 1000) / 1000;

        private static bool Matches(ProjectPiece piece, Rule rule) =>
            piece.Source is { } source && source.Kind == rule.Kind && source.Id == rule.Id && piece.VodId == rule.VodId
            && (rule.Within is not { } within || (piece.Start >= within.Start - Touch && piece.End <= within.End + Touch));

        /// <summary>The content with the rule applied, or null when nothing changes (or the result couldn't be saved).</summary>
        public static ProjectContent? Apply(ProjectContent content, Rule rule)
        {
            var pieces = content.Pieces ?? [];
            if (!pieces.Any(x => x is not null && Matches(x, rule)))
                return null;

            var result = pieces.ToList();
            if (rule.Removed.Count > 0)
                result = RemoveFrom(result, rule);
            if (rule.Added.Count > 0)
                result = AddTo(result, rule);

            if (result.Count == pieces.Count && result.All(pieces.Contains))
                return null;
            var changed = content with { Pieces = result };
            return changed.Validate() is null ? changed : null;
        }

        private static IEnumerable<IGrouping<string, ProjectPiece>> ByTrack(IEnumerable<ProjectPiece> pieces) =>
            pieces.Where(x => x is not null).GroupBy(x => x.TrackId);

        private static List<ProjectPiece> RemoveFrom(List<ProjectPiece> pieces, Rule rule)
        {
            var result = new List<ProjectPiece>();
            foreach (var track in ByTrack(pieces))
            {
                var shift = 0.0;
                foreach (var piece in track.OrderBy(x => x.At))
                {
                    var at = Round(piece.At - shift);
                    if (!Matches(piece, rule))
                    {
                        result.Add(piece with { At = at });
                        continue;
                    }
                    var parts = Minus([new Span(piece.Start, piece.End)], rule.Removed)
                        .Where(x => x.End - x.Start >= ProjectContent.MinPieceSeconds - 0.0005).ToList();
                    var kept = 0.0;
                    for (var i = 0; i < parts.Count; i++)
                    {
                        var part = parts[i];
                        result.Add(piece with
                        {
                            Id = i == 0 ? piece.Id : $"{(piece.Id.Length > 24 ? piece.Id[..24] : piece.Id)}s{i}{Random.Shared.Next(4096):x}",
                            At = Round(at + kept),
                            Start = Round(part.Start),
                            End = Round(part.End),
                        });
                        kept += Round(part.End) - Round(part.Start);
                    }
                    shift += (piece.End - piece.Start) - kept;
                }
            }
            return result;
        }

        private static List<ProjectPiece> AddTo(List<ProjectPiece> pieces, Rule rule)
        {
            var result = new List<ProjectPiece>();
            var used = new HashSet<Span>();
            foreach (var track in ByTrack(pieces))
            {
                var ordered = track.OrderBy(x => x.At).ToList();
                var shift = 0.0;
                for (var i = 0; i < ordered.Count; i++)
                {
                    var piece = ordered[i];
                    var at = Round(piece.At + shift);
                    if (!Matches(piece, rule))
                    {
                        result.Add(piece with { At = at });
                        continue;
                    }
                    var start = piece.Start;
                    var end = piece.End;
                    var grow = 0.0;
                    // The next piece, when a cut that's gone now had split it from this one (still right after it, same source)
                    var next = i + 1 < ordered.Count && Matches(ordered[i + 1], rule)
                        && Math.Abs(ordered[i + 1].At - (piece.At + piece.End - piece.Start)) < Touch ? ordered[i + 1] : null;
                    if (rule.Added.FirstOrDefault(r => !used.Contains(r) && Math.Abs(r.Start - end) < Touch
                        && (!r.Inner || (next is not null && Math.Abs(next.Start - r.End) < Touch))) is { } after)
                    {
                        used.Add(after);
                        grow += after.End - after.Start;
                        end = after.End;
                        if (after.Inner)
                        {
                            // They're one piece again
                            end = next!.End;
                            i++;
                        }
                    }
                    else if (rule.Added.FirstOrDefault(r => !used.Contains(r) && !r.Inner && Math.Abs(r.End - start) < Touch) is { } before)
                    {
                        used.Add(before);
                        grow += before.End - before.Start;
                        start = before.Start;
                    }
                    result.Add(piece with { At = at, Start = Round(start), End = Round(end) });
                    shift += grow;
                }
            }
            return result;
        }

        // ---------- the database ----------

        /// <summary>Clips and segments whose kept range is about to change in this save.</summary>
        public static List<Pending> Collect(LibraryDbContext db)
        {
            var pending = new List<Pending>();
            foreach (var entry in db.ChangeTracker.Entries<LibraryEntry>().Where(x => x.State == EntityState.Modified))
            {
                var e = entry.Entity;
                if (e.Kind != EntryKind.Vod || e.VodId <= 0)
                    continue;
                var oldStart = entry.Property(x => x.StartTime).OriginalValue;
                var oldEnd = entry.Property(x => x.EndTime).OriginalValue;
                var oldCuts = entry.Property(x => x.Cuts).OriginalValue;
                var oldLength = entry.Property(x => x.VodLengthSeconds).OriginalValue;
                if (oldStart == e.StartTime && oldEnd == e.EndTime && oldCuts == e.Cuts)
                    continue;
                var oldRangeEnd = oldEnd?.TotalSeconds ?? oldLength;
                var newRangeEnd = e.EndTime?.TotalSeconds ?? e.VodLengthSeconds;
                pending.Add(new Pending(e.TeamId, "clip", e.Id, e.VodId,
                    Kept(oldStart.TotalSeconds, oldRangeEnd, oldCuts), Kept(e.StartTime.TotalSeconds, newRangeEnd, e.Cuts)));
            }

            foreach (var entry in db.ChangeTracker.Entries<VodMarker>().Where(x => x.State == EntityState.Modified))
            {
                var m = entry.Entity;
                var oldStart = entry.Property(x => x.Seconds).OriginalValue;
                var oldEnd = entry.Property(x => x.EndSeconds).OriginalValue;
                var oldCuts = entry.Property(x => x.Cuts).OriginalValue;
                if (oldEnd is not { } before || m.EndSeconds is not { } after)
                    continue;
                if (oldStart == m.Seconds && before == after && oldCuts == m.Cuts)
                    continue;
                pending.Add(new Pending(m.TeamId, "segment", m.Id, m.VodId, Kept(oldStart, before, oldCuts), Kept(m.Seconds, after, m.Cuts)));
            }
            return pending;
        }

        /// <summary>Makes the team's edits follow the changes (after they were saved). Returns what changed, or null.</summary>
        public static async Task<Change?> ApplyAsync(LibraryDbContext db, IReadOnlyList<Pending> pending, CancellationToken cancellationToken)
        {
            var rules = new List<(int TeamId, Rule Rule)>();
            foreach (var p in pending)
            {
                if (RuleFor(p.Kind, p.Id, p.VodId, p.OldKept, p.NewKept) is { } rule)
                    rules.Add((p.TeamId, rule));
                if (p.Kind != "segment")
                    continue;
                // Videos with this segment: their pieces of it follow too (without the video's own cut-out parts)
                var links = await db.VideoSegments.AsNoTracking().Where(x => x.MarkerId == p.Id)
                    .Select(x => new { x.VideoId, x.Cuts }).ToListAsync(cancellationToken);
                foreach (var link in links)
                {
                    var videoCuts = CutOuts.Read(link.Cuts).Select(x => new Span(x.Start, x.End)).ToList();
                    var oldRange = new Span(p.OldKept[0].Start, p.OldKept[^1].End);
                    if (RuleFor("video", link.VideoId, p.VodId, Minus(p.OldKept, videoCuts), Minus(p.NewKept, videoCuts), oldRange) is { } videoRule)
                        rules.Add((p.TeamId, videoRule));
                }
            }
            if (rules.Count == 0)
                return null;

            var changed = new List<ProjectRevision>();
            foreach (var group in rules.GroupBy(x => (x.TeamId, x.Rule.VodId)))
            {
                var (teamId, vodId) = group.Key;
                for (var attempt = 0; ; attempt++)
                {
                    var projects = await db.Projects.IgnoreQueryFilters().AsNoTracking()
                        .Where(x => x.TeamId == teamId && (x.VodId == vodId || x.Vods.Any(v => v.VodId == vodId)))
                        .ToListAsync(cancellationToken);
                    var touched = new List<EditProject>();
                    foreach (var loaded in projects)
                    {
                        var content = ProjectContent.Read(loaded.Content);
                        var any = false;
                        foreach (var (_, rule) in group)
                        {
                            if (Apply(content, rule) is { } next)
                            {
                                content = next;
                                any = true;
                            }
                        }
                        if (!any)
                            continue;
                        // Attached as read, so saving checks the revision it had (a page's save in between → read again)
                        var project = db.ChangeTracker.Entries<EditProject>().FirstOrDefault(x => x.Entity.Id == loaded.Id)?.Entity
                            ?? db.Projects.Attach(loaded).Entity;
                        project.Content = content.Write();
                        project.LengthSeconds = content.LengthSeconds;
                        project.PieceCount = content.Pieces?.Count ?? 0;
                        project.Revision++;
                        touched.Add(project);
                    }
                    if (touched.Count == 0)
                        break;
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                        changed.AddRange(touched.Select(x => new ProjectRevision(x.Id, x.Revision)));
                        break;
                    }
                    catch (DbUpdateConcurrencyException) when (attempt < 3)
                    {
                        // A page saved one of these edits in between: read them again and redo it on the newer content
                        foreach (var project in touched)
                            db.Entry(project).State = EntityState.Detached;
                    }
                }
            }
            return new Change(rules.Select(x => x.Rule).ToList(), changed);
        }
    }
}
