using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record TimelineClipDto(int Id, string Title, string Kind, double Start, double? End);

    public sealed record MarkerDto(int Id, long VodId, double Seconds, string Label, string Color, int CreatedByUserId, string? CreatedBy,
        DateTime CreatedAtUtc, bool CanDelete);

    public sealed record TimelineDto(IReadOnlyList<TimelineClipDto> Clips, IReadOnlyList<MarkerDto> Markers);

    public sealed record AddMarkerRequest(double? Seconds, string? Label, string? Color);

    /// <summary>
    /// What the player's timeline shows besides Twitch's muted parts (which the page reads from the playlist itself): the team's
    /// library entries cut from the VOD, and the team's markers on it.
    /// </summary>
    public static class TimelineEndpoints
    {
        public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/{vodId:long}/timeline", GetTimeline).RequireTeam(TeamPermission.ViewLibrary);
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

        private static async Task<IResult> AddMarker(long vodId, AddMarkerRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var errors = new Dictionary<string, string[]>();
            if (request.Seconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0 || seconds > 7 * 24 * 3600)
                errors["seconds"] = ["Give the marker's time in seconds from the start of the VOD."];

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
                Label = label,
                Color = color,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Markers.Add(marker);
            db.Audit(AuditActions.MarkerAdded, null, $"VOD {vodId} at {FormatTime(marker.Seconds)}{(label.Length > 0 ? $": \"{label}\"" : "")}");
            await db.SaveChangesAsync(cancellationToken);

            var names = await UserNamesAsync(db, [marker.CreatedByUserId], cancellationToken);
            return Results.Created($"/api/markers/{marker.Id}", ToDto(marker, names, team));
        }

        private static async Task<IResult> DeleteMarker(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var marker = await db.Markers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (marker is null)
                return Results.Problem(detail: "That marker is gone.", statusCode: StatusCodes.Status404NotFound);

            if (!CanDelete(team, marker))
                return Results.Problem(detail: "Your role only lets you delete markers you added.", statusCode: StatusCodes.Status403Forbidden);

            db.Markers.Remove(marker);
            db.Audit(AuditActions.MarkerDeleted, null,
                $"VOD {marker.VodId} at {FormatTime(marker.Seconds)}{(marker.Label.Length > 0 ? $": \"{marker.Label}\"" : "")}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        private static MarkerDto ToDto(VodMarker x, IReadOnlyDictionary<int, string> names, TeamContext team) =>
            new(x.Id, x.VodId, x.Seconds, x.Label, x.Color, x.CreatedByUserId, names.GetValueOrDefault(x.CreatedByUserId), x.CreatedAtUtc,
                !team.IsSupport && CanDelete(team, x));

        private static async Task<Dictionary<int, string>> UserNamesAsync(LibraryDbContext db, IEnumerable<int> userIds, CancellationToken cancellationToken)
        {
            var ids = userIds.Distinct().ToList();
            return await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
        }

        private static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }
}
