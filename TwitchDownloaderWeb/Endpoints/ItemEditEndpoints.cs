using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="Cuts">Clip or segment: everything the edit removes, trimmed ends included (VOD seconds, inside the item's full range).</param>
    /// <param name="Parts">Video: cuts per segment (a segment taken out whole = one cut over its range). Segments not listed have none.</param>
    /// <param name="Layout">The vertical editor's layout (a JSON object); <c>{}</c> removes it. On PUT a missing field keeps what's there.</param>
    public sealed record EditRequest(int? EntryId, int? MarkerId, int? VideoId, string? Title, IReadOnlyList<CutRange>? Cuts,
        IReadOnlyList<ItemEditPart>? Parts, JsonElement? Layout);

    public sealed record EditCopyRequest(string? Title);

    /// <param name="RowId">Only when the edit is applied to a copy (someone else's item without EditAnyEntries): the caller's row for the copy.</param>
    public sealed record EditApplyRequest(int? RowId);

    /// <param name="To">"previous" (undo the last apply, the default) or "original" (undo every apply).</param>
    public sealed record RevertRequest(string? To);

    /// <summary>A video's segment as the edit sees it: its full range and its own cut-out parts (a video edit cuts on top of those).</summary>
    public sealed record EditSourcePartDto(int MarkerId, string Label, double Start, double End, IReadOnlyList<CutRange> Cuts);

    /// <param name="Cuts">Clip/segment: the edit's cuts against the item's current full range.</param>
    /// <param name="Parts">Video: every segment of the video in play order with the edit's cuts on it ([] = none).</param>
    /// <param name="KeptSeconds">What the edit plays.</param>
    /// <param name="SourceStart">Clip/segment: the item's full range; null for a video (see <paramref name="SourceParts"/>).</param>
    /// <param name="SourceParts">Video: its segments in play order.</param>
    public sealed record EditDto(int Id, string Kind, int? EntryId, int? MarkerId, int? VideoId, long VodId, string Title, int CreatedByUserId,
        string? CreatedBy, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, IReadOnlyList<CutRange> Cuts, IReadOnlyList<ItemEditPart> Parts,
        JsonElement? Layout, double KeptSeconds, double? SourceStart, double? SourceEnd, IReadOnlyList<EditSourcePartDto> SourceParts,
        bool CanEdit, bool CanDelete);

    public sealed record ItemApplyDto(int Id, int? EditId, string EditTitle, int UserId, string? Username, DateTime AppliedAtUtc);

    /// <param name="Edits">The whole team's edits of the item, newest first.</param>
    /// <param name="Applies">Applies that can be undone, newest first.</param>
    /// <param name="CanApply">The caller may apply edits (to the item itself, or to a copy: see <paramref name="ApplyMakesCopy"/>).</param>
    /// <param name="ApplyMakesCopy">The caller may not change this item, so applying copies it onto their row first.</param>
    /// <param name="CanRevert">The caller may undo applies (they may change the item and there is something to undo).</param>
    public sealed record EditListDto(IReadOnlyList<EditDto> Edits, IReadOnlyList<ItemApplyDto> Applies, bool CanApply, bool ApplyMakesCopy, bool CanRevert);

    /// <param name="Item">The entry, marker or video as its own endpoints return it (the copy when <paramref name="Copied"/>).</param>
    /// <param name="Edit">The edit that was applied (on a copy: the edit's copy, which belongs to the caller).</param>
    public sealed record EditApplyResultDto(string Kind, object Item, bool Copied, EditDto Edit);

    /// <summary>
    /// Saved edits in Player 2 (badrfly, 2026-11-16; see <see cref="ItemEdit"/>): versions of a clip, segment or video kept as references,
    /// applied to Player 1 as the item's cut-out parts and undone from a short history (<see cref="ItemApply"/>).
    /// </summary>
    public static class ItemEditEndpoints
    {
        public static IEndpointRouteBuilder MapItemEditEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/edits", List).RequireTeam(TeamPermission.ViewLibrary);
            app.MapGet("/api/edits/{id:int}", Get).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPost("/api/edits", Create).RequireTeam(TeamPermission.AddEntries);
            app.MapPut("/api/edits/{id:int}", Update).RequireTeam(TeamPermission.AddEntries);
            app.MapDelete("/api/edits/{id:int}", Delete).RequireTeam(TeamPermission.AddEntries | TeamPermission.DeleteAnyEntries);
            app.MapPost("/api/edits/{id:int}/copy", Copy).RequireTeam(TeamPermission.AddEntries);
            // Applying to the item needs the item's edit permission; without it the item is copied first (AddEntries). Checked in the handler.
            app.MapPost("/api/edits/{id:int}/apply", Apply)
                .RequireTeam(TeamPermission.AddEntries | TeamPermission.EditOwnEntries | TeamPermission.EditAnyEntries);
            app.MapPost("/api/entries/{id:int}/revert", (int id, RevertRequest? request, HttpContext context, CancellationToken token) =>
                    RevertAsync(new ItemRef(id, null, null), request, context, token))
                .RequireTeam(TeamPermission.EditOwnEntries | TeamPermission.EditAnyEntries);
            app.MapPost("/api/markers/{id:int}/revert", (int id, RevertRequest? request, HttpContext context, CancellationToken token) =>
                    RevertAsync(new ItemRef(null, id, null), request, context, token))
                .RequireTeam(TeamPermission.AddEntries | TeamPermission.EditAnyEntries);
            app.MapPost("/api/videos/{id:int}/revert", (int id, RevertRequest? request, HttpContext context, CancellationToken token) =>
                    RevertAsync(new ItemRef(null, null, id), request, context, token))
                .RequireTeam(TeamPermission.AddEntries | TeamPermission.EditAnyEntries);
            return app;
        }

        private sealed record ItemRef(int? EntryId, int? MarkerId, int? VideoId);

        /// <summary>The item an edit is of, loaded (a video with its segments and their markers).</summary>
        private sealed class Source
        {
            public required ItemEditKind Kind { get; init; }
            public LibraryEntry? Entry { get; init; }
            public VodMarker? Marker { get; init; }
            public Video? Video { get; init; }

            public long VodId => Entry?.VodId ?? Marker?.VodId ?? Video!.VodId;

            /// <summary>Clip/segment: the full range.</summary>
            public double Start => Entry is { } e ? e.StartTime.TotalSeconds : Marker?.Seconds ?? 0;
            public double End => Entry is { } e ? e.EndTime?.TotalSeconds ?? e.VodLengthSeconds : Marker?.EndSeconds ?? 0;

            /// <summary>Video: its segments in play order.</summary>
            public IReadOnlyList<VideoSegment> Links => Video is null
                ? []
                : Video.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position).ToList();

            public ItemRef Ref => new(Entry?.Id, Marker?.Id, Video?.Id);

            public string Describe => Kind switch
            {
                ItemEditKind.Clip => $"clip {Entry!.Id}",
                ItemEditKind.Segment => $"segment {Marker!.Id}",
                _ => $"video {Video!.Id}",
            };

            /// <summary>The caller may change the item itself (the same rule as its own edit endpoints).</summary>
            public bool CanChange(TeamContext team) => !team.IsSupport && Kind switch
            {
                ItemEditKind.Clip => Entry!.Status != EntryStatus.VodUnavailable
                    && team.CanOnOwnOrAny(Entry.AddedByUserId, TeamPermission.EditOwnEntries, TeamPermission.EditAnyEntries),
                ItemEditKind.Segment => TimelineEndpoints.CanEdit(team, Marker!),
                _ => VideoEndpoints.CanEdit(team, Video!),
            };
        }

        private static async Task<(Source? Source, IResult? Problem)> LoadAsync(LibraryDbContext db, ItemRef item, bool tracking,
            CancellationToken cancellationToken)
        {
            var count = (item.EntryId is null ? 0 : 1) + (item.MarkerId is null ? 0 : 1) + (item.VideoId is null ? 0 : 1);
            if (count != 1)
                return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["item"] = ["Give one of entryId, markerId or videoId."] }));

            if (item.EntryId is { } entryId)
            {
                var entries = tracking ? db.Entries : db.Entries.AsNoTracking();
                var entry = await entries.FirstOrDefaultAsync(x => x.Id == entryId, cancellationToken);
                if (entry is null)
                    return (null, Results.Problem(detail: "That clip is gone.", statusCode: StatusCodes.Status404NotFound));
                if (entry.Kind != EntryKind.Vod)
                    return (null, Results.Problem(detail: "Twitch clips have no edits.", statusCode: StatusCodes.Status409Conflict));
                return (new Source { Kind = ItemEditKind.Clip, Entry = entry }, null);
            }

            if (item.MarkerId is { } markerId)
            {
                var markers = tracking ? db.Markers : db.Markers.AsNoTracking();
                var marker = await markers.FirstOrDefaultAsync(x => x.Id == markerId, cancellationToken);
                if (marker is null)
                    return (null, Results.Problem(detail: "That segment is gone.", statusCode: StatusCodes.Status404NotFound));
                if (marker.EndSeconds is null)
                    return (null, Results.Problem(detail: "Moments have no edits.", statusCode: StatusCodes.Status409Conflict));
                return (new Source { Kind = ItemEditKind.Segment, Marker = marker }, null);
            }

            var videos = tracking ? VideoEndpoints.WithSegments(db) : VideoEndpoints.WithSegments(db).AsNoTracking();
            var video = await videos.FirstOrDefaultAsync(x => x.Id == item.VideoId, cancellationToken);
            if (video is null)
                return (null, Results.Problem(detail: "That video is gone.", statusCode: StatusCodes.Status404NotFound));
            return (new Source { Kind = ItemEditKind.Video, Video = video }, null);
        }

        private static ItemRef RefOf(ItemEdit edit) => new(edit.EntryId, edit.MarkerId, edit.VideoId);

        private static IQueryable<T> ForItem<T>(IQueryable<T> query, ItemRef item) where T : class => query switch
        {
            IQueryable<ItemEdit> edits => (IQueryable<T>)(item.EntryId is { } e ? edits.Where(x => x.EntryId == e)
                : item.MarkerId is { } m ? edits.Where(x => x.MarkerId == m) : edits.Where(x => x.VideoId == item.VideoId)),
            IQueryable<ItemApply> applies => (IQueryable<T>)(item.EntryId is { } e ? applies.Where(x => x.EntryId == e)
                : item.MarkerId is { } m ? applies.Where(x => x.MarkerId == m) : applies.Where(x => x.VideoId == item.VideoId)),
            _ => throw new ArgumentException("Not an item table.", nameof(query)),
        };

        // --- reading edits ---

        private static bool CanEdit(TeamContext team, ItemEdit edit) =>
            !team.IsSupport && edit.CreatedByUserId == team.UserId && team.Can(TeamPermission.AddEntries);

        private static bool CanDelete(TeamContext team, ItemEdit edit) =>
            !team.IsSupport && team.CanOnOwnOrAny(edit.CreatedByUserId.ToString(), TeamPermission.AddEntries, TeamPermission.DeleteAnyEntries);

        /// <summary>A video edit's cuts on one segment, against the segment's range.</summary>
        private static IReadOnlyList<CutRange> PartCuts(IReadOnlyList<ItemEditPart> parts, VideoSegment link) =>
            ItemEdits.ReadCuts(CutOuts.Write(parts.FirstOrDefault(x => x.MarkerId == link.MarkerId)?.Cuts ?? []), link.Marker!.Seconds, link.Marker.EndSeconds!.Value);

        /// <summary>
        /// What a video edit plays of one segment: its range without its own cut-out parts or the edit's (the video's own per-segment cuts are
        /// what applying the edit replaces, so they don't count).
        /// </summary>
        private static IReadOnlyList<(double Start, double End)> VideoEditKept(VideoSegment link, IReadOnlyList<CutRange> editCuts) =>
            CutOuts.Kept(link.Marker!.Seconds, link.Marker.EndSeconds!.Value, CutOuts.Read(link.Marker.Cuts).Concat(editCuts));

        /// <summary>The edit's kept parts in play order (VOD seconds); exports and previews play these.</summary>
        private static IReadOnlyList<(double Start, double End)> KeptParts(ItemEdit edit, Source source)
        {
            if (source.Kind != ItemEditKind.Video)
                return CutOuts.Kept(source.Start, source.End, ItemEdits.ReadCuts(edit.Cuts, source.Start, source.End));
            var parts = ItemEditPart.Read(edit.Parts);
            return source.Links.SelectMany(link => VideoEditKept(link, PartCuts(parts, link))).ToList();
        }

        private static JsonElement? ReadLayout(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static EditDto ToDto(ItemEdit edit, Source source, IReadOnlyDictionary<int, string> names, TeamContext team)
        {
            IReadOnlyList<CutRange> cuts = [];
            IReadOnlyList<ItemEditPart> parts = [];
            IReadOnlyList<EditSourcePartDto> sourceParts = [];
            if (source.Kind == ItemEditKind.Video)
            {
                var stored = ItemEditPart.Read(edit.Parts);
                parts = source.Links.Select(x => new ItemEditPart(x.MarkerId, PartCuts(stored, x))).ToList();
                sourceParts = source.Links.Select((x, i) => new EditSourcePartDto(x.MarkerId, x.Marker!.Label.Length > 0 ? x.Marker.Label : $"Segment {i + 1}",
                    x.Marker.Seconds, x.Marker.EndSeconds!.Value, CutOuts.Normalize(CutOuts.Read(x.Marker.Cuts), x.Marker.Seconds, x.Marker.EndSeconds.Value))).ToList();
            }
            else
                cuts = ItemEdits.ReadCuts(edit.Cuts, source.Start, source.End);

            return new EditDto(edit.Id, edit.Kind.ToString().ToLowerInvariant(), edit.EntryId, edit.MarkerId, edit.VideoId, edit.VodId, edit.Title,
                edit.CreatedByUserId, names.GetValueOrDefault(edit.CreatedByUserId), edit.CreatedAtUtc, edit.UpdatedAtUtc, cuts, parts,
                ReadLayout(edit.Layout), Math.Round(KeptParts(edit, source).Sum(x => x.End - x.Start), 2),
                source.Kind == ItemEditKind.Video ? null : source.Start, source.Kind == ItemEditKind.Video ? null : source.End, sourceParts,
                CanEdit(team, edit), CanDelete(team, edit));
        }

        private static async Task<EditDto> ToDtoAsync(LibraryDbContext db, ItemEdit edit, Source source, TeamContext team, CancellationToken cancellationToken)
        {
            var names = await TimelineEndpoints.UserNamesAsync(db, [edit.CreatedByUserId], cancellationToken);
            return ToDto(edit, source, names, team);
        }

        private static async Task<IResult> List(int? entryId, int? markerId, int? videoId, TeamContext team, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var item = new ItemRef(entryId, markerId, videoId);
            var (source, problem) = await LoadAsync(db, item, tracking: false, cancellationToken);
            if (problem is not null)
                return problem;

            var edits = await ForItem(db.ItemEdits.AsNoTracking(), item).OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id)
                .ToListAsync(cancellationToken);
            var applies = await ForItem(db.ItemApplies.AsNoTracking(), item).OrderByDescending(x => x.AppliedAtUtc).ThenByDescending(x => x.Id)
                .ToListAsync(cancellationToken);
            var names = await TimelineEndpoints.UserNamesAsync(db, edits.Select(x => x.CreatedByUserId).Concat(applies.Select(x => x.UserId)), cancellationToken);

            var canChange = source!.CanChange(team);
            return Results.Ok(new EditListDto(
                edits.Select(x => ToDto(x, source, names, team)).ToList(),
                applies.Select(x => new ItemApplyDto(x.Id, x.EditId, x.EditTitle, x.UserId, names.GetValueOrDefault(x.UserId), x.AppliedAtUtc)).ToList(),
                CanApply: !team.IsSupport && (canChange || team.Can(TeamPermission.AddEntries)),
                ApplyMakesCopy: !canChange,
                CanRevert: canChange && applies.Count > 0));
        }

        private static async Task<IResult> Get(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var edit = await db.ItemEdits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (edit is null)
                return EditNotFound();
            var (source, problem) = await LoadAsync(db, RefOf(edit), tracking: false, cancellationToken);
            return problem ?? Results.Ok(await ToDtoAsync(db, edit, source!, team, cancellationToken));
        }

        // --- for exports and vertical renders ---

        /// <summary>What an export of an edit is made of (copied when the export is asked for).</summary>
        /// <param name="Cuts">Clip/segment: what the edit removes, against the item's current range.</param>
        /// <param name="VideoParts">Video: its segments without their own cut-out parts or the edit's (segments cut out whole left out).</param>
        internal sealed record EditForExport(ItemEdit Edit, ItemEditKind Kind, LibraryEntry? Entry, VodMarker? Marker, Video? Video,
            IReadOnlyList<CutRange> Cuts, IReadOnlyList<ExportVideoPart> VideoParts);

        internal static async Task<EditForExport?> ForExportAsync(LibraryDbContext db, int editId, CancellationToken cancellationToken)
        {
            var edit = await db.ItemEdits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == editId, cancellationToken);
            if (edit is null)
                return null;
            var (source, problem) = await LoadAsync(db, RefOf(edit), tracking: false, cancellationToken);
            if (problem is not null)
                return null;

            if (source!.Kind != ItemEditKind.Video)
                return new EditForExport(edit, source.Kind, source.Entry, source.Marker, null, ItemEdits.ReadCuts(edit.Cuts, source.Start, source.End), []);

            var parts = ItemEditPart.Read(edit.Parts);
            var videoParts = source.Links.Select((link, i) =>
            {
                var (start, end) = (link.Marker!.Seconds, link.Marker.EndSeconds!.Value);
                return new ExportVideoPart(link.Marker.Label.Length > 0 ? link.Marker.Label : $"Segment {i + 1}", start, end,
                    CutOuts.Normalize(CutOuts.Read(link.Marker.Cuts).Concat(PartCuts(parts, link)), start, end));
            }).Where(x => x.KeptSeconds >= CutOuts.MinKeptPartSeconds).ToList();
            return new EditForExport(edit, ItemEditKind.Video, null, null, source.Video, [], videoParts);
        }

        /// <summary>
        /// What a clip edit keeps, in seconds into the clip's file (which holds the whole range, or for a file made the old way the range
        /// without its cut-out parts back to back). Null if the edit isn't one of this clip's.
        /// </summary>
        internal static async Task<IReadOnlyList<(double Start, double End)>?> ClipEditFilePartsAsync(LibraryDbContext db, int editId, LibraryEntry entry,
            CancellationToken cancellationToken)
        {
            var edit = await db.ItemEdits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == editId && x.EntryId == entry.Id, cancellationToken);
            if (edit is null || entry.Kind != EntryKind.Vod)
                return null;
            var (start, end) = (entry.StartTime.TotalSeconds, entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds);
            var filePieces = EntryDto.HasFileCuts(entry) ? CutOuts.Kept(start, end, CutOuts.Read(entry.FileCuts)) : [(start, end)];
            return VodToFile(CutOuts.Kept(start, end, ItemEdits.ReadCuts(edit.Cuts, start, end)), filePieces);
        }

        /// <summary>Ranges of the VOD mapped into a file that plays <paramref name="filePieces"/> back to back (what's not in the file is dropped).</summary>
        public static IReadOnlyList<(double Start, double End)> VodToFile(IReadOnlyList<(double Start, double End)> ranges,
            IReadOnlyList<(double Start, double End)> filePieces)
        {
            var result = new List<(double, double)>();
            foreach (var (start, end) in ranges)
            {
                var offset = 0.0;
                foreach (var (pieceStart, pieceEnd) in filePieces)
                {
                    var from = Math.Max(start, pieceStart);
                    var to = Math.Min(end, pieceEnd);
                    if (to > from)
                        result.Add((offset + from - pieceStart, offset + to - pieceStart));
                    offset += pieceEnd - pieceStart;
                }
            }
            return result;
        }

        // --- writing edits ---

        /// <summary>Checks and stores the request's title, cuts/parts and layout on the edit (null fields are kept).</summary>
        private static IResult? Fill(ItemEdit edit, Source source, EditRequest request)
        {
            if (request.Title is { } rawTitle)
            {
                var title = rawTitle.Trim();
                if (title.Length > ItemEdit.MaxTitleLength)
                    return Invalid("title", $"Keep the title to {ItemEdit.MaxTitleLength} characters.");
                edit.Title = title;
            }

            if (source.Kind == ItemEditKind.Video)
            {
                if (request.Cuts is { Count: > 0 })
                    return Invalid("cuts", "A video's edit cuts its segments: use parts.");
                if (request.Parts is { } parts)
                {
                    var links = source.Links;
                    if (parts.Select(x => x.MarkerId).Distinct().Count() != parts.Count)
                        return Invalid("parts", "List each segment once.");
                    var stored = new List<ItemEditPart>();
                    var kept = 0.0;
                    foreach (var link in links)
                    {
                        var part = parts.FirstOrDefault(x => x.MarkerId == link.MarkerId);
                        var (start, end) = (link.Marker!.Seconds, link.Marker.EndSeconds!.Value);
                        if (ItemEdits.Validate(part?.Cuts, start, end, mayLeaveNothing: true) is { } error)
                            return Invalid("parts", error);
                        var cuts = ItemEdits.ForStoring(part?.Cuts, start, end);
                        if (cuts.Count > 0)
                            stored.Add(new ItemEditPart(link.MarkerId, cuts));
                        kept += VideoEditKept(link, CutOuts.Normalize(part?.Cuts, start, end)).Sum(x => x.End - x.Start);
                    }
                    if (parts.Any(p => links.All(l => l.MarkerId != p.MarkerId)))
                        return Invalid("parts", "Cut parts out of segments in this video.");
                    if (kept < CutOuts.MinKeptPartSeconds)
                        return Invalid("parts", "The edit leaves nothing.");
                    edit.Parts = ItemEditPart.Write(stored);
                }
            }
            else
            {
                if (request.Parts is { Count: > 0 })
                    return Invalid("parts", "Only a video's edit has parts: use cuts.");
                if (request.Cuts is { } cuts)
                {
                    if (ItemEdits.Validate(cuts, source.Start, source.End, mayLeaveNothing: false) is { } error)
                        return Invalid("cuts", error);
                    edit.Cuts = CutOuts.Write(ItemEdits.ForStoring(cuts, source.Start, source.End));
                }
            }

            if (request.Layout is { } layout)
            {
                if (layout.ValueKind != JsonValueKind.Object)
                    return Invalid("layout", "The layout is a JSON object.");
                var raw = layout.GetRawText();
                if (raw.Length > ItemEdit.MaxLayoutLength)
                    return Invalid("layout", "The layout is too big.");
                edit.Layout = layout.EnumerateObject().Any() ? raw : null;
            }

            return null;
        }

        private static async Task<IResult> Create(EditRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            CancellationToken cancellationToken)
        {
            var (source, problem) = await LoadAsync(db, new ItemRef(request.EntryId, request.MarkerId, request.VideoId), tracking: false, cancellationToken);
            if (problem is not null)
                return problem;
            if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, source!.VodId, cancellationToken), source.VodId) is { } refused)
                return refused;

            var mine = await ForItem(db.ItemEdits, source.Ref).CountAsync(x => x.CreatedByUserId == team.UserId, cancellationToken);
            if (mine >= ItemEdit.MaxEditsPerItemPerMember)
                return Results.Problem(detail: $"You can keep at most {ItemEdit.MaxEditsPerItemPerMember} edits of one item. Delete some first.",
                    statusCode: StatusCodes.Status409Conflict);

            var now = DateTime.UtcNow;
            var edit = new ItemEdit
            {
                VodId = source.VodId, EntryId = request.EntryId, MarkerId = request.MarkerId, VideoId = request.VideoId,
                CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            if (Fill(edit, source, request) is { } invalid)
                return invalid;

            db.ItemEdits.Add(edit);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.EditAdded, edit.EntryId, $"VOD {edit.VodId} {source.Describe}: edit {edit.Id}{TitleText(edit)}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/edits/{edit.Id}", await ToDtoAsync(db, edit, source, team, cancellationToken));
        }

        /// <summary>Saves the caller's own edit (the page saves shortly after each change, so this isn't audited).</summary>
        private static async Task<IResult> Update(int id, EditRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var edit = await db.ItemEdits.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (edit is null)
                return EditNotFound();
            if (!CanEdit(team, edit))
                return Results.Problem(detail: "Only its maker changes an edit. Make a copy instead.", statusCode: StatusCodes.Status403Forbidden);
            if (request.EntryId is not null || request.MarkerId is not null || request.VideoId is not null)
            {
                if (request.EntryId != edit.EntryId || request.MarkerId != edit.MarkerId || request.VideoId != edit.VideoId)
                    return Invalid("item", "An edit stays with its item.");
            }

            var (source, problem) = await LoadAsync(db, RefOf(edit), tracking: false, cancellationToken);
            if (problem is not null)
                return problem;
            if (Fill(edit, source!, request) is { } invalid)
                return invalid;

            edit.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await ToDtoAsync(db, edit, source!, team, cancellationToken));
        }

        private static async Task<IResult> Delete(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var edit = await db.ItemEdits.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (edit is null)
                return EditNotFound();
            if (!CanDelete(team, edit))
                return Results.Problem(detail: "Your role only lets you delete your own edits.", statusCode: StatusCodes.Status403Forbidden);

            db.ItemEdits.Remove(edit);
            db.Audit(AuditActions.EditDeleted, edit.EntryId, $"VOD {edit.VodId} {edit.Kind.ToString().ToLowerInvariant()} edit {edit.Id}{TitleText(edit)}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        private static async Task<IResult> Copy(int id, EditCopyRequest? request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            CancellationToken cancellationToken)
        {
            var original = await db.ItemEdits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (original is null)
                return EditNotFound();
            var (source, problem) = await LoadAsync(db, RefOf(original), tracking: false, cancellationToken);
            if (problem is not null)
                return problem;
            if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, source!.VodId, cancellationToken), source.VodId) is { } refused)
                return refused;

            var title = request?.Title?.Trim();
            if (string.IsNullOrEmpty(title))
                title = original.Title.Length > 0 ? $"Copy of {original.Title}" : "";
            if (title.Length > ItemEdit.MaxTitleLength)
                title = title[..ItemEdit.MaxTitleLength];

            var copy = await AddCopyAsync(db, original, source.Ref, title, null, cancellationToken);
            return Results.Created($"/api/edits/{copy.Id}", await ToDtoAsync(db, copy, source, team, cancellationToken));
        }

        /// <summary>Saves a copy of an edit for the caller, of <paramref name="item"/> (the same item, or a copy of it with <paramref name="markerMap"/>).</summary>
        private static async Task<ItemEdit> AddCopyAsync(LibraryDbContext db, ItemEdit original, ItemRef item, string title,
            IReadOnlyDictionary<int, int>? markerMap, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var parts = ItemEditPart.Read(original.Parts);
            var copy = new ItemEdit
            {
                VodId = original.VodId, EntryId = item.EntryId, MarkerId = item.MarkerId, VideoId = item.VideoId, Title = title,
                Cuts = original.Cuts, Layout = original.Layout, CreatedAtUtc = now, UpdatedAtUtc = now,
                Parts = markerMap is null
                    ? original.Parts
                    : ItemEditPart.Write(parts.Where(x => markerMap.ContainsKey(x.MarkerId)).Select(x => x with { MarkerId = markerMap[x.MarkerId] }).ToList()),
            };
            db.ItemEdits.Add(copy);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.EditAdded, copy.EntryId, $"VOD {copy.VodId} {copy.Kind.ToString().ToLowerInvariant()} edit {copy.Id}{TitleText(copy)} (copy of edit {original.Id})");
            await db.SaveChangesAsync(cancellationToken);
            return copy;
        }

        // --- apply and revert ---

        /// <summary>
        /// Sets the item's cut-out parts to what the edit removes, keeping the item's range, and records it so it can be undone. On someone
        /// else's item without the permission to change it, the item (and the edit) are copied onto the caller's row first.
        /// </summary>
        private static async Task<IResult> Apply(int id, EditApplyRequest? request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            LibraryStorage storage, DownloadQueue queue, LiveMarkerEntryService liveEntries, IOptions<DownloadSettings> settings, ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
        {
            var edit = await db.ItemEdits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (edit is null)
                return EditNotFound();
            var (source, problem) = await LoadAsync(db, RefOf(edit), tracking: true, cancellationToken);
            if (problem is not null)
                return problem;
            if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, source!.VodId, cancellationToken), source.VodId) is { } refused)
                return refused;

            var copied = false;
            if (!source.CanChange(team))
            {
                if (team.IsSupport || !team.Can(TeamPermission.AddEntries))
                    return Results.Problem(detail: "Your role doesn't let you change this item or make a copy of it.", statusCode: StatusCodes.Status403Forbidden);

                (source, edit, problem) = await CopyItemAsync(source, edit, request?.RowId, team, db, access, storage, queue, liveEntries, loggerFactory,
                    cancellationToken);
                if (problem is not null)
                    return problem;
                copied = true;
            }
            else if (request?.RowId is not null)
                return Invalid("rowId", "The edit is applied to the item itself, so there is no copy to place.");

            var title = edit!.Title;
            switch (source!.Kind)
            {
                case ItemEditKind.Clip:
                {
                    var entry = source.Entry!;
                    if (entry.Status == EntryStatus.VodUnavailable)
                        return Results.Problem(detail: "The VOD is no longer on Twitch, so the clip can't change.", statusCode: StatusCodes.Status410Gone);
                    var cuts = ItemEdits.ReadCuts(edit.Cuts, source.Start, source.End);
                    if (CutOuts.KeptSeconds(source.Start, source.End, cuts) < CutOuts.MinKeptPartSeconds)
                        return NothingLeft();
                    AddApply(db, source, edit, entry.Cuts, CutOuts.Write(cuts));
                    if (await EntryEndpoints.RecutAsync(entry, entry.StartTime, entry.EndTime, cuts, entry.FollowsMarkers, db, queue, storage, liveEntries,
                            settings.Value, (AuditActions.EditApplied, $"Edit {edit.Id}{TitleText(edit)} applied"), cancellationToken) is { } busy)
                        return busy;
                    break;
                }
                case ItemEditKind.Segment:
                {
                    var marker = source.Marker!;
                    var cuts = ItemEdits.ReadCuts(edit.Cuts, source.Start, source.End);
                    if (CutOuts.KeptSeconds(source.Start, source.End, cuts) < CutOuts.MinKeptPartSeconds)
                        return NothingLeft();
                    var json = CutOuts.Write(cuts);
                    AddApply(db, source, edit, marker.Cuts, json);
                    marker.Cuts = json;
                    db.Audit(AuditActions.EditApplied, null, $"VOD {marker.VodId} segment {marker.Id}: edit {edit.Id}{TitleText(edit)} applied, {cuts.Count} cut-out part(s)");
                    break;
                }
                default:
                {
                    var parts = ItemEditPart.Read(edit.Parts);
                    var links = source.Links;
                    if (links.Sum(x => VideoEditKept(x, PartCuts(parts, x)).Sum(p => p.End - p.Start)) < CutOuts.MinKeptPartSeconds)
                        return NothingLeft();
                    var before = VideoCutsJson(links);
                    foreach (var link in links)
                        link.Cuts = CutOuts.Write(PartCuts(parts, link));
                    AddApply(db, source, edit, before, VideoCutsJson(links));
                    source.Video!.UpdatedAtUtc = DateTime.UtcNow;
                    db.Audit(AuditActions.EditApplied, null, $"VOD {source.VodId} video {source.Video.Id}: edit {edit.Id}{TitleText(edit)} applied");
                    break;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await PruneAsync(db, source.Ref, cancellationToken);

            var editDto = await ToDtoAsync(db, edit, (await LoadAsync(db, source.Ref, tracking: false, cancellationToken)).Source!, team, cancellationToken);
            return Results.Ok(new EditApplyResultDto(source.Kind.ToString().ToLowerInvariant(), await ItemDtoAsync(db, source.Ref, team, cancellationToken),
                copied, editDto));
        }

        /// <summary>Copies the item onto the caller's row (like its copy endpoint) and the edit onto the copy; returns both, loaded for tracking.</summary>
        private static async Task<(Source? Source, ItemEdit? Edit, IResult? Problem)> CopyItemAsync(Source source, ItemEdit edit, int? rowId, TeamContext team,
            LibraryDbContext db, TeamChannelAccess access, LibraryStorage storage, DownloadQueue queue, LiveMarkerEntryService liveEntries,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            ItemRef copyRef;
            IReadOnlyDictionary<int, int>? markerMap = null;
            switch (source.Kind)
            {
                case ItemEditKind.Clip:
                {
                    var (copy, problem) = await CopyEndpoints.CopyEntryAsync(source.Entry!, rowId, team, db, access, storage, queue, loggerFactory, cancellationToken);
                    if (problem is not null)
                        return (null, null, problem);
                    copyRef = new ItemRef(copy!.Id, null, null);
                    break;
                }
                case ItemEditKind.Segment:
                {
                    var (copy, problem) = await CopyEndpoints.CopyMarkerAsync(source.Marker!, rowId, team, db, access, cancellationToken);
                    if (problem is not null)
                        return (null, null, problem);
                    await liveEntries.MarkersChangedAsync(db, copy!.VodId, cancellationToken);
                    copyRef = new ItemRef(null, copy.Id, null);
                    break;
                }
                default:
                {
                    var (copy, problem) = await VideoEndpoints.CopyVideoAsync(source.Video!, rowId, team, db, access, cancellationToken);
                    if (problem is not null)
                        return (null, null, problem);
                    await liveEntries.MarkersChangedAsync(db, copy!.VodId, cancellationToken);
                    var sourceLinks = source.Links;
                    var copyLinks = copy.Segments.OrderBy(x => x.Position).ToList();
                    markerMap = sourceLinks.Zip(copyLinks).ToDictionary(x => x.First.MarkerId, x => x.Second.MarkerId);
                    copyRef = new ItemRef(null, null, copy.Id);
                    break;
                }
            }

            var editCopy = await AddCopyAsync(db, edit, copyRef, edit.Title, markerMap, cancellationToken);
            var (loaded, loadProblem) = await LoadAsync(db, copyRef, tracking: true, cancellationToken);
            return (loaded, editCopy, loadProblem);
        }

        private static string? VideoCutsJson(IReadOnlyList<VideoSegment> links) =>
            ItemEditPart.Write(links.Select(x => new ItemEditPart(x.MarkerId, CutOuts.Read(x.Cuts))).ToList());

        private static void AddApply(LibraryDbContext db, Source source, ItemEdit edit, string? before, string? after) =>
            db.ItemApplies.Add(new ItemApply
            {
                VodId = source.VodId, EntryId = source.Entry?.Id, MarkerId = source.Marker?.Id, VideoId = source.Video?.Id,
                EditId = edit.Id, EditTitle = edit.Title, AppliedAtUtc = DateTime.UtcNow, CutsBefore = before, CutsAfter = after,
            });

        /// <summary>Keeps the oldest apply (the way back to the original) and the newest ones, <see cref="ItemApply.MaxPerItem"/> in all.</summary>
        private static async Task PruneAsync(LibraryDbContext db, ItemRef item, CancellationToken cancellationToken)
        {
            var rows = await ForItem(db.ItemApplies, item).OrderBy(x => x.AppliedAtUtc).ThenBy(x => x.Id).ToListAsync(cancellationToken);
            if (rows.Count <= ItemApply.MaxPerItem)
                return;
            db.ItemApplies.RemoveRange(rows.Skip(1).Take(rows.Count - ItemApply.MaxPerItem));
            await db.SaveChangesAsync(cancellationToken);
        }

        /// <summary>Undoes the last apply ("previous") or every apply ("original") of an item: its cut-out parts go back to what they were.</summary>
        private static async Task<IResult> RevertAsync(ItemRef item, RevertRequest? request, HttpContext context, CancellationToken cancellationToken)
        {
            var services = context.RequestServices;
            var team = TeamContext.Of(context)!;
            var db = services.GetRequiredService<LibraryDbContext>();

            var toOriginal = request?.To?.Trim().ToLowerInvariant() switch
            {
                null or "" or "previous" => false,
                "original" => true,
                _ => (bool?)null,
            };
            if (toOriginal is null)
                return Invalid("to", "Use \"previous\" or \"original\".");

            var (source, problem) = await LoadAsync(db, item, tracking: true, cancellationToken);
            if (problem is not null)
                return problem;
            if (!source!.CanChange(team))
                return Results.Problem(detail: "Your role doesn't let you change this item.", statusCode: StatusCodes.Status403Forbidden);

            var applies = await ForItem(db.ItemApplies, item).OrderBy(x => x.AppliedAtUtc).ThenBy(x => x.Id).ToListAsync(cancellationToken);
            if (applies.Count == 0)
                return Results.Problem(detail: "No edit has been applied to this item.", statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["code"] = "nothing-to-undo" });

            var target = toOriginal.Value ? applies[0] : applies[^1];
            var what = toOriginal.Value ? "reverted to the original" : $"undid applying edit {target.EditId}{(target.EditTitle.Length > 0 ? $" \"{target.EditTitle}\"" : "")}";
            switch (source.Kind)
            {
                case ItemEditKind.Clip:
                {
                    var entry = source.Entry!;
                    if (entry.Status == EntryStatus.VodUnavailable)
                        return Results.Problem(detail: "The VOD is no longer on Twitch, so the clip can't change.", statusCode: StatusCodes.Status410Gone);
                    var cuts = CutOuts.Normalize(CutOuts.Read(target.CutsBefore), source.Start, source.End);
                    if (CutOuts.KeptSeconds(source.Start, source.End, cuts) < CutOuts.MinKeptPartSeconds)
                        return NothingLeft();
                    db.ItemApplies.RemoveRange(toOriginal.Value ? applies : [target]);
                    var queue = services.GetRequiredService<DownloadQueue>();
                    var storage = services.GetRequiredService<LibraryStorage>();
                    var liveEntries = services.GetRequiredService<LiveMarkerEntryService>();
                    var settings = services.GetRequiredService<IOptions<DownloadSettings>>().Value;
                    if (await EntryEndpoints.RecutAsync(entry, entry.StartTime, entry.EndTime, cuts, entry.FollowsMarkers, db, queue, storage, liveEntries,
                            settings, (AuditActions.EditReverted, $"Edits: {what}"), cancellationToken) is { } busy)
                        return busy;
                    break;
                }
                case ItemEditKind.Segment:
                {
                    var marker = source.Marker!;
                    var cuts = CutOuts.Normalize(CutOuts.Read(target.CutsBefore), source.Start, source.End);
                    if (CutOuts.KeptSeconds(source.Start, source.End, cuts) < CutOuts.MinKeptPartSeconds)
                        return NothingLeft();
                    db.ItemApplies.RemoveRange(toOriginal.Value ? applies : [target]);
                    marker.Cuts = CutOuts.Write(cuts);
                    db.Audit(AuditActions.EditReverted, null, $"VOD {marker.VodId} segment {marker.Id}: {what}");
                    break;
                }
                default:
                {
                    // Segments added to the video after that apply weren't touched by it, so they keep theirs
                    var before = ItemEditPart.Read(target.CutsBefore);
                    foreach (var link in source.Links)
                    {
                        if (before.FirstOrDefault(x => x.MarkerId == link.MarkerId) is { } part)
                            link.Cuts = CutOuts.Write(CutOuts.Normalize(part.Cuts, link.Marker!.Seconds, link.Marker.EndSeconds!.Value));
                    }
                    db.ItemApplies.RemoveRange(toOriginal.Value ? applies : [target]);
                    source.Video!.UpdatedAtUtc = DateTime.UtcNow;
                    db.Audit(AuditActions.EditReverted, null, $"VOD {source.VodId} video {source.Video.Id}: {what}");
                    break;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await ItemDtoAsync(db, item, team, cancellationToken));
        }

        /// <summary>The item as its own endpoints return it.</summary>
        private static async Task<object> ItemDtoAsync(LibraryDbContext db, ItemRef item, TeamContext team, CancellationToken cancellationToken)
        {
            if (item.EntryId is { } entryId)
            {
                var entry = await db.Entries.AsNoTracking().FirstAsync(x => x.Id == entryId, cancellationToken);
                var names = await EntryEndpoints.UsernamesAsync(db, [entry.AddedByUserId], cancellationToken);
                return EntryDto.From(entry) with { AddedBy = entry.AddedByUserId is { } u ? names.GetValueOrDefault(u) : null };
            }
            if (item.MarkerId is { } markerId)
            {
                var marker = await db.Markers.AsNoTracking().FirstAsync(x => x.Id == markerId, cancellationToken);
                return TimelineEndpoints.ToDto(marker, await TimelineEndpoints.UserNamesAsync(db, [marker.CreatedByUserId], cancellationToken), team);
            }
            var video = await VideoEndpoints.WithSegments(db).AsNoTracking().FirstAsync(x => x.Id == item.VideoId, cancellationToken);
            return (await VideoEndpoints.ToDtosAsync(db, [video], team, cancellationToken))[0];
        }

        private static string TitleText(ItemEdit edit) => edit.Title.Length > 0 ? $" \"{edit.Title}\"" : "";

        private static IResult Invalid(string field, string message) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

        private static IResult NothingLeft() =>
            Results.Problem(detail: "That would leave nothing of the item as it is now.", statusCode: StatusCodes.Status409Conflict);

        private static IResult EditNotFound() => Results.Problem(detail: "That edit is gone.", statusCode: StatusCodes.Status404NotFound);
    }
}
