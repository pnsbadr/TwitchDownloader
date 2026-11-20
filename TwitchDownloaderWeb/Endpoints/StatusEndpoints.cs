using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="SetWhen">ByHand, NewItem, EditMade, VerticalEditMade or Exported.</param>
    /// <param name="Clips">The team's clips with this status now (hidden ones included).</param>
    public sealed record TeamStatusDto(int Id, string Name, string Color, int Position, string SetWhen, bool HideFromLists, int Clips, int Videos);

    public sealed record TeamTagDto(int Id, string Name, int Clips, int Videos);

    /// <param name="CanManage">The caller may change statuses and delete tags (<see cref="TeamPermission.ManageStatuses"/>).</param>
    /// <param name="CanSet">The caller may set statuses and tags on clips and videos (<see cref="TeamPermission.AddEntries"/>).</param>
    public sealed record TeamStatusesDto(IReadOnlyList<TeamStatusDto> Statuses, IReadOnlyList<TeamTagDto> Tags, int NoStatusClips, int NoStatusVideos,
        bool CanManage, bool CanSet);

    public sealed record SaveStatusRequest(string? Name, string? Color, string? SetWhen, bool? HideFromLists);

    public sealed record OrderStatusesRequest(List<int>? Ids);

    /// <param name="StatusId">The team's status, or null for none.</param>
    public sealed record SetItemStatusRequest(int? StatusId);

    public sealed record SetItemTagsRequest(List<string>? Tags);

    /// <summary>
    /// The team's own statuses and tags for clips and videos (badrfly 2026-11-20, "Redesign the library for large teams", step 4). The team
    /// owner or a role with <see cref="TeamPermission.ManageStatuses"/> shapes them on the Team page; any member who makes clips
    /// (<see cref="TeamPermission.AddEntries"/>) sets them on any clip or video of the team, since statuses follow work between members
    /// (a clipper's clip is edited and posted by someone else).
    /// </summary>
    public static class StatusEndpoints
    {
        public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/team/statuses", List).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPost("/api/team/statuses", Create).RequireTeam(TeamPermission.ManageStatuses);
            app.MapPut("/api/team/statuses/order", Order).RequireTeam(TeamPermission.ManageStatuses);
            app.MapPut("/api/team/statuses/{id:int}", Update).RequireTeam(TeamPermission.ManageStatuses);
            app.MapDelete("/api/team/statuses/{id:int}", Delete).RequireTeam(TeamPermission.ManageStatuses);
            app.MapDelete("/api/team/tags/{id:int}", DeleteTag).RequireTeam(TeamPermission.ManageStatuses);
            app.MapPut("/api/entries/{id:int}/status", SetEntryStatus).RequireTeam(TeamPermission.AddEntries);
            app.MapPut("/api/videos/{id:int}/status", SetVideoStatus).RequireTeam(TeamPermission.AddEntries);
            app.MapPut("/api/entries/{id:int}/tags", SetEntryTags).RequireTeam(TeamPermission.AddEntries);
            app.MapPut("/api/videos/{id:int}/tags", SetVideoTags).RequireTeam(TeamPermission.AddEntries);
            return app;
        }

        private static IResult Problem(string field, string message) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

        private static IResult StatusNotFound() => Results.Problem(detail: "That status isn't one of your team's.", statusCode: StatusCodes.Status404NotFound);

        private static async Task<IResult> List(TeamContext team, LibraryDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await DtoAsync(db, team, cancellationToken));

        private static async Task<TeamStatusesDto> DtoAsync(LibraryDbContext db, TeamContext team, CancellationToken cancellationToken)
        {
            var statuses = await ItemStatuses.ListAsync(db, team.TeamId, cancellationToken);
            if (db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(cancellationToken);

            var clipCounts = await db.Entries.AsNoTracking().GroupBy(x => x.TeamStatusId).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key ?? 0, x => x.Count, cancellationToken);
            var videoCounts = await db.Videos.AsNoTracking().GroupBy(x => x.TeamStatusId).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key ?? 0, x => x.Count, cancellationToken);
            var tags = await db.TeamTags.AsNoTracking().OrderBy(x => x.Name)
                .Select(x => new TeamTagDto(x.Id, x.Name, db.EntryTags.Count(t => t.TagId == x.Id), db.VideoTags.Count(t => t.TagId == x.Id)))
                .ToListAsync(cancellationToken);
            return new TeamStatusesDto(
                statuses.Select((x, i) => new TeamStatusDto(x.Id, x.Name, x.Color, i, x.SetWhen.ToString(), x.HideFromLists,
                    clipCounts.GetValueOrDefault(x.Id), videoCounts.GetValueOrDefault(x.Id))).ToList(),
                tags, clipCounts.GetValueOrDefault(0), videoCounts.GetValueOrDefault(0),
                team.Can(TeamPermission.ManageStatuses) && !team.IsSupport, team.Can(TeamPermission.AddEntries) && !team.IsSupport);
        }

        /// <summary>Checks a request's fields onto <paramref name="status"/> (new or existing). Null fields keep what it has.</summary>
        private static IResult? Apply(SaveStatusRequest request, TeamStatus status, IReadOnlyList<TeamStatus> others)
        {
            if (request.Name is not null)
            {
                var name = request.Name.Trim();
                if (name.Length == 0)
                    return Problem("name", "Give the status a name.");
                if (name.Length > TeamStatus.MaxNameLength)
                    return Problem("name", $"Keep the name to {TeamStatus.MaxNameLength} characters.");
                if (others.Any(x => x != status && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return Problem("name", "Your team already has a status with that name.");
                status.Name = name;
            }
            if (request.Color is not null)
            {
                if (!VodMarker.Colors.Contains(request.Color))
                    return Problem("color", $"Use one of: {string.Join(", ", VodMarker.Colors)}.");
                status.Color = request.Color;
            }
            if (request.SetWhen is not null)
            {
                if (!Enum.TryParse<StatusTrigger>(request.SetWhen, true, out var trigger) || !Enum.GetNames<StatusTrigger>().Contains(request.SetWhen, StringComparer.OrdinalIgnoreCase))
                    return Problem("setWhen", $"Use one of: {string.Join(", ", Enum.GetNames<StatusTrigger>())}.");
                // One status per trigger: the one that had it is set by hand from now on
                if (trigger != StatusTrigger.ByHand)
                    foreach (var other in others.Where(x => x != status && x.SetWhen == trigger))
                        other.SetWhen = StatusTrigger.ByHand;
                status.SetWhen = trigger;
            }
            if (request.HideFromLists is { } hide)
                status.HideFromLists = hide;
            return null;
        }

        private static async Task<IResult> Create(SaveStatusRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var statuses = await ItemStatuses.ListAsync(db, team.TeamId, cancellationToken);
            if (statuses.Count >= TeamStatus.MaxPerTeam)
                return Problem("name", $"A team has at most {TeamStatus.MaxPerTeam} statuses.");
            if (string.IsNullOrWhiteSpace(request.Name))
                return Problem("name", "Give the status a name.");
            var status = new TeamStatus
            {
                TeamId = team.TeamId, Name = "", Position = statuses.Count == 0 ? 0 : statuses.Max(x => x.Position) + 1, CreatedAtUtc = DateTime.UtcNow,
            };
            if (Apply(request, status, statuses) is { } problem)
                return problem;
            db.TeamStatuses.Add(status);
            db.Audit(AuditActions.StatusesChanged, null, $"Added status \"{status.Name}\"");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await DtoAsync(db, team, cancellationToken));
        }

        private static async Task<IResult> Update(int id, SaveStatusRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var statuses = await ItemStatuses.ListAsync(db, team.TeamId, cancellationToken);
            if (statuses.FirstOrDefault(x => x.Id == id) is not { } status)
                return StatusNotFound();
            var before = status.Name;
            if (Apply(request, status, statuses) is { } problem)
                return problem;
            db.Audit(AuditActions.StatusesChanged, null, before == status.Name ? $"Changed status \"{status.Name}\"" : $"Renamed status \"{before}\" to \"{status.Name}\"");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await DtoAsync(db, team, cancellationToken));
        }

        private static async Task<IResult> Order(OrderStatusesRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var statuses = await ItemStatuses.ListAsync(db, team.TeamId, cancellationToken);
            var ids = request.Ids ?? [];
            if (ids.Count != statuses.Count || ids.Distinct().Count() != ids.Count || ids.Any(x => statuses.All(s => s.Id != x)))
                return Problem("ids", "List every one of your team's statuses once, in the new order.");
            for (var i = 0; i < ids.Count; i++)
                statuses.First(x => x.Id == ids[i]).Position = i;
            db.Audit(AuditActions.StatusesChanged, null, "Reordered statuses");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await DtoAsync(db, team, cancellationToken));
        }

        /// <param name="moveTo">Another status their clips and videos get; none = they have no status.</param>
        private static async Task<IResult> Delete(int id, int? moveTo, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var statuses = await ItemStatuses.ListAsync(db, team.TeamId, cancellationToken);
            if (statuses.FirstOrDefault(x => x.Id == id) is not { } status)
                return StatusNotFound();
            TeamStatus? target = null;
            if (moveTo is { } targetId && (target = statuses.FirstOrDefault(x => x.Id == targetId && x.Id != id)) is null)
                return Problem("moveTo", "Move them to another of your team's statuses.");

            var newId = target?.Id;
            await db.Entries.Where(x => x.TeamStatusId == id).ExecuteUpdateAsync(x => x.SetProperty(e => e.TeamStatusId, newId), cancellationToken);
            await db.Videos.Where(x => x.TeamStatusId == id).ExecuteUpdateAsync(x => x.SetProperty(v => v.TeamStatusId, newId), cancellationToken);
            db.TeamStatuses.Remove(status);
            db.Audit(AuditActions.StatusesChanged, null, target is null ? $"Deleted status \"{status.Name}\"" : $"Deleted status \"{status.Name}\", its items moved to \"{target.Name}\"");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await DtoAsync(db, team, cancellationToken));
        }

        private static async Task<IResult> DeleteTag(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (await db.TeamTags.FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } tag)
                return Results.Problem(detail: "Tag not found.", statusCode: StatusCodes.Status404NotFound);
            db.TeamTags.Remove(tag);
            db.Audit(AuditActions.StatusesChanged, null, $"Deleted tag #{tag.Name}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await DtoAsync(db, team, cancellationToken));
        }

        /// <summary>The status a request names (null = none), or a problem when it isn't the team's.</summary>
        private static async Task<(TeamStatus? Status, IResult? Problem)> RequestedStatusAsync(SetItemStatusRequest request, LibraryDbContext db,
            TeamContext team, CancellationToken cancellationToken)
        {
            if (request.StatusId is not { } statusId)
                return (null, null);
            var statuses = await ItemStatuses.ListAsync(db, team.TeamId, cancellationToken);
            return statuses.FirstOrDefault(x => x.Id == statusId) is { } status ? (status, null) : (null, StatusNotFound());
        }

        private static async Task<IResult> SetEntryStatus(int id, SetItemStatusRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (await db.Entries.FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } entry)
                return Results.Problem(detail: "Entry not found.", statusCode: StatusCodes.Status404NotFound);
            var (status, problem) = await RequestedStatusAsync(request, db, team, cancellationToken);
            if (problem is not null)
                return problem;
            if (entry.TeamStatusId != status?.Id)
            {
                entry.TeamStatus = status;
                entry.TeamStatusId = status?.Id;
                db.Audit(AuditActions.ItemStatusChanged, entry.Id, status is null ? "No status" : $"Status \"{status.Name}\"");
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(new { id = entry.Id, teamStatusId = entry.TeamStatusId });
        }

        private static async Task<IResult> SetVideoStatus(int id, SetItemStatusRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (await db.Videos.FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } video)
                return Results.Problem(detail: "Video not found.", statusCode: StatusCodes.Status404NotFound);
            var (status, problem) = await RequestedStatusAsync(request, db, team, cancellationToken);
            if (problem is not null)
                return problem;
            if (video.TeamStatusId != status?.Id)
            {
                video.TeamStatus = status;
                video.TeamStatusId = status?.Id;
                db.Audit(AuditActions.ItemStatusChanged, null, $"Video \"{video.Title}\": " + (status is null ? "no status" : $"status \"{status.Name}\""));
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(new { id = video.Id, teamStatusId = video.TeamStatusId });
        }

        /// <summary>Normalized, distinct tag names of a request, or a problem.</summary>
        private static (List<string> Names, IResult? Problem) TagNames(SetItemTagsRequest request)
        {
            var names = new List<string>();
            foreach (var text in request.Tags ?? [])
            {
                if (ItemStatuses.NormalizeTag(text) is not { } name)
                    return ([], Problem("tags", $"Tags are 1 to {TeamTag.MaxNameLength} letters, digits, '-' or '_'."));
                if (!names.Contains(name))
                    names.Add(name);
            }
            return names.Count > TeamTag.MaxPerItem ? ([], Problem("tags", $"At most {TeamTag.MaxPerItem} tags each.")) : (names, null);
        }

        private static async Task<IResult> SetEntryTags(int id, SetItemTagsRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (!await db.Entries.AnyAsync(x => x.Id == id, cancellationToken))
                return Results.Problem(detail: "Entry not found.", statusCode: StatusCodes.Status404NotFound);
            var (names, problem) = TagNames(request);
            if (problem is not null)
                return problem;
            var tags = await ItemStatuses.TagsNamedAsync(db, team.TeamId, names, cancellationToken);
            var current = await db.EntryTags.Include(x => x.Tag).Where(x => x.EntryId == id).ToListAsync(cancellationToken);
            if (current.Select(x => x.Tag!.Name).Order().SequenceEqual(names.Order()))
                return Results.Ok(new { id, tags = names.Order().ToList() });
            db.EntryTags.RemoveRange(current.Where(x => !names.Contains(x.Tag!.Name)));
            foreach (var tag in tags.Where(t => current.All(c => c.Tag!.Name != t.Name)))
                db.EntryTags.Add(new EntryTag { EntryId = id, Tag = tag });
            db.Audit(AuditActions.ItemTagsChanged, id, names.Count == 0 ? "No tags" : "Tags " + string.Join(" ", names.Select(x => "#" + x)));
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { id, tags = names.Order().ToList() });
        }

        private static async Task<IResult> SetVideoTags(int id, SetItemTagsRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (await db.Videos.AsNoTracking().Where(x => x.Id == id).Select(x => x.Title).FirstOrDefaultAsync(cancellationToken) is not { } title)
                return Results.Problem(detail: "Video not found.", statusCode: StatusCodes.Status404NotFound);
            var (names, problem) = TagNames(request);
            if (problem is not null)
                return problem;
            var tags = await ItemStatuses.TagsNamedAsync(db, team.TeamId, names, cancellationToken);
            var current = await db.VideoTags.Include(x => x.Tag).Where(x => x.VideoId == id).ToListAsync(cancellationToken);
            if (current.Select(x => x.Tag!.Name).Order().SequenceEqual(names.Order()))
                return Results.Ok(new { id, tags = names.Order().ToList() });
            db.VideoTags.RemoveRange(current.Where(x => !names.Contains(x.Tag!.Name)));
            foreach (var tag in tags.Where(t => current.All(c => c.Tag!.Name != t.Name)))
                db.VideoTags.Add(new VideoTag { VideoId = id, Tag = tag });
            db.Audit(AuditActions.ItemTagsChanged, null, $"Video \"{title}\": " + (names.Count == 0 ? "no tags" : "tags " + string.Join(" ", names.Select(x => "#" + x))));
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { id, tags = names.Order().ToList() });
        }

        /// <summary>
        /// The list filters shared by clips and videos: <paramref name="status"/> = a status ID or "none"; without it, items whose status is hidden
        /// from lists are left out. <paramref name="tag"/> = a tag name (with or without '#'). Null problem = fine.
        /// </summary>
        internal static (int? StatusId, bool NoStatus, string? Tag, IResult? Problem) ParseFilters(string? status, string? tag)
        {
            int? statusId = null;
            var none = false;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (string.Equals(status.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                    none = true;
                else if (int.TryParse(status, out var parsed))
                    statusId = parsed;
                else
                    return (null, false, null, Problem("status", "Use a status ID or \"none\"."));
            }
            string? tagName = null;
            if (!string.IsNullOrWhiteSpace(tag) && (tagName = ItemStatuses.NormalizeTag(tag)) is null)
                return (null, false, null, Problem("tag", "Not a tag."));
            return (statusId, none, tagName, null);
        }
    }
}
