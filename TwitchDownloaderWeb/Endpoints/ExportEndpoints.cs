using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models.Interfaces;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services.Projects;
using TwitchDownloaderWeb.Services.Segments;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record ExportRequest(List<ExportItemRequest>? Items);

    /// <param name="Kind">"vod" (a range of a VOD: <paramref name="VodId"/>, <paramref name="Start"/>, <paramref name="End"/>; a one-off, never saved),
    /// "clip" (<paramref name="EntryId"/>; <paramref name="Horizontal"/> and/or <paramref name="Vertical"/>), "video" (<paramref name="VideoId"/>;
    /// <paramref name="Joined"/> = one file, else a .zip of its segments), "segment" (<paramref name="MarkerId"/>) or "edit" (<paramref name="EditId"/>:
    /// a saved edit of a clip, segment or video, made like that item but without what the edit removes; a clip edit takes horizontal/vertical,
    /// its vertical a render made for that edit; a video edit takes joined) or "project" (<paramref name="ProjectId"/>: the whole timeline rendered
    /// as one file: landscape for a horizontal edit, 9:16 with each piece's boxes for a vertical one; Horizontal/Vertical are ignored).</param>
    /// <param name="FileName">The download's name (no extension); blank = the item's title.</param>
    /// <param name="Quality">Blank = best (a clip: its own quality, so a stored clip is just copied). Ignored for vertical.</param>
    /// <param name="RenderId">Vertical: one of the clip's older renders (someone else's needs DownloadTeamVerticals). With neither this nor
    /// <paramref name="ProjectId"/>, a clip's vertical is the caller's newest vertical edit of it, else their own newest render.</param>
    public sealed record ExportItemRequest(string? Kind, string? FileName = null, string? Quality = null, long? VodId = null, double? Start = null,
        double? End = null, int? EntryId = null, bool Horizontal = true, bool Vertical = false, string? RenderId = null, int? VideoId = null,
        bool Joined = false, int? MarkerId = null, int? EditId = null, int? ProjectId = null, bool Separate = false);

    /// <param name="Kind">VodRange, Clip, ClipVertical, Video or Segment.</param>
    /// <param name="LengthSeconds">What it plays (cut-out parts left out).</param>
    /// <param name="PartsDone">Progress: files made so far of <paramref name="PartsTotal"/> (a video's segments, plus joining).</param>
    /// <param name="FileUrl">Its file once ready (a .zip for a video of separate files).</param>
    public sealed record ExportItemDto(int Position, string Kind, string FileName, string? Quality, long VodId, double StartSeconds, double EndSeconds,
        IReadOnlyList<CutRange> Cuts, int? EntryId, int? MarkerId, int? VideoId, string? RenderId, bool Joined, double LengthSeconds, string Status,
        string? Error, int PartsDone, int PartsTotal, long? FileSizeBytes, double? AudioRestoredSeconds, double? AudioMutedSeconds, string? FileUrl)
    {
        /// <summary>The saved edit it's made from (its kind is then the edit's item's: Clip, ClipVertical, Segment or Video).</summary>
        public int? EditId { get; init; }

        /// <summary>The project it's made from (kind Project or ProjectVertical).</summary>
        public int? ProjectId { get; init; }

        /// <summary>A project as separate files, one per piece it plays (served as a .zip).</summary>
        public bool Separate { get; init; }

        /// <summary>While it's being made: the step it's on, with that step's percent and time left when known.</summary>
        public JobProgressSnapshot? Progress { get; init; }
    }

    public sealed record ExportBatchDto(int Id, string Status, string? Error, int CreatedByUserId, string? CreatedBy, DateTime CreatedAtUtc,
        DateTime? ExpiresAtUtc, int ItemsDone, int ItemsTotal, bool CanDelete, IReadOnlyList<ExportItemDto> Items);

    /// <summary>
    /// Exports (badrfly, 2026-11-15, frontend rework): the Export tab's list in one request. Each item has its own quality and file name; the
    /// export runs in the background (<see cref="ExportWorker"/>) and the page polls it for progress and downloads each file when it's ready.
    /// A video joined into one file needs the team's "render as one" setting (<see cref="Team.CombineSegmentDownloads"/>, like segment downloads).
    /// </summary>
    public static class ExportEndpoints
    {
        public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/exports");
            group.MapPost("/", Create).RequireTeam(TeamPermission.GenerateFiles);
            group.MapGet("/", List).RequireTeam(TeamPermission.ViewLibrary);
            group.MapGet("/{id:int}", Get).RequireTeam(TeamPermission.ViewLibrary);
            group.MapGet("/{id:int}/items/{position:int}/file", Download).RequireTeam(TeamPermission.ViewLibrary);
            // Own with GenerateFiles, anyone's with DeleteAnyEntries; checked in the handler
            group.MapDelete("/{id:int}", Delete).RequireTeam(TeamPermission.GenerateFiles | TeamPermission.DeleteAnyEntries);
            return app;
        }

        private static bool CanDelete(TeamContext team, ExportBatch batch) =>
            !team.IsSupport && team.CanOnOwnOrAny(batch.CreatedByUserId.ToString(), TeamPermission.GenerateFiles, TeamPermission.DeleteAnyEntries);

        private static IResult Invalid(int index, string message) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [$"items[{index}]"] = [message] });

        private static IResult Coded(int status, string code, string detail) =>
            Results.Json(new { status, code, detail }, statusCode: status, contentType: "application/problem+json");

        /// <summary>The render a vertical export uses (B7): the one asked for, if it's the caller's or they may use the team's; else their own newest.</summary>
        internal static ShortformRender? PickRender(IReadOnlyList<ShortformRender> renders, string? renderId, TeamContext team, out string? error)
        {
            error = null;
            var anyone = VerticalAccess.CanDownload(team, null);
            if (!string.IsNullOrWhiteSpace(renderId))
            {
                var render = renders.FirstOrDefault(x => x.Id == renderId.Trim());
                if (render is null)
                    error = "That vertical render doesn't exist or has expired.";
                else if (render.CreatedByUserId != team.UserId && !anyone)
                    error = "Your role only lets you export your own vertical renders.";
                return error is null ? render : null;
            }

            var own = renders.Where(x => x.CreatedByUserId == team.UserId).MaxBy(x => x.CreatedAtUtc);
            if (own is null)
                error = anyone && renders.Count > 0 ? "Pick one of the team's vertical renders of this clip." : "Make a vertical version of this clip first.";
            return own;
        }

        private sealed class VodInfoCache(IVodDetailsSource vods, IVodQualitySource qualities, TeamChannelAccess access, TeamContext team)
        {
            private readonly Dictionary<long, VodDetails?> _details = [];
            private readonly Dictionary<long, IVideoQualities<M3U8.Stream>?> _qualities = [];
            private readonly Dictionary<long, IResult?> _refusals = [];

            public async Task<IResult?> RefusalAsync(long vodId, CancellationToken cancellationToken)
            {
                if (!_refusals.TryGetValue(vodId, out var refusal))
                    _refusals[vodId] = refusal = TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vodId, cancellationToken), vodId);
                return refusal;
            }

            public async Task<VodDetails?> DetailsAsync(long vodId)
            {
                if (!_details.TryGetValue(vodId, out var details))
                    _details[vodId] = details = await vods.GetVodAsync(vodId);
                return details;
            }

            public async Task<IVideoQualities<M3U8.Stream>?> QualitiesAsync(long vodId)
            {
                if (!_qualities.TryGetValue(vodId, out var list))
                    _qualities[vodId] = list = await qualities.GetQualitiesAsync(vodId);
                return list;
            }
        }

        private static async Task<IResult> Create(ExportRequest request, TeamContext team, LibraryDbContext db, IVodDetailsSource vods,
            IVodQualitySource qualitySource, TeamChannelAccess access, ShortformService shortform, ExportQueue queue, IOptions<DownloadSettings> options,
            CancellationToken cancellationToken)
        {
            var settings = options.Value;
            var requests = request.Items ?? [];
            if (requests.Count == 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Put something on the export list."] });

            var cache = new VodInfoCache(vods, qualitySource, access, team);
            var items = new List<ExportItem>();
            for (var i = 0; i < requests.Count; i++)
            {
                var (made, problem) = await BuildAsync(i, requests[i], team, db, cache, shortform, cancellationToken);
                if (problem is not null)
                    return problem;
                items.AddRange(made);
            }

            if (items.Count > settings.MaxExportItems)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [$"One export can have at most {settings.MaxExportItems} files."] });

            var totalSeconds = items.Where(x => x.Kind != ExportItemKind.ClipVertical).Sum(LengthOf);
            if (totalSeconds > settings.MaxExportSeconds)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["items"] = [$"These add up to {TrimTime.Format(TimeSpan.FromSeconds(totalSeconds))}; one export can be at most "
                                 + $"{TrimTime.Format(TimeSpan.FromSeconds(settings.MaxExportSeconds))}."],
                });

            if (items.Any(x => x.Kind == ExportItemKind.Video && x.Joined)
                && !await db.Teams.AsNoTracking().AnyAsync(x => x.Id == team.TeamId && x.CombineSegmentDownloads, cancellationToken))
                return Coded(StatusCodes.Status403Forbidden, "combine-not-enabled", "Rendering a video as one file isn't turned on for this team.");

            var active = await db.ExportBatches.CountAsync(x => x.Status == ExportStatus.Queued || x.Status == ExportStatus.Running, cancellationToken);
            if (active >= settings.MaxActiveExportsPerTeam)
                return Results.Problem(detail: $"Your team already has {active} exports in progress. Wait for one to finish.",
                    statusCode: StatusCodes.Status409Conflict);

            var now = DateTime.UtcNow;
            for (var i = 0; i < items.Count; i++)
                items[i].Position = i + 1;
            var batch = new ExportBatch { Status = ExportStatus.Queued, CreatedAtUtc = now, UpdatedAtUtc = now, Items = items };
            db.ExportBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.ExportRequested, items.Select(x => x.EntryId).FirstOrDefault(x => x is not null),
                $"Export {batch.Id}: {items.Count} file(s) ({string.Join(", ", items.GroupBy(x => x.Kind).Select(g => $"{g.Count()} {g.Key}"))}), "
                + TrimTime.Format(TimeSpan.FromSeconds(totalSeconds)));
            await db.SaveChangesAsync(cancellationToken);

            queue.Enqueue(batch.Id);
            return Results.Accepted($"/api/exports/{batch.Id}", await ToDtoAsync(db, team, batch, queue, cancellationToken));
        }

        private static double LengthOf(ExportItem item) => item.Kind switch
        {
            ExportItemKind.Video => ExportVideoPart.Read(item.Parts).Sum(x => x.KeptSeconds),
            ExportItemKind.Project or ExportItemKind.ProjectVertical => ProjectTimeline.Flatten(ProjectContent.Read(item.Parts)).Sum(x => x.Duration),
            _ => CutOuts.KeptSeconds(item.StartSeconds, item.EndSeconds, CutOuts.Read(item.Cuts)),
        };

        /// <summary>Checks one list item and copies what it's made from. A clip in both formats gives two items.</summary>
        private static async Task<(List<ExportItem> Items, IResult? Problem)> BuildAsync(int index, ExportItemRequest request, TeamContext team,
            LibraryDbContext db, VodInfoCache cache, ShortformService shortform, CancellationToken cancellationToken)
        {
            var fileName = request.FileName?.Trim();
            if (fileName is { Length: > ExportItem.MaxFileNameLength })
                return ([], Invalid(index, $"Keep the file name to {ExportItem.MaxFileNameLength} characters."));

            switch (request.Kind?.Trim().ToLowerInvariant())
            {
                case "vod":
                {
                    if (request.VodId is not { } vodId || request.Start is not { } start || request.End is not { } end
                        || !double.IsFinite(start) || !double.IsFinite(end) || start < 0)
                        return ([], Invalid(index, "A VOD range needs the VOD, a start and an end."));
                    if (end < start + TimelineEndpoints.MinSegmentSeconds)
                        return ([], Invalid(index, $"The end must be at least {TimelineEndpoints.MinSegmentSeconds} s after the start."));
                    var (vod, problem) = await VodForCutAsync(index, vodId, end, request.Quality, cache, cancellationToken);
                    if (problem is not null)
                        return ([], problem);
                    return ([new ExportItem
                    {
                        Kind = ExportItemKind.VodRange, VodId = vodId, StartSeconds = Math.Round(start, 2), EndSeconds = Math.Round(end, 2),
                        Quality = vod.Quality, FileName = string.IsNullOrEmpty(fileName) ? vod.Details.Title : fileName,
                    }], null);
                }

                case "segment":
                {
                    var marker = request.MarkerId is { } markerId ? await db.Markers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == markerId, cancellationToken) : null;
                    if (marker is null)
                        return ([], Invalid(index, "That segment is gone."));
                    if (marker.EndSeconds is not { } end)
                        return ([], Invalid(index, "Only segments can be exported, not moments."));
                    var (vod, problem) = await VodForCutAsync(index, marker.VodId, end, request.Quality, cache, cancellationToken);
                    if (problem is not null)
                        return ([], problem);
                    return ([new ExportItem
                    {
                        Kind = ExportItemKind.Segment, VodId = marker.VodId, MarkerId = marker.Id, StartSeconds = marker.Seconds, EndSeconds = end,
                        Cuts = CutOuts.Write(CutOuts.Normalize(CutOuts.Read(marker.Cuts), marker.Seconds, end)), Quality = vod.Quality,
                        FileName = string.IsNullOrEmpty(fileName) ? (marker.Label.Length > 0 ? marker.Label : $"{vod.Details.Title} {TrimTime.Format(TimeSpan.FromSeconds(Math.Floor(marker.Seconds)))}") : fileName,
                    }], null);
                }

                case "video":
                {
                    var video = request.VideoId is { } videoId
                        ? await db.Videos.AsNoTracking().Include(x => x.Segments).ThenInclude(x => x.Marker).FirstOrDefaultAsync(x => x.Id == videoId, cancellationToken)
                        : null;
                    if (video is null)
                        return ([], Invalid(index, "That video is gone."));
                    var links = video.Segments.Where(x => x.Marker is { EndSeconds: not null }).OrderBy(x => x.Position).ToList();
                    if (links.Count == 0)
                        return ([], Invalid(index, "That video has no segments."));
                    var (vod, problem) = await VodForCutAsync(index, video.VodId, links.Max(x => x.Marker!.EndSeconds!.Value), request.Quality, cache,
                        cancellationToken);
                    if (problem is not null)
                        return ([], problem);
                    var parts = links.Select((x, i) =>
                    {
                        var (start, end) = (x.Marker!.Seconds, x.Marker.EndSeconds!.Value);
                        return new ExportVideoPart(x.Marker.Label.Length > 0 ? x.Marker.Label : $"Segment {i + 1}", start, end,
                            CutOuts.Normalize(CutOuts.Read(x.Marker.Cuts).Concat(CutOuts.Read(x.Cuts)), start, end));
                    }).Where(x => x.KeptSeconds >= CutOuts.MinKeptPartSeconds).ToList(); // an applied edit may take a segment out whole
                    if (parts.Count == 0)
                        return ([], Invalid(index, "Everything in that video is cut out."));
                    return ([new ExportItem
                    {
                        Kind = ExportItemKind.Video, VodId = video.VodId, VideoId = video.Id, StartSeconds = parts.Min(x => x.Start),
                        EndSeconds = parts.Max(x => x.End), Parts = ExportVideoPart.Write(parts), Joined = request.Joined, Quality = vod.Quality,
                        FileName = string.IsNullOrEmpty(fileName) ? video.Title : fileName,
                    }], null);
                }

                case "clip":
                {
                    var entry = request.EntryId is { } entryId ? await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == entryId, cancellationToken) : null;
                    if (entry is null)
                        return ([], Invalid(index, "That clip is gone."));
                    return await BuildClipAsync(index, request, entry, fileName, CutOuts.Read(entry.Cuts), null, team, db, cache, shortform, cancellationToken);
                }

                case "edit":
                {
                    var source = request.EditId is { } editId ? await ItemEditEndpoints.ForExportAsync(db, editId, cancellationToken) : null;
                    if (source is null)
                        return ([], Invalid(index, "That edit is gone."));
                    var edit = source.Edit;
                    var name = string.IsNullOrEmpty(fileName) ? (edit.Title.Length > 0 ? edit.Title : null) : fileName;
                    switch (source.Kind)
                    {
                        case ItemEditKind.Clip:
                            return await BuildClipAsync(index, request, source.Entry!, name, source.Cuts, edit.Id, team, db, cache, shortform, cancellationToken);
                        case ItemEditKind.Segment:
                        {
                            var marker = source.Marker!;
                            var end = marker.EndSeconds!.Value;
                            var (vod, problem) = await VodForCutAsync(index, marker.VodId, end, request.Quality, cache, cancellationToken);
                            if (problem is not null)
                                return ([], problem);
                            return ([new ExportItem
                            {
                                Kind = ExportItemKind.Segment, VodId = marker.VodId, MarkerId = marker.Id, EditId = edit.Id, StartSeconds = marker.Seconds,
                                EndSeconds = end, Cuts = CutOuts.Write(source.Cuts), Quality = vod.Quality,
                                FileName = name ?? (marker.Label.Length > 0 ? marker.Label : $"{vod.Details.Title} {TrimTime.Format(TimeSpan.FromSeconds(Math.Floor(marker.Seconds)))}"),
                            }], null);
                        }
                        default:
                        {
                            var video = source.Video!;
                            if (source.VideoParts.Count == 0)
                                return ([], Invalid(index, "That edit leaves nothing of the video."));
                            var (vod, problem) = await VodForCutAsync(index, video.VodId, source.VideoParts.Max(x => x.End), request.Quality, cache, cancellationToken);
                            if (problem is not null)
                                return ([], problem);
                            return ([new ExportItem
                            {
                                Kind = ExportItemKind.Video, VodId = video.VodId, VideoId = video.Id, EditId = edit.Id,
                                StartSeconds = source.VideoParts.Min(x => x.Start), EndSeconds = source.VideoParts.Max(x => x.End),
                                Parts = ExportVideoPart.Write(source.VideoParts), Joined = request.Joined, Quality = vod.Quality, FileName = name ?? video.Title,
                            }], null);
                        }
                    }
                }

                case "project":
                    return await BuildProjectAsync(index, request, fileName, team, db, cache, cancellationToken);

                default:
                    return ([], Invalid(index, "Kind must be vod, clip, video, segment, edit or project."));
            }
        }

        /// <summary>
        /// A clip's items (horizontal and/or vertical) without <paramref name="cuts"/>: its own cut-out parts, or an edit's (<paramref name="editId"/>,
        /// whose vertical is a render made for that edit; a plain clip's vertical is a render made without one).
        /// </summary>
        private static async Task<(List<ExportItem> Items, IResult? Problem)> BuildClipAsync(int index, ExportItemRequest request, LibraryEntry entry,
            string? fileName, IReadOnlyList<CutRange> cuts, int? editId, TeamContext team, LibraryDbContext db, VodInfoCache cache,
            ShortformService shortform, CancellationToken cancellationToken)
        {
            if (!request.Horizontal && !request.Vertical)
                return ([], Invalid(index, "Pick horizontal, vertical or both."));
            if (entry.Kind != EntryKind.Vod)
                return ([], Invalid(index, "Twitch clips can't be exported here; use the library card's download."));
            if (entry.Status == EntryStatus.VodUnavailable && entry.FilePath is null)
                return ([], Coded(StatusCodes.Status410Gone, "vod-gone", $"\"{entry.Title}\": the VOD is gone from Twitch."));
            if (await cache.RefusalAsync(entry.VodId, cancellationToken) is { } refused)
                return ([], refused);

            var name = string.IsNullOrEmpty(fileName) ? entry.Title : fileName;
            var made = new List<ExportItem>();
            var end = entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds;
            if (request.Horizontal)
            {
                // Its own quality by default: a stored clip is then copied, nothing is downloaded
                string? quality = entry.Quality;
                if (!string.IsNullOrWhiteSpace(request.Quality) && entry.Status != EntryStatus.VodUnavailable
                    && !(entry.FilePath is not null && string.Equals(request.Quality.Trim(), entry.Quality, StringComparison.OrdinalIgnoreCase))
                    // VOD deleted: the stored file's quality is the only one left
                    && !(entry.FilePath is not null && await cache.DetailsAsync(entry.VodId) is null))
                {
                    var (vod, problem) = await VodForCutAsync(index, entry.VodId, end, request.Quality, cache, cancellationToken);
                    if (problem is not null)
                        return ([], problem);
                    quality = vod.Quality;
                }
                made.Add(new ExportItem
                {
                    Kind = ExportItemKind.Clip, VodId = entry.VodId, EntryId = entry.Id, StartSeconds = entry.StartTime.TotalSeconds, EndSeconds = end,
                    Cuts = CutOuts.Write(CutOuts.Normalize(cuts, entry.StartTime.TotalSeconds, end)), TrimMode = entry.TrimMode,
                    Quality = quality, FileName = name, EditId = editId,
                });
            }

            if (request.Vertical && editId is null && await VerticalEditOfClipAsync(request, entry, team, db, cancellationToken) is var (project, editError)
                && (project is not null || editError is not null))
            {
                // A vertical edit of the clip (badrfly 2026-11-17: the vertical editor makes these now, not clip renders)
                if (editError is not null)
                    return ([], Coded(StatusCodes.Status409Conflict, "no-vertical", $"\"{entry.Title}\": {editError}"));
                var (items, problem) = await ProjectItemsAsync(index, project!, request.Quality, name, cache, cancellationToken);
                if (problem is not null)
                    return ([], problem);
                made.AddRange(items);
            }
            else if (request.Vertical)
            {
                if (PickRender(shortform.ListRenders(entry.Id).Where(x => x.EditId == editId).ToList(), request.RenderId, team, out var error) is not { } render)
                    return ([], Coded(StatusCodes.Status409Conflict, "no-vertical", $"\"{entry.Title}\": {error}"));
                made.Add(new ExportItem
                {
                    Kind = ExportItemKind.ClipVertical, VodId = entry.VodId, EntryId = entry.Id, StartSeconds = entry.StartTime.TotalSeconds,
                    EndSeconds = end, RenderId = render.Id, FileName = name, EditId = editId,
                });
            }
            return (made, null);
        }

        /// <summary>
        /// A project's items (landscape and/or 9:16): what it plays now is copied. Every VOD it plays must be open to the team, on Twitch and
        /// streamed far enough; the quality asked for is used where a VOD has it (each VOD's best otherwise, the output is one size anyway).
        /// </summary>
        private static async Task<(List<ExportItem> Items, IResult? Problem)> BuildProjectAsync(int index, ExportItemRequest request, string? fileName,
            TeamContext team, LibraryDbContext db, VodInfoCache cache, CancellationToken cancellationToken)
        {
            var project = request.ProjectId is { } projectId
                ? await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
                : null;
            if (project is null || !ProjectEndpoints.CanSee(team, project))
                return ([], Invalid(index, "That project is gone."));
            if (!ProjectEndpoints.CanDownload(team, project))
                return ([], Invalid(index, "Your role doesn't let you export teammates' vertical edits."));

            var name = string.IsNullOrEmpty(fileName) ? (project.Title.Length > 0 ? project.Title : "Untitled project") : fileName;
            return await ProjectItemsAsync(index, project, request.Quality, name, cache, cancellationToken, request.Separate);
        }

        /// <summary>
        /// The vertical edit a clip's vertical file is made from: the one asked for (<see cref="ExportItemRequest.ProjectId"/>, one of the
        /// clip's vertical edits the caller may export), else the caller's newest vertical edit of the clip when no render was asked for.
        /// (null, null) = none: older clip renders are used.
        /// </summary>
        private static async Task<(EditProject? Project, string? Error)> VerticalEditOfClipAsync(ExportItemRequest request, LibraryEntry entry,
            TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (request.ProjectId is { } projectId)
            {
                var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
                if (project is null || project.EntryId != entry.Id || !project.Vertical || !ProjectEndpoints.CanSee(team, project))
                    return (null, "That vertical edit is gone.");
                if (!ProjectEndpoints.CanDownload(team, project))
                    return (null, "Your role doesn't let you export teammates' vertical edits.");
                return (project, null);
            }
            if (!string.IsNullOrWhiteSpace(request.RenderId))
                return (null, null);
            var own = await db.Projects.AsNoTracking()
                .Where(x => x.EntryId == entry.Id && x.Vertical && x.UserId == team.UserId)
                .OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            return (own, null);
        }

        /// <summary>A project's file: what it plays now is copied (landscape for a horizontal edit, 9:16 for a vertical one).</summary>
        private static async Task<(List<ExportItem> Items, IResult? Problem)> ProjectItemsAsync(int index, EditProject project, string? requestedQuality,
            string name, VodInfoCache cache, CancellationToken cancellationToken, bool separate = false)
        {
            var content = ProjectContent.Read(project.Content);
            var stretches = ProjectTimeline.Flatten(content);
            if (stretches.All(x => x.IsGap))
                return ([], Invalid(index, "That project has nothing on its visible rows."));
            // The edit's kind decides its shape (badrfly 2026-11-17): a vertical edit exports 9:16, a horizontal one landscape, whatever
            // the request's checkboxes say

            foreach (var vod in stretches.Where(x => !x.IsGap).GroupBy(x => x.Piece!.VodId))
            {
                var (_, problem) = await VodForCutAsync(index, vod.Key, vod.Max(x => x.VodEnd), null, cache, cancellationToken);
                if (problem is not null)
                    return ([], problem);
            }

            var quality = string.IsNullOrWhiteSpace(requestedQuality) ? null : requestedQuality.Trim();
            if (quality is not null && (VodInfoService.IsAudioQuality(quality) || quality.Length > 64))
                return ([], Invalid(index, "Pick a video quality."));

            var first = stretches.First(x => !x.IsGap);
            var json = content.Write();
            var made = new List<ExportItem>();
            foreach (var vertical in new[] { project.Vertical })
                made.Add(new ExportItem
                {
                    Kind = vertical ? ExportItemKind.ProjectVertical : ExportItemKind.Project, ProjectId = project.Id, VodId = first.Piece!.VodId,
                    StartSeconds = 0, EndSeconds = Math.Round(stretches.Sum(x => x.Duration), 2), Parts = json, Quality = quality, FileName = name,
                    Separate = separate,
                });
            return (made, null);
        }

        /// <summary>The VOD must be open to the team, on Twitch, not restricted, streamed up to <paramref name="end"/>, and have the quality.</summary>
        private static async Task<((VodDetails Details, string? Quality) Vod, IResult? Problem)> VodForCutAsync(int index, long vodId, double end,
            string? requestedQuality, VodInfoCache cache, CancellationToken cancellationToken)
        {
            if (await cache.RefusalAsync(vodId, cancellationToken) is { } refused)
                return (default, refused);
            if (await cache.DetailsAsync(vodId) is not { } vod)
                return (default, Results.Problem(detail: $"VOD {vodId} was not found. It may be deleted, expired or private.", statusCode: StatusCodes.Status404NotFound));
            if (end > vod.LengthSeconds + 1)
                return (default, Invalid(index, vod.IsLive ? "That part hasn't been streamed yet. Try again once it's streamed." : "That goes past the end of the VOD."));

            var qualities = await cache.QualitiesAsync(vodId);
            if (qualities is null)
                return (default, Results.Problem(detail: "This VOD is restricted (e.g. subscriber-only), which is not supported yet.", statusCode: StatusCodes.Status403Forbidden));

            var requested = string.IsNullOrWhiteSpace(requestedQuality) ? null : requestedQuality.Trim();
            if (requested is null)
                return ((vod, null), null);
            if (VodInfoService.IsAudioQuality(requested))
                return (default, Invalid(index, "Audio-only quality cannot produce an mp4."));
            var quality = qualities.GetQuality(requested)?.Name;
            return quality is null
                ? (default, Invalid(index, $"Quality \"{requested}\" is not available. Available: {string.Join(", ", VodInfoService.GetVideoQualityNames(qualities))}."))
                : ((vod, quality), null);
        }

        /// <summary>The caller's unexpired exports, newest first.</summary>
        private static async Task<IResult> List(TeamContext team, LibraryDbContext db, ExportQueue queue, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var batches = await db.ExportBatches.AsNoTracking().Include(x => x.Items)
                .Where(x => x.CreatedByUserId == team.UserId && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now))
                .OrderByDescending(x => x.Id).Take(50).ToListAsync(cancellationToken);
            var names = await TimelineEndpoints.UserNamesAsync(db, [team.UserId], cancellationToken);
            return Results.Ok(batches.Select(x => ToDto(x, names, team, queue)).ToList());
        }

        private static async Task<IResult> Get(int id, TeamContext team, LibraryDbContext db, ExportQueue queue, CancellationToken cancellationToken)
        {
            var batch = await FindAsync(db, id, cancellationToken);
            return batch is null ? NotFound(id) : Results.Ok(await ToDtoAsync(db, team, batch, queue, cancellationToken));
        }

        private static async Task<ExportBatch?> FindAsync(LibraryDbContext db, int id, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            return await db.ExportBatches.AsNoTracking().Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.Id == id && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now), cancellationToken);
        }

        private static IResult NotFound(int id) =>
            Results.Problem(detail: $"Export {id} was not found. Its files may have expired.", statusCode: StatusCodes.Status404NotFound);

        private static async Task<IResult> Download(int id, int position, HttpContext context, LibraryDbContext db, LibraryStorage storage,
            CancellationToken cancellationToken)
        {
            var batch = await FindAsync(db, id, cancellationToken);
            var item = batch?.Items.FirstOrDefault(x => x.Position == position);
            if (batch is null || item is null)
                return NotFound(id);
            if (item.Status != ExportStatus.Ready || item.FilePath is null)
                return Results.Problem(detail: "That file isn't ready.", statusCode: StatusCodes.Status409Conflict);

            var name = Safe(item.FileName) is { Length: > 0 } safe ? safe : $"export {batch.Id}-{item.Position}";
            var fullPath = storage.Resolve(item.FilePath);
            var range = context.Request.Headers.Range.ToString();
            var firstRequest = string.IsNullOrEmpty(range) || range.StartsWith("bytes=0-", StringComparison.Ordinal);

            if (item.Separate && item.Kind is ExportItemKind.Project or ExportItemKind.ProjectVertical)
            {
                if (!Directory.Exists(fullPath))
                    return Results.Problem(detail: "The files are missing from storage.", statusCode: StatusCodes.Status410Gone);
                db.Audit(AuditActions.FileServed, item.EntryId, $"export {batch.Id}, item {item.Position} (zip)");
                await db.SaveChangesAsync(cancellationToken);
                var pieces = ProjectTimeline.Flatten(ProjectContent.Read(item.Parts)).Where(x => !x.IsGap).ToList();
                var suffix = item.Kind == ExportItemKind.ProjectVertical ? " (vertical)" : "";
                await WriteZipAsync(context, $"{name}{suffix}.zip", pieces.Select((p, i) => (Path.Combine(fullPath, $"{i + 1}.mp4"),
                    $"{i + 1:00} {(Safe(p.Piece!.Label ?? "") is { Length: > 0 } label ? label : $"VOD {p.Piece.VodId}")}{suffix}.mp4")), cancellationToken);
                return Results.Empty;
            }

            if (item.Kind == ExportItemKind.Video && !item.Joined)
            {
                if (!Directory.Exists(fullPath))
                    return Results.Problem(detail: "The files are missing from storage.", statusCode: StatusCodes.Status410Gone);
                db.Audit(AuditActions.FileServed, item.EntryId, $"export {batch.Id}, item {item.Position} (zip)");
                await db.SaveChangesAsync(cancellationToken);
                var parts = ExportVideoPart.Read(item.Parts);
                await WriteZipAsync(context, $"{name}.zip", parts.Select((p, i) => (Path.Combine(fullPath, $"{i + 1}.mp4"), $"{i + 1:00} {Safe(p.Label)}.mp4")),
                    cancellationToken);
                return Results.Empty;
            }

            if (!File.Exists(fullPath))
                return Results.Problem(detail: "The file is missing from storage.", statusCode: StatusCodes.Status410Gone);
            if (firstRequest)
            {
                db.Audit(AuditActions.FileServed, item.EntryId, $"export {batch.Id}, item {item.Position}");
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.File(fullPath, "video/mp4", item.Kind is ExportItemKind.ClipVertical or ExportItemKind.ProjectVertical ? $"{name} (vertical).mp4" : $"{name}.mp4",
                enableRangeProcessing: true);
        }

        /// <summary>The files in a .zip written straight to the response (stored, not compressed: mp4 doesn't shrink).</summary>
        private static Task WriteZipAsync(HttpContext context, string name, IEnumerable<(string Path, string Name)> files, CancellationToken cancellationToken)
        {
            context.Response.ContentType = "application/zip";
            // SetHttpFileName adds an ASCII filename plus filename*=UTF-8''... (a raw non-ASCII header value makes Kestrel drop the response)
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(name);
            context.Response.Headers.ContentDisposition = disposition.ToString();
            if (context.Features.Get<IHttpBodyControlFeature>() is { } bodyControl)
                bodyControl.AllowSynchronousIO = true;
            using var zip = new ZipArchive(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var (path, entryName) in files.Where(x => File.Exists(x.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                zip.CreateEntryFromFile(path, entryName, CompressionLevel.NoCompression);
            }
            return Task.CompletedTask;
        }

        private static async Task<IResult> Delete(int id, TeamContext team, LibraryDbContext db, LibraryStorage storage, ExportQueue queue,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var batch = await db.ExportBatches.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (batch is null)
                return NotFound(id);
            if (!CanDelete(team, batch))
                return Results.Problem(detail: "Your role only lets you delete exports you started.", statusCode: StatusCodes.Status403Forbidden);

            db.ExportBatches.Remove(batch);
            db.Audit(AuditActions.ExportDeleted, null, $"Export {batch.Id}");
            await db.SaveChangesAsync(cancellationToken);

            // A running job deletes its folder when it stops; otherwise do it now (the cleanup sweep gets anything left open)
            queue.Cancel(batch.Id);
            if (!queue.IsRunning(batch.Id))
            {
                var folder = storage.Resolve(LibraryStorage.ExportRelativeFolder(batch.Id));
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    loggerFactory.CreateLogger("TwitchDownloaderWeb.Exports").LogWarning("Couldn't delete {Path}: {Message}", folder, ex.Message);
                }
            }
            return Results.NoContent();
        }

        private static string Safe(string text) => FilenameService.ReplaceInvalidFilenameChars(text)?.Trim().TrimEnd('.') ?? "";

        private static async Task<ExportBatchDto> ToDtoAsync(LibraryDbContext db, TeamContext team, ExportBatch batch, ExportQueue queue,
            CancellationToken cancellationToken) =>
            ToDto(batch, await TimelineEndpoints.UserNamesAsync(db, [batch.CreatedByUserId], cancellationToken), team, queue);

        private static ExportBatchDto ToDto(ExportBatch x, IReadOnlyDictionary<int, string> names, TeamContext team, ExportQueue queue)
        {
            var url = $"/api/exports/{x.Id}";
            return new ExportBatchDto(x.Id, x.Status.ToString(), x.Error, x.CreatedByUserId, names.GetValueOrDefault(x.CreatedByUserId), x.CreatedAtUtc,
                x.ExpiresAtUtc, x.Items.Count(i => i.Status is ExportStatus.Ready or ExportStatus.Failed), x.Items.Count, CanDelete(team, x),
                x.Items.OrderBy(i => i.Position).Select(i => new ExportItemDto(i.Position, i.Kind.ToString(), i.FileName, i.Quality, i.VodId, i.StartSeconds,
                    i.EndSeconds, CutOuts.Read(i.Cuts), i.EntryId, i.MarkerId, i.VideoId, i.RenderId, i.Joined, Math.Round(LengthOf(i), 2), i.Status.ToString(),
                    i.Error, i.PartsDone, i.PartsTotal, i.FileSizeBytes, i.AudioRestoredSeconds, i.AudioMutedSeconds,
                    i.Status == ExportStatus.Ready ? $"{url}/items/{i.Position}/file" : null)
                {
                    EditId = i.EditId,
                    ProjectId = i.ProjectId,
                    Separate = i.Separate,
                    Progress = i.Status == ExportStatus.Running ? queue.ProgressOf(x.Id, i.Position) : null,
                }).ToList());
        }
    }
}
