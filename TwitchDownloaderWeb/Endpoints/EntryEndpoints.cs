using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Services;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="VodId">A VOD ID or twitch.tv/videos URL.</param>
    /// <param name="Title">Blank uses the VOD's Twitch title.</param>
    /// <param name="Quality">e.g. "1080p60". Blank uses the best available quality.</param>
    /// <param name="TrimMode">"Exact" (default) or "Safe".</param>
    /// <param name="StartTime">"h:mm:ss" offset from the start of the VOD. Blank is the start.</param>
    /// <param name="EndTime">"h:mm:ss" offset from the start of the VOD, not a duration. Blank is the end.</param>
    public sealed record AddEntryRequest(string? VodId, string? Title, string? Quality, string? TrimMode, string? StartTime, string? EndTime);

    public sealed record RenameEntryRequest(string? Title);

    public sealed record EntryDto(
        int Id,
        string Title,
        long VodId,
        string? ChannelName,
        string? Quality,
        string TrimMode,
        string StartTime,
        string? EndTime,
        double ClipLengthSeconds,
        int VodLengthSeconds,
        DateTime StreamDateUtc,
        DateTime AddedAtUtc,
        DateTime UpdatedAtUtc,
        string? ThumbnailUrl,
        string Status,
        string? Error,
        bool IsReferenceOnly,
        long? FileSizeBytes,
        string GenerationStatus,
        long? GeneratedFileSizeBytes,
        DateTime? GeneratedExpiresAtUtc,
        bool CanDownload,
        double? AudioRestoredSeconds,
        double? AudioMutedSeconds,
        string? AddedByUserId)
    {
        public static EntryDto From(LibraryEntry entry) => new(
            entry.Id,
            entry.Title,
            entry.VodId,
            entry.ChannelName,
            entry.Quality,
            entry.TrimMode.ToString(),
            TrimTime.Format(entry.StartTime),
            TrimTime.Format(entry.EndTime),
            entry.ClipLengthSeconds,
            entry.VodLengthSeconds,
            entry.StreamDateUtc,
            entry.AddedAtUtc,
            entry.UpdatedAtUtc,
            entry.ThumbnailUrl,
            entry.Status.ToString(),
            entry.Error,
            entry.Status == EntryStatus.ReferenceOnly || (entry.Status == EntryStatus.VodUnavailable && entry.FilePath is null),
            entry.FileSizeBytes,
            entry.GenerationStatus.ToString(),
            entry.GeneratedFileSizeBytes,
            entry.GeneratedExpiresAtUtc,
            entry.Status == EntryStatus.Ready || HasUnexpiredGeneratedFile(entry),
            entry.AudioRestoredSeconds,
            entry.AudioMutedSeconds,
            entry.AddedByUserId);

        /// <summary>The cleanup service deletes expired files periodically, so check the expiry itself rather than the status.</summary>
        public static bool HasUnexpiredGeneratedFile(LibraryEntry entry) =>
            entry.Status == EntryStatus.ReferenceOnly
            && entry.GenerationStatus == Data.GenerationStatus.Ready
            && entry.GeneratedExpiresAtUtc > DateTime.UtcNow;
    }

    public sealed record VodInfoDto(long VodId, string Title, string? ChannelName, int LengthSeconds, string Length, DateTime StreamDateUtc,
        string? ThumbnailUrl, string[] Qualities, bool Restricted, int MaxStoredClipSeconds);

    public static class EntryEndpoints
    {
        private const int MaxTitleLength = 500;

        public static IEndpointRouteBuilder MapEntryEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/info", GetVodInfo);

            var entries = app.MapGroup("/api/entries");
            entries.MapPost("/", AddEntry);
            entries.MapGet("/", ListEntries);
            entries.MapGet("/{id:int}", GetEntry);
            entries.MapPatch("/{id:int}", RenameEntry);
            entries.MapDelete("/{id:int}", DeleteEntry);
            entries.MapPost("/{id:int}/generate", GenerateEntry);
            entries.MapGet("/{id:int}/file", DownloadEntryFile);

            return app;
        }

        private static async Task<IResult> GetVodInfo(string? vod, VodInfoService vodInfo, IOptions<DownloadSettings> settings)
        {
            if (VodInfoService.ParseVodId(vod) is not { } vodId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vod"] = ["Unable to parse VOD ID/URL."] });

            var details = await vodInfo.GetVodAsync(vodId);
            if (details is null)
                return VodNotFound(vodId);

            var qualities = await vodInfo.GetQualitiesAsync(vodId);

            return Results.Ok(new VodInfoDto(vodId, details.Title, details.ChannelName, details.LengthSeconds,
                TrimTime.Format(TimeSpan.FromSeconds(details.LengthSeconds)), details.StreamDateUtc, details.ThumbnailUrl,
                qualities is null ? [] : VodInfoService.GetVideoQualityNames(qualities), qualities is null,
                settings.Value.MaxStoredClipSeconds));
        }

        private static async Task<IResult> AddEntry(AddEntryRequest request, LibraryDbContext db, VodInfoService vodInfo, DownloadQueue queue,
            IOptions<DownloadSettings> settings, CancellationToken cancellationToken)
        {
            if (VodInfoService.ParseVodId(request.VodId) is not { } vodId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vodId"] = ["Unable to parse VOD ID/URL."] });

            if (!TrimTime.TryParse(request.StartTime, out var parsedStart))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startTime"] = ["Use h:mm:ss, m:ss or seconds."] });

            if (!TrimTime.TryParse(request.EndTime, out var endTime))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endTime"] = ["Use h:mm:ss, m:ss or seconds."] });

            var trimMode = VideoTrimMode.Exact;
            // Enum.TryParse also accepts numbers like "5", so require one of the names
            if (!string.IsNullOrWhiteSpace(request.TrimMode)
                && !(Enum.TryParse(request.TrimMode, true, out trimMode) && Enum.GetNames<VideoTrimMode>().Contains(request.TrimMode.Trim(), StringComparer.OrdinalIgnoreCase)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["trimMode"] = ["Trim mode must be Exact or Safe."] });

            var requestedQuality = string.IsNullOrWhiteSpace(request.Quality) ? null : request.Quality.Trim();
            if (VodInfoService.IsAudioQuality(requestedQuality))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["quality"] = ["Audio-only quality cannot produce an mp4."] });

            var title = request.Title?.Trim();
            if (title is { Length: > MaxTitleLength })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"Title cannot be longer than {MaxTitleLength} characters."] });

            var vod = await vodInfo.GetVodAsync(vodId);
            if (vod is null)
                return VodNotFound(vodId);

            var startTime = parsedStart ?? TimeSpan.Zero;
            if (VodInfoService.ValidateTrim(startTime, endTime, vod.LengthSeconds) is { } trimErrors)
                return Results.ValidationProblem(trimErrors);

            var qualities = await vodInfo.GetQualitiesAsync(vodId);
            if (qualities is null)
                return RestrictedVod();

            // Store the resolved name ("720p" -> "720p60") so the entry shows what will actually be downloaded
            string? quality = null;
            if (requestedQuality is not null)
            {
                quality = qualities.GetQuality(requestedQuality)?.Name;
                if (quality is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["quality"] = [$"Quality \"{requestedQuality}\" is not available. Available: {string.Join(", ", VodInfoService.GetVideoQualityNames(qualities))}."]
                    });
                }
            }

            // The stored-vs-reference rule is decided here from the trim times, never by the client
            var clipLength = (endTime ?? TimeSpan.FromSeconds(vod.LengthSeconds)) - startTime;
            var isStored = clipLength.TotalSeconds <= settings.Value.MaxStoredClipSeconds;
            var now = DateTime.UtcNow;

            var entry = new LibraryEntry
            {
                Title = string.IsNullOrEmpty(title) ? vod.Title : title,
                VodId = vodId,
                ChannelName = vod.ChannelName,
                Quality = quality,
                TrimMode = trimMode,
                StartTime = startTime,
                EndTime = endTime,
                ClipLengthSeconds = clipLength.TotalSeconds,
                VodLengthSeconds = vod.LengthSeconds,
                StreamDateUtc = vod.StreamDateUtc,
                AddedAtUtc = now,
                UpdatedAtUtc = now,
                ThumbnailUrl = vod.ThumbnailUrl,
                Status = isStored ? EntryStatus.Queued : EntryStatus.ReferenceOnly,
            };

            // Two saves because the audit event needs the new entry's ID; the transaction keeps them together
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                db.Entries.Add(entry);
                await db.SaveChangesAsync(cancellationToken);

                db.Audit(AuditActions.EntryAdded, entry.Id,
                    $"VOD {vodId}, {TrimTime.Format(startTime)}-{TrimTime.Format(endTime) ?? "end"}, {clipLength.TotalSeconds:0.###}s, {(isStored ? "stored" : "reference-only")}");
                await db.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }

            if (isStored)
                queue.Enqueue(entry.Id, DownloadJobKind.Store);

            return Results.Created($"/api/entries/{entry.Id}", EntryDto.From(entry));
        }

        /// <param name="from">Inclusive lower bound, an ISO 8601 instant (e.g. "2026-10-09T07:00:00Z"). Applies to <paramref name="dateField"/>.</param>
        /// <param name="to">Exclusive upper bound, an ISO 8601 instant.</param>
        /// <param name="dateField">"dateAdded" (default) or "streamDate".</param>
        /// <remarks>
        /// Day boundaries are the client's job: it knows which time zone it is showing and converts that zone's midnights
        /// to UTC instants, so the server only ever compares UTC values.
        /// </remarks>
        private static async Task<IResult> ListEntries(LibraryDbContext db, string? search, string? sort, string? order, int? limit,
            string? from, string? to, string? dateField, CancellationToken cancellationToken)
        {
            if (!TryParseInstant(from, out var fromUtc))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["Use an ISO 8601 date-time with an offset, e.g. 2026-10-09T07:00:00Z."] });

            if (!TryParseInstant(to, out var toUtc))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Use an ISO 8601 date-time with an offset, e.g. 2026-10-10T07:00:00Z."] });

            if (fromUtc >= toUtc)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["\"to\" must be after \"from\"."] });

            var useStreamDate = false;
            switch (dateField?.ToLowerInvariant())
            {
                case null or "" or "dateadded":
                    break;
                case "streamdate":
                    useStreamDate = true;
                    break;
                default:
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateField"] = ["Date field must be streamDate or dateAdded."] });
            }

            var query = db.Entries.AsNoTracking();

            if (fromUtc is { } lower)
                query = useStreamDate ? query.Where(x => x.StreamDateUtc >= lower) : query.Where(x => x.AddedAtUtc >= lower);

            if (toUtc is { } upper)
                query = useStreamDate ? query.Where(x => x.StreamDateUtc < upper) : query.Where(x => x.AddedAtUtc < upper);

            if (!string.IsNullOrWhiteSpace(search))
            {
                // Escape LIKE wildcards so the search is a plain substring match. SQLite's LIKE ignores case for ASCII only.
                var pattern = "%" + search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
                query = query.Where(x => EF.Functions.Like(x.Title, pattern, @"\"));
            }

            var ascending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
            query = sort?.ToLowerInvariant() switch
            {
                "streamdate" => ascending ? query.OrderBy(x => x.StreamDateUtc).ThenBy(x => x.Id) : query.OrderByDescending(x => x.StreamDateUtc).ThenByDescending(x => x.Id),
                "dateadded" or null or "" => ascending ? query.OrderBy(x => x.AddedAtUtc).ThenBy(x => x.Id) : query.OrderByDescending(x => x.AddedAtUtc).ThenByDescending(x => x.Id),
                _ => null
            };

            if (query is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = ["Sort must be streamDate or dateAdded."] });

            var entries = await query.Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync(cancellationToken);
            return Results.Ok(entries.Select(EntryDto.From));
        }

        /// <summary>Parses an instant that states its offset; a bare local time would be ambiguous. Blank parses to null.</summary>
        private static bool TryParseInstant(string? text, out DateTime? utc)
        {
            utc = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var value = text.Trim();
            var hasOffset = value.EndsWith('Z') || value.EndsWith('z') || Regex.IsMatch(value, @"[+-]\d{2}:?\d{2}$");
            if (!hasOffset || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return false;

            utc = parsed.UtcDateTime;
            return true;
        }

        private static async Task<IResult> GetEntry(int id, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return entry is null ? EntryNotFound(id) : Results.Ok(EntryDto.From(entry));
        }

        private static async Task<IResult> RenameEntry(int id, RenameEntryRequest request, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var title = request.Title?.Trim();
            if (string.IsNullOrEmpty(title))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Title cannot be blank."] });

            if (title.Length > MaxTitleLength)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"Title cannot be longer than {MaxTitleLength} characters."] });

            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            if (entry.Title != title)
            {
                db.Audit(AuditActions.EntryRenamed, entry.Id, $"\"{entry.Title}\" -> \"{title}\"");
                entry.Title = title;
                entry.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            return Results.Ok(EntryDto.From(entry));
        }

        private static async Task<IResult> DeleteEntry(int id, LibraryDbContext db, DownloadQueue queue, LibraryStorage storage, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            queue.Cancel(id);

            db.Entries.Remove(entry);
            db.Audit(AuditActions.EntryDeleted, id, $"\"{entry.Title}\" (VOD {entry.VodId}, {entry.Status})");
            await db.SaveChangesAsync(cancellationToken);

            // Files that can't be deleted right now (e.g. open in another request) are removed by the cleanup sweep
            storage.TryDelete(entry.FilePath);
            storage.TryDelete(entry.GeneratedFilePath);
            storage.TryDelete(LibraryStorage.StoredRelativePath(id));
            storage.TryDelete(LibraryStorage.GeneratedRelativePath(id));

            return Results.NoContent();
        }

        /// <summary>
        /// Reference-only entries: queues an on-demand file (202) or returns the entry if one is ready (200).
        /// Failed stored entries: re-queues the download. Use GET /file once the entry's canDownload is true.
        /// </summary>
        private static async Task<IResult> GenerateEntry(int id, LibraryDbContext db, DownloadQueue queue, LibraryStorage storage, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            switch (entry.Status)
            {
                case EntryStatus.VodUnavailable:
                    return Results.Problem(detail: "The VOD is no longer available on Twitch.", statusCode: StatusCodes.Status410Gone);

                case EntryStatus.Ready:
                    return Results.Ok(EntryDto.From(entry));

                case EntryStatus.Queued or EntryStatus.Downloading:
                    return Results.Accepted($"/api/entries/{id}", EntryDto.From(entry));

                case EntryStatus.Failed:
                    entry.Status = EntryStatus.Queued;
                    entry.Error = null;
                    entry.UpdatedAtUtc = DateTime.UtcNow;
                    db.Audit(AuditActions.GenerationRequested, id, "Retry of failed stored download");
                    await db.SaveChangesAsync(cancellationToken);
                    queue.Enqueue(id, DownloadJobKind.Store);
                    return Results.Accepted($"/api/entries/{id}", EntryDto.From(entry));
            }

            // Reference-only
            switch (entry.GenerationStatus)
            {
                case GenerationStatus.Queued or GenerationStatus.Generating:
                    return Results.Accepted($"/api/entries/{id}", EntryDto.From(entry));

                case GenerationStatus.Ready when EntryDto.HasUnexpiredGeneratedFile(entry) && File.Exists(storage.Resolve(entry.GeneratedFilePath!)):
                    return Results.Ok(EntryDto.From(entry));
            }

            // None, Failed, or a Ready file that expired or went missing
            storage.TryDelete(entry.GeneratedFilePath);
            entry.GenerationStatus = GenerationStatus.Queued;
            entry.GeneratedFilePath = null;
            entry.GeneratedFileSizeBytes = null;
            entry.GeneratedExpiresAtUtc = null;
            entry.Error = null;
            entry.UpdatedAtUtc = DateTime.UtcNow;
            db.Audit(AuditActions.GenerationRequested, id);
            await db.SaveChangesAsync(cancellationToken);
            queue.Enqueue(id, DownloadJobKind.Generate);

            return Results.Accepted($"/api/entries/{id}", EntryDto.From(entry));
        }

        private static async Task<IResult> DownloadEntryFile(int id, HttpRequest request, LibraryDbContext db, LibraryStorage storage, ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            string? relativePath;
            string kind;
            if (entry.Status == EntryStatus.Ready)
            {
                relativePath = entry.FilePath;
                kind = "stored";
            }
            else if (EntryDto.HasUnexpiredGeneratedFile(entry))
            {
                relativePath = entry.GeneratedFilePath;
                kind = "on-demand";
            }
            else
            {
                var hint = entry.Status == EntryStatus.ReferenceOnly ? $" Request it with POST /api/entries/{id}/generate first." : "";
                return Results.Problem(detail: $"The file for entry {id} is not ready (status {entry.Status}, generation {entry.GenerationStatus}).{hint}",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var fullPath = storage.Resolve(relativePath!);
            if (!File.Exists(fullPath))
            {
                loggerFactory.CreateLogger("TwitchDownloaderWeb.Entries").LogError("File for entry {EntryId} is missing: {Path}", id, fullPath);

                // Put the entry in a state the user can recover from with /generate
                if (kind == "stored")
                {
                    entry.Status = EntryStatus.Failed;
                    entry.FilePath = null;
                    entry.FileSizeBytes = null;
                }
                else
                {
                    entry.GenerationStatus = GenerationStatus.None;
                    entry.GeneratedFilePath = null;
                    entry.GeneratedFileSizeBytes = null;
                    entry.GeneratedExpiresAtUtc = null;
                }

                entry.Error = "The file was missing from storage.";
                entry.UpdatedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.DownloadFailed, id, $"{kind} file missing from storage");
                await db.SaveChangesAsync(cancellationToken);
                return Results.Problem(detail: entry.Error, statusCode: StatusCodes.Status410Gone);
            }

            // Browsers and players may fetch a file in several ranges; only record the request that starts at the beginning
            var range = request.Headers.Range.ToString();
            if (string.IsNullOrEmpty(range) || range.StartsWith("bytes=0-", StringComparison.Ordinal))
            {
                db.Audit(AuditActions.FileServed, id, kind);
                await db.SaveChangesAsync(cancellationToken);
            }

            return Results.File(fullPath, "video/mp4", GetDownloadFileName(entry), enableRangeProcessing: true);
        }

        private static string GetDownloadFileName(LibraryEntry entry)
        {
            // Windows silently drops trailing dots and spaces, which could leave "" or ".." as the name
            var name = FilenameService.ReplaceInvalidFilenameChars(entry.Title)?.Trim().TrimEnd('.');
            return (string.IsNullOrEmpty(name) ? entry.VodId.ToString() : name) + ".mp4";
        }

        private static IResult EntryNotFound(int id) =>
            Results.Problem(detail: $"Entry {id} was not found.", statusCode: StatusCodes.Status404NotFound);

        private static IResult VodNotFound(long vodId) =>
            Results.Problem(detail: $"VOD {vodId} was not found. It may be deleted, expired or private.", statusCode: StatusCodes.Status404NotFound);

        private static IResult RestrictedVod() =>
            Results.Problem(detail: "This VOD is restricted (e.g. subscriber-only), which is not supported yet.", statusCode: StatusCodes.Status403Forbidden);
    }
}
