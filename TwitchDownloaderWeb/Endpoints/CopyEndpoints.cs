using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="RowId">Where the copy goes: one of the caller's rows of the right kind on the same VOD. Blank = their first one (made if needed).</param>
    public sealed record CopyRequest(int? RowId);

    /// <summary>
    /// Copies of someone's segment, moment or clip onto the caller's own row (badrfly, 2026-11-15, frontend rework). Members can't change
    /// another member's items unless their role has "edit anyone's entries"; they make a copy instead, and members who may edit the original
    /// pick "Edit original" or "Make a copy" on the page. A copy remembers what it was copied from. Alt+drag of a clip onto a video row makes
    /// a segment copy at exactly the clip's times. Copies need AddEntries (they are new items of the caller's).
    /// </summary>
    public static class CopyEndpoints
    {
        public static IEndpointRouteBuilder MapCopyEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/markers/{id:int}/copy", CopyMarker).RequireTeam(TeamPermission.AddEntries);
            app.MapPost("/api/entries/{id:int}/copy", CopyEntry).RequireTeam(TeamPermission.AddEntries);
            app.MapPost("/api/entries/{id:int}/copy-as-segment", CopyEntryAsSegment).RequireTeam(TeamPermission.AddEntries);
            return app;
        }

        /// <summary>A copy of a segment or moment on one of the caller's video rows on the same VOD.</summary>
        private static async Task<IResult> CopyMarker(int id, CopyRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            LiveMarkerEntryService liveEntries, CancellationToken cancellationToken, bool makeLiveEntry = true)
        {
            var source = await db.Markers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (source is null)
                return Results.Problem(detail: "That marker is gone.", statusCode: StatusCodes.Status404NotFound);

            if (await CheckTargetAsync(db, access, team, source.VodId, request.RowId, TimelineRowKind.Video, cancellationToken) is { } problem)
                return problem;

            var copy = new VodMarker
            {
                VodId = source.VodId,
                Seconds = source.Seconds,
                EndSeconds = source.EndSeconds,
                Label = source.Label,
                Color = source.Color,
                RowId = request.RowId,
                CopiedFromMarkerId = source.Id,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Markers.Add(copy);
            db.Audit(AuditActions.MarkerAdded, null, $"VOD {copy.VodId} {TimelineEndpoints.Describe(copy)} (copy of marker {source.Id})");
            await db.SaveChangesAsync(cancellationToken);
            var liveEntry = await liveEntries.MarkersChangedAsync(db, copy.VodId, cancellationToken, makeLiveEntry);

            var names = await TimelineEndpoints.UserNamesAsync(db, [copy.CreatedByUserId], cancellationToken);
            return Results.Created($"/api/markers/{copy.Id}", TimelineEndpoints.ToDto(copy, names, team) with { LiveEntry = liveEntry });
        }

        /// <summary>
        /// A copy of a VOD clip on one of the caller's clip rows. A stored clip's file is copied (no new download); one still downloading
        /// or failed is downloaded again. Twitch clip links (added before they were turned off) aren't copied.
        /// </summary>
        private static async Task<IResult> CopyEntry(int id, CopyRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            LibraryStorage storage, DownloadQueue queue, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var source = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (source is null)
                return Results.Problem(detail: $"Entry {id} was not found.", statusCode: StatusCodes.Status404NotFound);
            if (source.Kind != EntryKind.Vod)
                return Results.Problem(detail: "Twitch clips can't be copied.", statusCode: StatusCodes.Status409Conflict);
            if (source.Status == EntryStatus.VodUnavailable)
                return Results.Problem(detail: "The VOD is gone from Twitch, so this clip can't be copied.", statusCode: StatusCodes.Status410Gone);

            if (await CheckTargetAsync(db, access, team, source.VodId, request.RowId, TimelineRowKind.Clip, cancellationToken) is { } problem)
                return problem;

            var now = DateTime.UtcNow;
            var sourceFile = source is { Status: EntryStatus.Ready, FilePath: { } path } ? Path.Combine(storage.RootFolder, path) : null;
            var copy = new LibraryEntry
            {
                Title = source.Title,
                VodId = source.VodId,
                ChannelName = source.ChannelName,
                Quality = source.Quality,
                TrimMode = source.TrimMode,
                StartTime = source.StartTime,
                EndTime = source.EndTime,
                ClipLengthSeconds = source.ClipLengthSeconds,
                VodLengthSeconds = source.VodLengthSeconds,
                StreamDateUtc = source.StreamDateUtc,
                ThumbnailUrl = source.ThumbnailUrl,
                AddedAtUtc = now,
                UpdatedAtUtc = now,
                Status = source.Status == EntryStatus.ReferenceOnly ? EntryStatus.ReferenceOnly : EntryStatus.Queued,
                RowId = request.RowId,
                CopiedFromEntryId = source.Id,
            };

            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                db.Entries.Add(copy);
                await db.SaveChangesAsync(cancellationToken);

                if (sourceFile is not null && File.Exists(sourceFile))
                {
                    try
                    {
                        var relative = LibraryStorage.StoredRelativePath(copy.Id);
                        var target = Path.Combine(storage.RootFolder, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(sourceFile, target, overwrite: true);
                        copy.FilePath = relative;
                        copy.FileSizeBytes = new FileInfo(target).Length;
                        copy.AudioRestoredSeconds = source.AudioRestoredSeconds;
                        copy.AudioMutedSeconds = source.AudioMutedSeconds;
                        copy.Status = EntryStatus.Ready;
                    }
                    catch (IOException ex)
                    {
                        // Downloads it again instead
                        loggerFactory.CreateLogger("TwitchDownloaderWeb.Copies").LogWarning("Couldn't copy the file of entry {EntryId}: {Message}", source.Id, ex.Message);
                    }
                }

                db.Audit(AuditActions.EntryAdded, copy.Id, $"Copy of entry {source.Id}, VOD {copy.VodId}, "
                    + $"{TrimTime.Format(copy.StartTime)}-{TrimTime.Format(copy.EndTime) ?? "end"}, {(copy.Status == EntryStatus.ReferenceOnly ? "reference-only" : "stored")}");
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            if (copy.Status == EntryStatus.Queued)
                queue.Enqueue(copy.Id, DownloadJobKind.Store);

            var names = await EntryEndpoints.UsernamesAsync(db, [copy.AddedByUserId], cancellationToken);
            return Results.Created($"/api/entries/{copy.Id}", EntryDto.From(copy) with { AddedBy = copy.AddedByUserId is { } u ? names.GetValueOrDefault(u) : null });
        }

        /// <summary>A segment at exactly the clip's times on one of the caller's video rows (Alt+drag of a clip onto a video row).</summary>
        private static async Task<IResult> CopyEntryAsSegment(int id, CopyRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            LiveMarkerEntryService liveEntries, CancellationToken cancellationToken, bool makeLiveEntry = true)
        {
            var source = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (source is null)
                return Results.Problem(detail: $"Entry {id} was not found.", statusCode: StatusCodes.Status404NotFound);
            if (source.VodId <= 0)
                return Results.Problem(detail: "Twitch didn't say which VOD this clip is from.", statusCode: StatusCodes.Status409Conflict);

            var end = source.EndTime?.TotalSeconds ?? source.VodLengthSeconds;
            if (end < source.StartTime.TotalSeconds + TimelineEndpoints.MinSegmentSeconds)
                return Results.Problem(detail: "This clip is too short to be a segment.", statusCode: StatusCodes.Status409Conflict);

            if (await CheckTargetAsync(db, access, team, source.VodId, request.RowId, TimelineRowKind.Video, cancellationToken) is { } problem)
                return problem;

            var label = source.Title.Trim();
            var marker = new VodMarker
            {
                VodId = source.VodId,
                Seconds = Math.Round(source.StartTime.TotalSeconds, 2),
                EndSeconds = Math.Round(end, 2),
                Label = label.Length > VodMarker.MaxLabelLength ? label[..VodMarker.MaxLabelLength] : label,
                Color = VodMarker.Colors[0],
                RowId = request.RowId,
                CopiedFromEntryId = source.Id,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Markers.Add(marker);
            db.Audit(AuditActions.MarkerAdded, null, $"VOD {marker.VodId} {TimelineEndpoints.Describe(marker)} (from clip {source.Id})");
            await db.SaveChangesAsync(cancellationToken);
            var liveEntry = await liveEntries.MarkersChangedAsync(db, marker.VodId, cancellationToken, makeLiveEntry);

            var names = await TimelineEndpoints.UserNamesAsync(db, [marker.CreatedByUserId], cancellationToken);
            return Results.Created($"/api/markers/{marker.Id}", TimelineEndpoints.ToDto(marker, names, team) with { LiveEntry = liveEntry });
        }

        /// <summary>The VOD must still be open to the team and the row (if given) one of the caller's of that kind on it.</summary>
        private static async Task<IResult?> CheckTargetAsync(LibraryDbContext db, TeamChannelAccess access, TeamContext team, long vodId, int? rowId,
            TimelineRowKind kind, CancellationToken cancellationToken)
        {
            if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vodId, cancellationToken), vodId) is { } refused)
                return refused;
            return rowId is { } id ? await TimelineEndpoints.CheckRowAsync(db, id, vodId, kind, team.UserId, cancellationToken) : null;
        }
    }
}
