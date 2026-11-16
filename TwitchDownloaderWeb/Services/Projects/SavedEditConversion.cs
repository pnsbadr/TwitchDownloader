using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Services.Projects
{
    /// <summary>
    /// Turns saved edits (<see cref="ItemEdit"/>, the old Player 2) into projects (badrfly 2026-11-16: "saved edits turn into projects"). Runs at
    /// startup; each edit is converted once (<see cref="EditProject.ConvertedFromEditId"/>), so deleting the project doesn't bring it back. The
    /// edits themselves are kept. A project gets one row with one piece per part the edit keeps (a video: per segment, in play order; the edit's
    /// splits split pieces too), placed end to end from 0:00, owned by the edit's maker. A clip edit's vertical layout goes on its pieces:
    /// each piece takes the layout part under its middle, keyframes moved from file seconds to VOD seconds.
    /// </summary>
    public static class SavedEditConversion
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static async Task<int> RunAsync(LibraryDbContext db, ILogger logger, CancellationToken cancellationToken = default)
        {
            var doneSet = (await db.Projects.IgnoreQueryFilters().Where(x => x.ConvertedFromEditId != null)
                .Select(x => x.ConvertedFromEditId!.Value).ToListAsync(cancellationToken)).ToHashSet();
            var edits = (await db.ItemEdits.IgnoreQueryFilters().OrderBy(x => x.Id).ToListAsync(cancellationToken))
                .Where(x => !doneSet.Contains(x.Id)).ToList();
            if (edits.Count == 0)
                return 0;

            var made = 0;
            foreach (var edit in edits)
            {
                ProjectContent? content = null;
                string title = edit.Title;
                try
                {
                    (content, title) = await BuildAsync(db, edit, cancellationToken);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    logger.LogWarning("Saved edit {EditId} could not be turned into a project: {Error}", edit.Id, ex.Message);
                }

                var now = DateTime.UtcNow;
                var project = new EditProject
                {
                    TeamId = edit.TeamId,
                    UserId = edit.CreatedByUserId,
                    Title = Truncate(title, EditProject.MaxTitleLength),
                    Revision = 1,
                    ConvertedFromEditId = edit.Id,
                    CreatedAtUtc = edit.CreatedAtUtc,
                    UpdatedAtUtc = now,
                };
                // An edit that keeps nothing (or can't be read) makes no project; it's looked at again next startup (cheap: edits are
                // deleted with their item)
                if (content is null || content.Pieces is not { Count: > 0 })
                    continue;
                if (content.Validate() is { } error)
                {
                    logger.LogWarning("Saved edit {EditId} made an invalid project ({Error}); skipped", edit.Id, error);
                    continue;
                }

                project.Content = content.Write();
                project.LengthSeconds = content.LengthSeconds;
                project.PieceCount = content.Pieces.Count;
                foreach (var vodId in content.VodIds)
                    project.Vods.Add(new ProjectVod { VodId = vodId });
                db.Projects.Add(project);
                made++;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (made > 0)
                logger.LogInformation("Turned {Count} saved edit(s) into projects", made);
            return made;
        }

        private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

        private sealed record Range(double Start, double End, string Label, string Color, ProjectSource Source, ProjectLayout? Layout = null);

        /// <summary>The project for one edit (null if its item is gone) and its title.</summary>
        public static async Task<(ProjectContent? Content, string Title)> BuildAsync(LibraryDbContext db, ItemEdit edit, CancellationToken cancellationToken)
        {
            var ranges = new List<Range>();
            string itemTitle;
            long vodId;
            ShortformLayout? layout = null;
            double fileStart = 0;
            var splits = Splits(edit.Layout);

            if (edit.EntryId is { } entryId)
            {
                var entry = await db.Entries.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == entryId, cancellationToken);
                if (entry is null || entry.VodId <= 0)
                    return (null, edit.Title);
                itemTitle = entry.Title;
                vodId = entry.VodId;
                var start = entry.StartTime.TotalSeconds;
                var end = entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds;
                foreach (var (s, e) in CutOuts.Kept(start, end, ItemEdits.ReadCuts(edit.Cuts, start, end)))
                    ranges.Add(new Range(s, e, entry.Title, "violet", new ProjectSource("clip", entry.Id)));
                // The vertical layout is in seconds of the clip's file, which holds the whole range (an older file that skips the clip's
                // cut-out parts would need mapping; its layout is left out)
                if (entry.FileCuts is null)
                {
                    layout = ReadLayout(edit.Layout);
                    fileStart = start;
                }
            }
            else if (edit.MarkerId is { } markerId)
            {
                var marker = await db.Markers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == markerId, cancellationToken);
                if (marker?.EndSeconds is not { } end)
                    return (null, edit.Title);
                itemTitle = string.IsNullOrWhiteSpace(marker.Label) ? "Segment" : marker.Label;
                vodId = marker.VodId;
                foreach (var (s, e) in CutOuts.Kept(marker.Seconds, end, ItemEdits.ReadCuts(edit.Cuts, marker.Seconds, end)))
                    ranges.Add(new Range(s, e, itemTitle, marker.Color, new ProjectSource("segment", marker.Id)));
            }
            else if (edit.VideoId is { } videoId)
            {
                var video = await db.Videos.IgnoreQueryFilters().Include(x => x.Segments).ThenInclude(x => x.Marker)
                    .FirstOrDefaultAsync(x => x.Id == videoId, cancellationToken);
                if (video is null)
                    return (null, edit.Title);
                itemTitle = video.Title;
                vodId = video.VodId;
                var parts = ItemEditPart.Read(edit.Parts);
                foreach (var link in video.Segments.OrderBy(x => x.Position))
                {
                    if (link.Marker?.EndSeconds is not { } end)
                        continue;
                    var marker = link.Marker;
                    var stored = parts.FirstOrDefault(x => x.MarkerId == marker.Id)?.Cuts ?? [];
                    var cuts = CutOuts.Read(marker.Cuts).Concat(ItemEdits.ReadCuts(CutOuts.Write(stored.ToList()), marker.Seconds, end));
                    var label = string.IsNullOrWhiteSpace(marker.Label) ? video.Title : $"{video.Title} · {marker.Label}";
                    foreach (var (s, e) in CutOuts.Kept(marker.Seconds, end, cuts))
                        ranges.Add(new Range(s, e, label, marker.Color, new ProjectSource("video", video.Id)));
                }
            }
            else
            {
                return (null, edit.Title);
            }

            // Splits made in the old Player 2 split pieces here too
            var pieces = new List<ProjectPiece>();
            var at = 0.0;
            var n = 0;
            foreach (var range in ranges)
            {
                var edges = new List<double> { range.Start };
                edges.AddRange(splits.Where(t => t > range.Start + ProjectContent.MinPieceSeconds && t < range.End - ProjectContent.MinPieceSeconds).Order());
                edges.Add(range.End);
                for (var i = 0; i + 1 < edges.Count; i++)
                {
                    var (s, e) = (Math.Round(edges[i], 3), Math.Round(edges[i + 1], 3));
                    if (e - s < ProjectContent.MinPieceSeconds)
                        continue;
                    var pieceLayout = layout is null ? null : LayoutFor(layout, fileStart, s, e);
                    var color = VodMarker.Colors.Contains(range.Color) ? range.Color : null;
                    pieces.Add(new ProjectPiece($"e{edit.Id}p{++n}", "t1", Math.Round(at, 3), vodId, s, e,
                        new ProjectSource(range.Source.Kind, range.Source.Id), Truncate(range.Label, ProjectContent.MaxLabelLength), color, pieceLayout));
                    at += e - s;
                }
            }

            var title = string.IsNullOrWhiteSpace(edit.Title) ? $"{itemTitle} (edit)" : $"{itemTitle} · {edit.Title}";
            return (new ProjectContent([new ProjectTrack("t1", null, false, false, false)], pieces), title);
        }

        private static ShortformLayout? ReadLayout(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var layout = JsonSerializer.Deserialize<ShortformLayout>(json, Json);
                return layout is { Top: not null } || layout?.Parts is { Count: > 0 } ? layout : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Every split time (VOD seconds) in the old layout's <c>splits</c> (<c>{ partId: [seconds] }</c>).</summary>
        private static IReadOnlyList<double> Splits(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return [];
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("splits", out var splits)
                    || splits.ValueKind != JsonValueKind.Object)
                    return [];
                var times = new List<double>();
                foreach (var part in splits.EnumerateObject())
                    if (part.Value.ValueKind == JsonValueKind.Array)
                        foreach (var t in part.Value.EnumerateArray())
                            if (t.ValueKind == JsonValueKind.Number && double.IsFinite(t.GetDouble()))
                                times.Add(t.GetDouble());
                return times;
            }
            catch (JsonException)
            {
                return [];
            }
        }

        /// <summary>The layout of the part under the piece's middle (file seconds = VOD seconds - <paramref name="fileStart"/>), keys in VOD seconds.</summary>
        private static ProjectLayout? LayoutFor(ShortformLayout layout, double fileStart, double start, double end)
        {
            var middle = (start + end) / 2 - fileStart;
            string? mode;
            CropBox? top, bottom;
            double? ratio;
            IReadOnlyList<ShortformKey>? keys;
            if (layout.Parts is { Count: > 0 } parts)
            {
                var part = parts.FirstOrDefault(x => middle >= x.Start && middle < x.End)
                    ?? parts.MinBy(x => Math.Min(Math.Abs(middle - x.Start), Math.Abs(middle - x.End)))!;
                (mode, top, bottom, ratio, keys) = (part.Mode, part.Top, part.Bottom, part.SplitRatio, part.Keys);
            }
            else
            {
                (mode, top, bottom, ratio, keys) = (layout.Mode, layout.Top, layout.Bottom, layout.SplitRatio, layout.Keys);
            }
            if (top is null)
                return null;
            var moved = keys is { Count: > 0 }
                ? keys.Select(k => new ShortformKey(Math.Round(k.Time + fileStart, 3), k.Top, k.Bottom)).ToList()
                : null;
            var result = new ProjectLayout(mode, top, bottom, ratio, moved);
            return result.Validate() is null ? result : new ProjectLayout(mode, top, bottom, ratio);
        }
    }
}
