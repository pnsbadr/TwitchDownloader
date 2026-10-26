using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Services;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="VodId">A VOD ID or twitch.tv/videos URL, or a clip link (clips.twitch.tv/…, twitch.tv/{channel}/clip/…) or slug.
    /// Clips are always reference-only and ignore TrimMode/StartTime/EndTime: they keep Twitch's cut.</param>
    /// <param name="Title">Blank uses the VOD's (or clip's) Twitch title.</param>
    /// <param name="Quality">e.g. "1080p60". Blank uses the best available quality.</param>
    /// <param name="TrimMode">"Exact" (default) or "Safe".</param>
    /// <param name="StartTime">"h:mm:ss" offset from the start of the VOD. Blank is the start.</param>
    /// <param name="EndTime">"h:mm:ss" offset from the start of the VOD, not a duration. Blank is the end.</param>
    public sealed record AddEntryRequest(string? VodId, string? Title, string? Quality, string? TrimMode, string? StartTime, string? EndTime);

    public sealed record RenameEntryRequest(string? Title);

    /// <param name="StartTime">"h:mm:ss" offset from the start of the VOD. Blank is the start.</param>
    /// <param name="EndTime">"h:mm:ss" offset from the start of the VOD, not a duration. Blank is the end.</param>
    public sealed record RetrimEntryRequest(string? StartTime, string? EndTime);

    public sealed record EntryDto(
        int Id,
        string Title,
        string Kind,
        string? ClipSlug,
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
            entry.Kind.ToString(),
            entry.ClipSlug,
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

    /// <summary>How much of a clip is muted, from its VOD's playlist. Only known when the VOD has an audio rescue.</summary>
    public sealed record ClipAudioDto(double MutedSeconds, double RestorableSeconds);

    /// <param name="VodId">The VOD the clip was cut from, if Twitch says (null once the VOD is gone).</param>
    /// <param name="VodStart">Where the clip sits in that VOD, "h:mm:ss" (whole seconds).</param>
    /// <param name="Audio">Null when unknown: no VOD, or no audio rescue for it.</param>
    public sealed record ClipInfoDto(string Slug, string Title, string? ChannelName, int LengthSeconds, string Length, DateTime CreatedAtUtc,
        DateTime? StreamDateUtc, string? ThumbnailUrl, string[] Qualities, long? VodId, string? VodStart, string? VodEnd, ClipAudioDto? Audio);

    public static class EntryEndpoints
    {
        private const int MaxTitleLength = 500;

        public static IEndpointRouteBuilder MapEntryEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/info", GetVodInfo);
            app.MapGet("/api/clips/info", GetClipInfo);

            var entries = app.MapGroup("/api/entries");
            entries.MapPost("/", AddEntry);
            entries.MapGet("/", ListEntries);
            entries.MapGet("/{id:int}", GetEntry);
            entries.MapPatch("/{id:int}", RenameEntry);
            entries.MapPut("/{id:int}/trim", RetrimEntry);
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

        private static async Task<IResult> GetClipInfo(string? clip, LibraryDbContext db, ClipInfoService clipInfo, VodInfoService vodInfo,
            AudioRestoreService restore, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            if (ClipInfoService.ParseClipSlug(clip) is not { } slug)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["clip"] = ["Unable to parse clip link/ID."] });

            var details = await clipInfo.GetClipAsync(slug);
            if (details is null)
                return ClipNotFound(slug);

            var vod = details.VodId is { } id ? await vodInfo.GetVodAsync(id) : null;
            var (start, end) = ClipRange(details);

            ClipAudioDto? audio = null;
            if (vod is not null)
            {
                try
                {
                    if (await restore.GetCoverageAsync(db, vod.VodId, start, end, cancellationToken) is { } coverage)
                        audio = new ClipAudioDto(coverage.MutedSeconds, coverage.RestorableSeconds);
                }
                catch (HttpRequestException ex)
                {
                    // The clip can still be added; the file job checks again
                    loggerFactory.CreateLogger("TwitchDownloaderWeb.Entries").LogWarning("Couldn't read the playlist of VOD {VodId}: {Message}", vod.VodId, ex.Message);
                }
            }

            return Results.Ok(new ClipInfoDto(slug, details.Title, details.ChannelName, details.DurationSeconds,
                TrimTime.Format(TimeSpan.FromSeconds(details.DurationSeconds)), details.CreatedAtUtc, vod?.StreamDateUtc, details.ThumbnailUrl,
                details.Qualities, details.VodId, details.VodId is null ? null : TrimTime.Format(start), details.VodId is null ? null : TrimTime.Format(end),
                audio));
        }

        /// <summary>The clip's place in its VOD, or 0..length when Twitch doesn't say.</summary>
        private static (TimeSpan Start, TimeSpan End) ClipRange(ClipDetails clip)
        {
            var start = TimeSpan.FromSeconds(clip.VodOffsetSeconds ?? 0);
            return (start, start + TimeSpan.FromSeconds(clip.DurationSeconds));
        }

        private static async Task<IResult> AddEntry(AddEntryRequest request, LibraryDbContext db, VodInfoService vodInfo, ClipInfoService clipInfo,
            DownloadQueue queue, IOptions<DownloadSettings> settings, CancellationToken cancellationToken)
        {
            if (VodInfoService.ParseVodId(request.VodId) is not { } vodId)
            {
                if (ClipInfoService.ParseClipSlug(request.VodId) is { } slug)
                    return await AddClipEntry(slug, request, db, vodInfo, clipInfo, cancellationToken);

                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vodId"] = ["Unable to parse VOD or clip ID/URL."] });
            }

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

        /// <summary>
        /// Adds a clips.twitch.tv clip. Nothing is downloaded: Twitch keeps the clip, and its file is made on demand
        /// (from the VOD with clean audio when muted parts can be restored, see <see cref="ClipFileService"/>).
        /// </summary>
        private static async Task<IResult> AddClipEntry(string slug, AddEntryRequest request, LibraryDbContext db, VodInfoService vodInfo,
            ClipInfoService clipInfo, CancellationToken cancellationToken)
        {
            var title = request.Title?.Trim();
            if (title is { Length: > MaxTitleLength })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"Title cannot be longer than {MaxTitleLength} characters."] });

            var clip = await clipInfo.GetClipAsync(slug);
            if (clip is null)
                return ClipNotFound(slug);

            string? quality = null;
            var requestedQuality = string.IsNullOrWhiteSpace(request.Quality) ? null : request.Quality.Trim();
            if (requestedQuality is not null)
            {
                quality = clip.Qualities.FirstOrDefault(x => string.Equals(x, requestedQuality, StringComparison.OrdinalIgnoreCase));
                if (quality is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["quality"] = [$"Quality \"{requestedQuality}\" is not available. Available: {string.Join(", ", clip.Qualities)}."]
                    });
                }
            }

            var vod = clip.VodId is { } id ? await vodInfo.GetVodAsync(id) : null;
            var (start, end) = ClipRange(clip);
            var now = DateTime.UtcNow;

            var entry = new LibraryEntry
            {
                Title = string.IsNullOrEmpty(title) ? clip.Title : title,
                Kind = EntryKind.Clip,
                ClipSlug = slug,
                VodId = clip.VodId ?? 0,
                ChannelName = clip.ChannelName,
                Quality = quality,
                TrimMode = VideoTrimMode.Exact,
                StartTime = start,
                EndTime = end,
                ClipLengthSeconds = clip.DurationSeconds,
                VodLengthSeconds = vod?.LengthSeconds ?? 0,
                // When the stream started; without the VOD, the clip's own date is the closest we have
                StreamDateUtc = vod?.StreamDateUtc ?? clip.CreatedAtUtc,
                AddedAtUtc = now,
                UpdatedAtUtc = now,
                ThumbnailUrl = clip.ThumbnailUrl,
                Status = EntryStatus.ReferenceOnly,
            };

            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                db.Entries.Add(entry);
                await db.SaveChangesAsync(cancellationToken);

                var source = clip.VodId is null ? "no VOD" : $"VOD {clip.VodId} at {TrimTime.Format(start)}";
                db.Audit(AuditActions.EntryAdded, entry.Id, $"Clip {slug} ({source}), {clip.DurationSeconds}s, reference-only");
                await db.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }

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

        /// <summary>How long a re-trim waits for the entry's running download to stop after cancelling it.</summary>
        private static readonly TimeSpan RetrimCancelWait = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Changes an entry's start/end. Any running download is cancelled and its file deleted; the stored-vs-reference rule is
        /// applied again to the new length, so a stored clip is downloaded again (or becomes reference-only if it got too long).
        /// </summary>
        private static async Task<IResult> RetrimEntry(int id, RetrimEntryRequest request, LibraryDbContext db, DownloadQueue queue,
            LibraryStorage storage, IOptions<DownloadSettings> settings, CancellationToken cancellationToken)
        {
            if (!TrimTime.TryParse(request.StartTime, out var parsedStart))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startTime"] = ["Use h:mm:ss, m:ss or seconds."] });

            if (!TrimTime.TryParse(request.EndTime, out var endTime))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endTime"] = ["Use h:mm:ss, m:ss or seconds."] });

            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            if (entry.Kind == EntryKind.Clip)
                return Results.Problem(detail: "Twitch clips keep Twitch's cut. Use the trim in Make vertical to shorten one.", statusCode: StatusCodes.Status409Conflict);

            if (entry.Status == EntryStatus.VodUnavailable)
                return Results.Problem(detail: "The VOD is no longer available on Twitch, so the clip can't be cut again.", statusCode: StatusCodes.Status410Gone);

            var startTime = parsedStart ?? TimeSpan.Zero;
            if (VodInfoService.ValidateTrim(startTime, endTime, entry.VodLengthSeconds) is { } trimErrors)
                return Results.ValidationProblem(trimErrors);

            if (startTime == entry.StartTime && endTime == entry.EndTime)
                return Results.Ok(EntryDto.From(entry));

            // A running job would keep writing the old cut to the same file. Stop it and wait until it has cleaned up.
            if (queue.IsRunning(id))
            {
                queue.Cancel(id);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (queue.IsRunning(id))
                {
                    if (waited.Elapsed > RetrimCancelWait)
                        return Results.Problem(detail: "The current download didn't stop in time. Try again in a moment.", statusCode: StatusCodes.Status409Conflict);
                    await Task.Delay(100, cancellationToken);
                }

                // The job may have saved a status while stopping
                await db.Entry(entry).ReloadAsync(cancellationToken);
            }

            var oldRange = $"{TrimTime.Format(entry.StartTime)}-{TrimTime.Format(entry.EndTime) ?? "end"}";
            var clipLength = (endTime ?? TimeSpan.FromSeconds(entry.VodLengthSeconds)) - startTime;
            var isStored = clipLength.TotalSeconds <= settings.Value.MaxStoredClipSeconds;

            storage.TryDelete(entry.FilePath);
            storage.TryDelete(entry.GeneratedFilePath);

            entry.StartTime = startTime;
            entry.EndTime = endTime;
            entry.ClipLengthSeconds = clipLength.TotalSeconds;
            entry.Status = isStored ? EntryStatus.Queued : EntryStatus.ReferenceOnly;
            entry.Error = null;
            entry.FilePath = null;
            entry.FileSizeBytes = null;
            entry.GenerationStatus = GenerationStatus.None;
            entry.GeneratedFilePath = null;
            entry.GeneratedFileSizeBytes = null;
            entry.GeneratedExpiresAtUtc = null;
            entry.AudioRestoredSeconds = null;
            entry.AudioMutedSeconds = null;
            entry.UpdatedAtUtc = DateTime.UtcNow;
            db.Audit(AuditActions.EntryRetrimmed, id,
                $"{oldRange} -> {TrimTime.Format(startTime)}-{TrimTime.Format(endTime) ?? "end"}, {clipLength.TotalSeconds:0.###}s, {(isStored ? "stored" : "reference-only")}");
            await db.SaveChangesAsync(cancellationToken);

            if (isStored)
                queue.Enqueue(id, DownloadJobKind.Store);

            return Results.Ok(EntryDto.From(entry));
        }

        private static async Task<IResult> DeleteEntry(int id, LibraryDbContext db, DownloadQueue queue, LibraryStorage storage, ShortformService shortform,
            CancellationToken cancellationToken)
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
            shortform.Delete(id);

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
                    return Results.Problem(detail: entry.Error ?? ReferenceVodCheckService.VodGoneMessage, statusCode: StatusCodes.Status410Gone);

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

        /// <param name="preview">True when the page plays the file (e.g. the vertical editor); not audited as a download.</param>
        private static async Task<IResult> DownloadEntryFile(int id, bool? preview, HttpRequest request, LibraryDbContext db, LibraryStorage storage,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken)
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
            if (preview != true && (string.IsNullOrEmpty(range) || range.StartsWith("bytes=0-", StringComparison.Ordinal)))
            {
                db.Audit(AuditActions.FileServed, id, kind);
                await db.SaveChangesAsync(cancellationToken);
            }

            return Results.File(fullPath, "video/mp4", GetDownloadFileName(entry), enableRangeProcessing: true);
        }

        internal static string GetDownloadFileName(LibraryEntry entry)
        {
            // Windows silently drops trailing dots and spaces, which could leave "" or ".." as the name
            var name = FilenameService.ReplaceInvalidFilenameChars(entry.Title)?.Trim().TrimEnd('.');
            return (string.IsNullOrEmpty(name) ? entry.ClipSlug ?? entry.VodId.ToString() : name) + ".mp4";
        }

        private static IResult EntryNotFound(int id) =>
            Results.Problem(detail: $"Entry {id} was not found.", statusCode: StatusCodes.Status404NotFound);

        private static IResult VodNotFound(long vodId) =>
            Results.Problem(detail: $"VOD {vodId} was not found. It may be deleted, expired or private.", statusCode: StatusCodes.Status404NotFound);

        private static IResult ClipNotFound(string slug) =>
            Results.Problem(detail: $"Clip {slug} was not found. It may be deleted.", statusCode: StatusCodes.Status404NotFound);

        private static IResult RestrictedVod() =>
            Results.Problem(detail: "This VOD is restricted (e.g. subscriber-only), which is not supported yet.", statusCode: StatusCodes.Status403Forbidden);
    }
}
