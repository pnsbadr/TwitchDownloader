using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record TimelineClipDto(int Id, string Title, string Kind, double Start, double? End);

    public sealed record MarkerDto(int Id, long VodId, double Seconds, double? EndSeconds, string Label, string Color, int CreatedByUserId, string? CreatedBy,
        DateTime CreatedAtUtc, bool CanDelete)
    {
        /// <summary>Set when saving the marker made or moved the VOD's live markers entry (see <see cref="LiveMarkerEntryService"/>).</summary>
        public LiveEntryChange? LiveEntry { get; init; }
    }

    public sealed record TimelineDto(IReadOnlyList<TimelineClipDto> Clips, IReadOnlyList<MarkerDto> Markers);

    public sealed record AddMarkerRequest(double? Seconds, double? EndSeconds, string? Label, string? Color);

    /// <summary>
    /// What the player's timeline shows besides Twitch's muted parts (which the page reads from the playlist itself): the team's
    /// library entries cut from the VOD, and the team's markers on it.
    /// </summary>
    public static class TimelineEndpoints
    {
        public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/{vodId:long}/timeline", GetTimeline).RequireTeam(TeamPermission.ViewLibrary);
            // Markers and segments of several VODs at once, for the library and the VOD browser cards
            app.MapGet("/api/markers", ListMarkers).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPost("/api/vods/{vodId:long}/markers", AddMarker).RequireTeam(TeamPermission.AddEntries);
            // Own markers need AddEntries (as when they were made); anyone's need DeleteAnyEntries. Checked in the handler.
            app.MapDelete("/api/markers/{id:int}", DeleteMarker).RequireTeam(TeamPermission.AddEntries | TeamPermission.DeleteAnyEntries);
            return app;
        }

        private static bool CanDelete(TeamContext team, VodMarker marker) =>
            team.CanOnOwnOrAny(marker.CreatedByUserId.ToString(), TeamPermission.AddEntries, TeamPermission.DeleteAnyEntries);

        private static async Task<IResult> GetTimeline(long vodId, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var clips = await db.Entries.AsNoTracking()
                .Where(x => x.VodId == vodId)
                .OrderBy(x => x.AddedAtUtc)
                .Select(x => new { x.Id, x.Title, x.Kind, x.StartTime, x.EndTime })
                .ToListAsync(cancellationToken);

            var markers = await db.Markers.AsNoTracking().Where(x => x.VodId == vodId).ToListAsync(cancellationToken);
            var names = await UserNamesAsync(db, markers.Select(x => x.CreatedByUserId), cancellationToken);

            return Results.Ok(new TimelineDto(
                clips.Select(x => new TimelineClipDto(x.Id, x.Title, x.Kind.ToString(), x.StartTime.TotalSeconds, x.EndTime?.TotalSeconds)).ToList(),
                markers.OrderBy(x => x.Seconds).Select(x => ToDto(x, names, team)).ToList()));
        }

        public const int MaxVodIdsPerRequest = 200;

        private static async Task<IResult> ListMarkers(string? vodIds, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var ids = new HashSet<long>();
            foreach (var part in (vodIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!long.TryParse(part, out var id) || id <= 0)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["vodIds"] = ["Give VOD IDs separated by commas."] });
                ids.Add(id);
            }

            if (ids.Count > MaxVodIdsPerRequest)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["vodIds"] = [$"Ask for at most {MaxVodIdsPerRequest} VODs at once."] });

            if (ids.Count == 0)
                return Results.Ok(Array.Empty<MarkerDto>());

            var markers = await db.Markers.AsNoTracking().Where(x => ids.Contains(x.VodId)).ToListAsync(cancellationToken);
            var names = await UserNamesAsync(db, markers.Select(x => x.CreatedByUserId), cancellationToken);
            return Results.Ok(markers.OrderBy(x => x.VodId).ThenBy(x => x.Seconds).Select(x => ToDto(x, names, team)).ToList());
        }

        private static async Task<IResult> AddMarker(long vodId, AddMarkerRequest request, TeamContext team, LibraryDbContext db, LiveMarkerEntryService liveEntries,
            CancellationToken cancellationToken)
        {
            var errors = new Dictionary<string, string[]>();
            if (request.Seconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0 || seconds > 7 * 24 * 3600)
                errors["seconds"] = ["Give the marker's time in seconds from the start of the VOD."];

            if (request.EndSeconds is { } end
                && (!double.IsFinite(end) || end > 7 * 24 * 3600 || request.Seconds is not { } start || end < start + MinSegmentSeconds))
                errors["endSeconds"] = [$"A segment must end at least {MinSegmentSeconds} s after it starts."];

            var label = request.Label?.Trim() ?? "";
            if (label.Length > VodMarker.MaxLabelLength)
                errors["label"] = [$"Keep the label to {VodMarker.MaxLabelLength} characters."];

            var color = string.IsNullOrWhiteSpace(request.Color) ? VodMarker.Colors[0] : request.Color.Trim().ToLowerInvariant();
            if (!VodMarker.Colors.Contains(color))
                errors["color"] = [$"Color must be one of {string.Join(", ", VodMarker.Colors)}."];

            if (vodId <= 0)
                errors["vodId"] = ["Unknown VOD."];

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var marker = new VodMarker
            {
                VodId = vodId,
                Seconds = Math.Round(request.Seconds!.Value, 2),
                EndSeconds = request.EndSeconds is { } endSeconds ? Math.Round(endSeconds, 2) : null,
                Label = label,
                Color = color,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Markers.Add(marker);
            db.Audit(AuditActions.MarkerAdded, null, $"VOD {vodId} {Describe(marker)}");
            await db.SaveChangesAsync(cancellationToken);

            // A marker on a live stream makes (or moves) the team's reference entry around its markers
            var liveEntry = await liveEntries.MarkersChangedAsync(db, vodId, cancellationToken);

            var names = await UserNamesAsync(db, [marker.CreatedByUserId], cancellationToken);
            return Results.Created($"/api/markers/{marker.Id}", ToDto(marker, names, team) with { LiveEntry = liveEntry });
        }

        private static async Task<IResult> DeleteMarker(int id, TeamContext team, LibraryDbContext db, LiveMarkerEntryService liveEntries,
            CancellationToken cancellationToken)
        {
            var marker = await db.Markers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (marker is null)
                return Results.Problem(detail: "That marker is gone.", statusCode: StatusCodes.Status404NotFound);

            if (!CanDelete(team, marker))
                return Results.Problem(detail: "Your role only lets you delete markers you added.", statusCode: StatusCodes.Status403Forbidden);

            db.Markers.Remove(marker);
            db.Audit(AuditActions.MarkerDeleted, null,
                $"VOD {marker.VodId} {Describe(marker)}");
            await db.SaveChangesAsync(cancellationToken);
            await liveEntries.MarkersChangedAsync(db, marker.VodId, cancellationToken);
            return Results.NoContent();
        }

        private static MarkerDto ToDto(VodMarker x, IReadOnlyDictionary<int, string> names, TeamContext team) =>
            new(x.Id, x.VodId, x.Seconds, x.EndSeconds, x.Label, x.Color, x.CreatedByUserId, names.GetValueOrDefault(x.CreatedByUserId), x.CreatedAtUtc,
                !team.IsSupport && CanDelete(team, x));

        private static async Task<Dictionary<int, string>> UserNamesAsync(LibraryDbContext db, IEnumerable<int> userIds, CancellationToken cancellationToken)
        {
            var ids = userIds.Distinct().ToList();
            return await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
        }

        public const double MinSegmentSeconds = 0.5;

        private static string Describe(VodMarker marker) =>
            (marker.EndSeconds is { } end ? $"from {FormatTime(marker.Seconds)} to {FormatTime(end)}" : $"at {FormatTime(marker.Seconds)}")
            + (marker.Label.Length > 0 ? $": \"{marker.Label}\"" : "");

        private static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }
}
