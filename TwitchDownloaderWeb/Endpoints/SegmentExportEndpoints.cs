using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services.Segments;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="MarkerIds">The team's segments on the VOD to download (markers with an end).</param>
    /// <param name="Combined">Also join them into one file (only for teams the app Owner turned it on for).</param>
    /// <param name="Quality">Like "720p60"; blank = the entry's quality when <paramref name="EntryId"/> is given, else best.</param>
    /// <param name="EntryId">The library card the segments were picked on (names the files, gives the quality).</param>
    public sealed record SegmentExportRequest(int[]? MarkerIds, bool Combined = false, string? Quality = null, int? EntryId = null);

    public sealed record SegmentExportItemDto(int Position, int? MarkerId, string Label, double StartSeconds, double EndSeconds, string Status, string? Error,
        long? FileSizeBytes, double? AudioRestoredSeconds, double? AudioMutedSeconds, string? FileUrl);

    /// <param name="ZipUrl">All ready segment files in one .zip (no compression), once the download is finished.</param>
    public sealed record SegmentExportDto(int Id, long VodId, int? EntryId, string Title, string? Quality, bool Combined, string Status, string? Error,
        int CreatedByUserId, string? CreatedBy, DateTime CreatedAtUtc, DateTime? ExpiresAtUtc, long? CombinedFileSizeBytes, string? CombinedFileUrl,
        string? ZipUrl, bool CanDelete, IReadOnlyList<SegmentExportItemDto> Items);

    /// <summary>
    /// Segment downloads: pick some of a VOD's team segments on a card and download them, one file per segment (every team) or also joined
    /// into one (teams the app Owner enabled, <see cref="Team.CombineSegmentDownloads"/>). Segments never become library entries.
    /// </summary>
    public static class SegmentExportEndpoints
    {
        public static IEndpointRouteBuilder MapSegmentExportEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/vods/{vodId:long}/segment-downloads", Create).RequireTeam(TeamPermission.GenerateFiles).RequireChannelVod();
            var group = app.MapGroup("/api/segment-downloads");
            group.MapGet("/", List).RequireTeam(TeamPermission.ViewLibrary);
            group.MapGet("/{id:int}", Get).RequireTeam(TeamPermission.ViewLibrary);
            group.MapGet("/{id:int}/files/{position:int}", DownloadSegment).RequireTeam(TeamPermission.ViewLibrary);
            group.MapGet("/{id:int}/combined", DownloadCombined).RequireTeam(TeamPermission.ViewLibrary);
            group.MapGet("/{id:int}/zip", DownloadZip).RequireTeam(TeamPermission.ViewLibrary);
            // Own with GenerateFiles (as when it was made), anyone's with DeleteAnyEntries; checked in the handler
            group.MapDelete("/{id:int}", Delete).RequireTeam(TeamPermission.GenerateFiles | TeamPermission.DeleteAnyEntries);
            return app;
        }

        private static bool CanDelete(TeamContext team, SegmentExport export) =>
            team.CanOnOwnOrAny(export.CreatedByUserId.ToString(), TeamPermission.GenerateFiles, TeamPermission.DeleteAnyEntries);

        private static IResult Invalid(string field, string message) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

        private static async Task<IResult> Create(long vodId, SegmentExportRequest request, TeamContext team, LibraryDbContext db, IVodDetailsSource vods,
            IVodQualitySource qualitySource, SegmentExportQueue queue, IOptions<DownloadSettings> options, CancellationToken cancellationToken)
        {
            var settings = options.Value;
            var markerIds = (request.MarkerIds ?? []).Distinct().ToList();
            if (markerIds.Count == 0)
                return Invalid("markerIds", "Pick at least one segment.");
            if (markerIds.Count > settings.MaxSegmentsPerDownload)
                return Invalid("markerIds", $"Pick at most {settings.MaxSegmentsPerDownload} segments at once.");

            if (request.Combined && !await db.Teams.AsNoTracking().AnyAsync(x => x.Id == team.TeamId && x.CombineSegmentDownloads, cancellationToken))
                return Results.Json(new { status = 403, code = "combine-not-enabled", detail = "Joining segments into one file isn't turned on for this team." },
                    statusCode: StatusCodes.Status403Forbidden, contentType: "application/problem+json");

            LibraryEntry? entry = null;
            if (request.EntryId is { } entryId)
            {
                entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == entryId, cancellationToken);
                if (entry is null)
                    return Results.Problem(detail: $"Entry {entryId} was not found.", statusCode: StatusCodes.Status404NotFound);
                if (entry.VodId != vodId)
                    return Invalid("entryId", "That entry isn't from this VOD.");
            }

            // The query filter keeps other teams' markers out
            var markers = await db.Markers.AsNoTracking().Where(x => x.VodId == vodId && markerIds.Contains(x.Id)).ToListAsync(cancellationToken);
            if (markers.Count != markerIds.Count)
                return Invalid("markerIds", "Some of those segments aren't on this VOD any more. Reload and pick again.");
            if (markers.Any(x => x.EndSeconds is null))
                return Invalid("markerIds", "Only segments can be downloaded, not single markers.");

            var active = await db.SegmentExports.CountAsync(x => x.Status == SegmentExportStatus.Queued || x.Status == SegmentExportStatus.Running, cancellationToken);
            if (active >= settings.MaxActiveSegmentDownloadsPerTeam)
                return Results.Problem(detail: $"Your team already has {active} segment downloads in progress. Wait for one to finish.",
                    statusCode: StatusCodes.Status409Conflict);

            var vod = await vods.GetVodAsync(vodId);
            if (vod is null)
                return Results.Problem(detail: $"VOD {vodId} was not found. It may be deleted, expired or private.", statusCode: StatusCodes.Status404NotFound);

            // Segments are cut whole; one that runs past what has been streamed (a live VOD) has to wait
            var ordered = markers.OrderBy(x => x.Seconds).ThenBy(x => x.EndSeconds).ThenBy(x => x.Id).ToList();
            foreach (var marker in ordered)
            {
                if (marker.EndSeconds > vod.LengthSeconds + 1)
                    return Invalid("markerIds", vod.IsLive
                        ? $"Segment \"{Name(marker)}\" ends after what has been streamed so far. Try again once it's streamed."
                        : $"Segment \"{Name(marker)}\" ends after the end of the VOD.");
            }

            var totalSeconds = ordered.Sum(x => Math.Min(x.EndSeconds!.Value, vod.LengthSeconds) - x.Seconds);
            if (totalSeconds > settings.MaxSegmentDownloadSeconds)
                return Invalid("markerIds", $"Those segments add up to {TrimTime.Format(TimeSpan.FromSeconds(totalSeconds))}; one download can be at most "
                    + $"{TrimTime.Format(TimeSpan.FromSeconds(settings.MaxSegmentDownloadSeconds))}.");

            var qualities = await qualitySource.GetQualitiesAsync(vodId);
            if (qualities is null)
                return Results.Problem(detail: "This VOD is restricted (e.g. subscriber-only), which is not supported yet.", statusCode: StatusCodes.Status403Forbidden);

            var requested = string.IsNullOrWhiteSpace(request.Quality) ? entry?.Quality : request.Quality.Trim();
            string? quality = null;
            if (requested is not null)
            {
                if (VodInfoService.IsAudioQuality(requested))
                    return Invalid("quality", "Audio-only quality cannot produce an mp4.");
                quality = qualities.GetQuality(requested)?.Name;
                if (quality is null)
                    return Invalid("quality", $"Quality \"{requested}\" is not available. Available: {string.Join(", ", VodInfoService.GetVideoQualityNames(qualities))}.");
            }

            var now = DateTime.UtcNow;
            var export = new SegmentExport
            {
                VodId = vodId,
                EntryId = entry?.Id,
                Title = entry?.Title ?? vod.Title,
                Quality = quality,
                Combined = request.Combined,
                Status = SegmentExportStatus.Queued,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Items = ordered.Select((m, i) => new SegmentExportItem
                {
                    Position = i + 1,
                    MarkerId = m.Id,
                    Label = m.Label,
                    StartSeconds = m.Seconds,
                    EndSeconds = Math.Min(m.EndSeconds!.Value, vod.LengthSeconds),
                    Status = SegmentExportItemStatus.Queued,
                }).ToList(),
            };
            db.SegmentExports.Add(export);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.SegmentsRequested, entry?.Id,
                $"Segment download {export.Id} of VOD {vodId}: {export.Items.Count} segment(s), {TrimTime.Format(TimeSpan.FromSeconds(totalSeconds))}"
                + (export.Combined ? ", joined into one file" : ", one file each"));
            await db.SaveChangesAsync(cancellationToken);

            queue.Enqueue(export.Id);
            return Results.Accepted($"/api/segment-downloads/{export.Id}", await ToDtoAsync(db, team, export, cancellationToken));
        }

        private static string Name(VodMarker marker) =>
            string.IsNullOrWhiteSpace(marker.Label) ? TrimTime.Format(TimeSpan.FromSeconds(Math.Floor(marker.Seconds))) : marker.Label;

        /// <summary>The team's unexpired segment downloads, newest first; <paramref name="vodIds"/> narrows them to some VODs.</summary>
        private static async Task<IResult> List(string? vodIds, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (ids, problem) = TimelineEndpoints.ParseVodIds(vodIds);
            if (problem is not null)
                return problem;

            var now = DateTime.UtcNow;
            var query = db.SegmentExports.AsNoTracking().Include(x => x.Items).Where(x => x.ExpiresAtUtc == null || x.ExpiresAtUtc > now);
            if (ids.Count > 0)
                query = query.Where(x => ids.Contains(x.VodId));
            var exports = await query.OrderByDescending(x => x.Id).Take(100).ToListAsync(cancellationToken);
            var names = await TimelineEndpoints.UserNamesAsync(db, exports.Select(x => x.CreatedByUserId), cancellationToken);
            return Results.Ok(exports.Select(x => ToDto(x, names, team)).ToList());
        }

        private static async Task<IResult> Get(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var export = await FindAsync(db, id, cancellationToken);
            return export is null ? NotFound(id) : Results.Ok(await ToDtoAsync(db, team, export, cancellationToken));
        }

        private static async Task<SegmentExport?> FindAsync(LibraryDbContext db, int id, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            return await db.SegmentExports.AsNoTracking().Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.Id == id && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now), cancellationToken);
        }

        private static IResult NotFound(int id) =>
            Results.Problem(detail: $"Segment download {id} was not found. Its files may have expired.", statusCode: StatusCodes.Status404NotFound);

        private static async Task<IResult> DownloadSegment(int id, int position, HttpRequest request, LibraryDbContext db, LibraryStorage storage,
            CancellationToken cancellationToken)
        {
            var export = await FindAsync(db, id, cancellationToken);
            var item = export?.Items.FirstOrDefault(x => x.Position == position);
            if (export is null || item is null)
                return NotFound(id);
            if (item.Status != SegmentExportItemStatus.Ready || item.FilePath is null)
                return Results.Problem(detail: "That segment's file isn't ready.", statusCode: StatusCodes.Status409Conflict);

            return await ServeAsync(request, db, storage, export, item.FilePath, $"{BaseName(export)} - {ItemName(item)}.mp4",
                $"segment {item.Position}", cancellationToken);
        }

        private static async Task<IResult> DownloadCombined(int id, HttpRequest request, LibraryDbContext db, LibraryStorage storage, CancellationToken cancellationToken)
        {
            var export = await FindAsync(db, id, cancellationToken);
            if (export is null)
                return NotFound(id);
            if (export.CombinedFilePath is null)
                return Results.Problem(detail: "There is no joined file for this download.", statusCode: StatusCodes.Status409Conflict);

            return await ServeAsync(request, db, storage, export, export.CombinedFilePath, $"{BaseName(export)} - segments.mp4", "joined", cancellationToken);
        }

        private static async Task<IResult> ServeAsync(HttpRequest request, LibraryDbContext db, LibraryStorage storage, SegmentExport export, string relativePath,
            string fileName, string what, CancellationToken cancellationToken)
        {
            var fullPath = storage.Resolve(relativePath);
            if (!File.Exists(fullPath))
                return Results.Problem(detail: "The file is missing from storage.", statusCode: StatusCodes.Status410Gone);

            var range = request.Headers.Range.ToString();
            if (string.IsNullOrEmpty(range) || range.StartsWith("bytes=0-", StringComparison.Ordinal))
            {
                db.Audit(AuditActions.FileServed, export.EntryId, $"segment download {export.Id}, {what}");
                await db.SaveChangesAsync(cancellationToken);
            }

            return Results.File(fullPath, "video/mp4", fileName, enableRangeProcessing: true);
        }

        /// <summary>Every ready segment file in a .zip, written straight to the response (stored, not compressed: mp4 doesn't shrink).</summary>
        private static async Task<IResult> DownloadZip(int id, HttpContext context, LibraryDbContext db, LibraryStorage storage, CancellationToken cancellationToken)
        {
            var export = await FindAsync(db, id, cancellationToken);
            if (export is null)
                return NotFound(id);
            if (export.Status is not (SegmentExportStatus.Ready or SegmentExportStatus.Failed))
                return Results.Problem(detail: "The segments are still being made.", statusCode: StatusCodes.Status409Conflict);

            var files = export.Items.Where(x => x.Status == SegmentExportItemStatus.Ready && x.FilePath is not null)
                .OrderBy(x => x.Position)
                .Select(x => (Item: x, Path: storage.Resolve(x.FilePath!)))
                .Where(x => File.Exists(x.Path))
                .ToList();
            if (files.Count == 0)
                return Results.Problem(detail: "None of the segment files are on disk.", statusCode: StatusCodes.Status410Gone);

            db.Audit(AuditActions.FileServed, export.EntryId, $"segment download {export.Id}, zip of {files.Count}");
            await db.SaveChangesAsync(cancellationToken);

            var name = $"{BaseName(export)} - segments.zip";
            context.Response.ContentType = "application/zip";
            // Titles often have emoji: SetHttpFileName adds an ASCII filename plus filename*=UTF-8''... (a raw non-ASCII header value makes
            // Kestrel drop the response, which the browser shows as "Site wasn't available")
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(name);
            context.Response.Headers.ContentDisposition = disposition.ToString();
            // ZipArchive writes synchronously; the archive streams out as it's made (no temporary copy on disk)
            if (context.Features.Get<IHttpBodyControlFeature>() is { } bodyControl)
                bodyControl.AllowSynchronousIO = true;
            using (var zip = new ZipArchive(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (item, path) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(path, $"{ItemName(item)}.mp4", CompressionLevel.NoCompression);
                }
            }
            return Results.Empty;
        }

        private static async Task<IResult> Delete(int id, TeamContext team, LibraryDbContext db, LibraryStorage storage, SegmentExportQueue queue,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var export = await db.SegmentExports.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (export is null)
                return NotFound(id);
            if (!CanDelete(team, export))
                return Results.Problem(detail: "Your role only lets you delete segment downloads you started.", statusCode: StatusCodes.Status403Forbidden);

            db.SegmentExports.Remove(export);
            db.Audit(AuditActions.SegmentsDeleted, export.EntryId, $"Segment download {export.Id} of VOD {export.VodId}");
            await db.SaveChangesAsync(cancellationToken);

            // A running job deletes its folder when it stops; otherwise do it now (the cleanup sweep gets anything left open)
            queue.Cancel(export.Id);
            if (!queue.IsRunning(export.Id))
            {
                var folder = storage.Resolve(LibraryStorage.SegmentExportRelativeFolder(export.Id));
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    loggerFactory.CreateLogger("TwitchDownloaderWeb.Segments").LogWarning("Couldn't delete {Path}: {Message}", folder, ex.Message);
                }
            }
            return Results.NoContent();
        }

        // ---------- names and DTOs ----------

        private static string Safe(string text) => FilenameService.ReplaceInvalidFilenameChars(text)?.Trim().TrimEnd('.') ?? "";

        private static string BaseName(SegmentExport export) => Safe(export.Title) is { Length: > 0 } title ? title : export.VodId.ToString();

        /// <summary>"02 Arky meets Sunny": numbered so names are unique and sort in time order.</summary>
        private static string ItemName(SegmentExportItem item)
        {
            var label = Safe(item.Label);
            var number = item.Position.ToString("00");
            return label.Length > 0 ? $"{number} {label}" : $"{number} {TrimTime.Format(TimeSpan.FromSeconds(Math.Floor(item.StartSeconds))).Replace(':', '.')}";
        }

        private static async Task<SegmentExportDto> ToDtoAsync(LibraryDbContext db, TeamContext team, SegmentExport export, CancellationToken cancellationToken) =>
            ToDto(export, await TimelineEndpoints.UserNamesAsync(db, [export.CreatedByUserId], cancellationToken), team);

        private static SegmentExportDto ToDto(SegmentExport x, IReadOnlyDictionary<int, string> names, TeamContext team)
        {
            var url = $"/api/segment-downloads/{x.Id}";
            var finished = x.Status is SegmentExportStatus.Ready or SegmentExportStatus.Failed;
            return new SegmentExportDto(x.Id, x.VodId, x.EntryId, x.Title, x.Quality, x.Combined, x.Status.ToString(), x.Error, x.CreatedByUserId,
                names.GetValueOrDefault(x.CreatedByUserId), x.CreatedAtUtc, x.ExpiresAtUtc, x.CombinedFileSizeBytes,
                x.CombinedFilePath is null ? null : $"{url}/combined",
                finished && x.Items.Any(i => i.Status == SegmentExportItemStatus.Ready) ? $"{url}/zip" : null,
                CanDelete(team, x),
                x.Items.OrderBy(i => i.Position).Select(i => new SegmentExportItemDto(i.Position, i.MarkerId, i.Label, i.StartSeconds, i.EndSeconds,
                    i.Status.ToString(), i.Error, i.FileSizeBytes, i.AudioRestoredSeconds, i.AudioMutedSeconds,
                    i.Status == SegmentExportItemStatus.Ready ? $"{url}/files/{i.Position}" : null)).ToList());
        }
    }
}
