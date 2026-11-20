using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services.Projects;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="Content">The whole timeline (null keeps it on PUT; an empty project on POST).</param>
    /// <param name="Revision">PUT: the revision the page last saw; a newer one on the server → 409 <c>project-changed</c> with the project.</param>
    /// <param name="EntryId">POST: the clip this is an edit of (its VOD is the edit's VOD).</param>
    /// <param name="VideoId">POST: the video this is an edit of.</param>
    /// <param name="VodId">POST: the VOD, for an edit of neither (else read from the clip/video, or from the content's first piece).</param>
    /// <param name="Vertical">POST: a vertical (9:16) edit; false or left out = a horizontal one, whose layouts are dropped. Fixed once made.</param>
    public sealed record ProjectRequest(string? Title, ProjectContent? Content = null, int? Revision = null,
        long? VodId = null, int? EntryId = null, int? VideoId = null, bool? Vertical = null);

    /// <param name="Content">Only from <c>GET /api/projects/{id}</c>, POST, PUT and copy (not in lists).</param>
    /// <param name="VodId">The VOD it belongs to (0 = none yet); <paramref name="EntryId"/> / <paramref name="VideoId"/> = the clip or video
    /// it's an edit of. <paramref name="VodIds"/> = every VOD its pieces use (more than one only for projects made before 2026-11-17).</param>
    public sealed record ProjectDto(int Id, int UserId, string? CreatedBy, string Title, int Revision, double LengthSeconds, int PieceCount,
        IReadOnlyList<long> VodIds, int? CopiedFromProjectId, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool CanEdit, bool CanDelete,
        ProjectContent? Content, long VodId = 0, int? EntryId = null, int? VideoId = null)
    {
        /// <summary>May make their own copy of it (own: AddEntries; a teammate's: also see it and EditAnyEntries).</summary>
        public bool CanCopy { get; init; }

        /// <summary>May put it on the export list (own, or a teammate's with DownloadTeamVerticals).</summary>
        public bool CanDownload { get; init; }

        /// <summary>A vertical (9:16) edit; false = a horizontal (Player 2) one.</summary>
        public bool Vertical { get; init; }
    }

    /// <summary>
    /// Projects: the multi-track Player 2 timeline (see <see cref="EditProject"/>). The page saves the whole content as it works; only
    /// adding and deleting are audited.
    /// </summary>
    public static class ProjectEndpoints
    {
        public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/projects", List).RequireTeam(TeamPermission.ViewLibrary);
            app.MapGet("/api/projects/{id:int}", Get).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPost("/api/projects", Create).RequireTeam(TeamPermission.AddEntries);
            // Own projects need AddEntries, anyone's EditAnyEntries / DeleteAnyEntries (checked in the handler)
            app.MapPut("/api/projects/{id:int}", Update).RequireTeam(TeamPermission.AddEntries | TeamPermission.EditAnyEntries);
            app.MapDelete("/api/projects/{id:int}", Delete).RequireTeam(TeamPermission.AddEntries | TeamPermission.DeleteAnyEntries);
            app.MapPost("/api/projects/{id:int}/copy", Copy).RequireTeam(TeamPermission.AddEntries);
            return app;
        }

        // A teammate's VERTICAL edits are seen, changed, copied, deleted and exported only as VerticalAccess allows (badrfly 2026-11-17);
        // one the caller may not see answers 404 like a missing one. HORIZONTAL edits are open to the whole team (badrfly 2026-11-17: anyone
        // can open a clip someone cut and make their own vertical edit from it): seen by everyone, copied with AddEntries, exported by anyone
        // who may make files, changed with EditAnyEntries and deleted with DeleteAnyEntries.
        internal static bool CanSee(TeamContext team, EditProject project) => !project.Vertical || VerticalAccess.CanSee(team, project.UserId);

        internal static bool CanEdit(TeamContext team, EditProject project) =>
            VerticalAccess.IsOwn(team, project.UserId) ? team.Can(TeamPermission.AddEntries)
            : project.Vertical ? VerticalAccess.CanReEditTeam(team) : !team.IsSupport && team.Can(TeamPermission.EditAnyEntries);

        private static bool CanDelete(TeamContext team, EditProject project) =>
            VerticalAccess.IsOwn(team, project.UserId) ? team.Can(TeamPermission.AddEntries)
            : project.Vertical ? VerticalAccess.CanDeleteTeam(team) : !team.IsSupport && team.Can(TeamPermission.DeleteAnyEntries);

        private static bool CanCopy(TeamContext team, EditProject project) =>
            !team.IsSupport && team.Can(TeamPermission.AddEntries)
            && (!project.Vertical || VerticalAccess.IsOwn(team, project.UserId) || VerticalAccess.CanReEditTeam(team));

        internal static bool CanDownload(TeamContext team, EditProject project) => !project.Vertical || VerticalAccess.CanDownload(team, project.UserId);

        private static async Task<List<ProjectDto>> ToDtosAsync(LibraryDbContext db, IReadOnlyList<EditProject> projects, TeamContext team,
            bool withContent, CancellationToken cancellationToken)
        {
            var names = await TimelineEndpoints.UserNamesAsync(db, projects.Select(x => x.UserId), cancellationToken);
            return projects.Select(x => new ProjectDto(x.Id, x.UserId, names.GetValueOrDefault(x.UserId), x.Title, x.Revision,
                Math.Round(x.LengthSeconds, 2), x.PieceCount, x.Vods.Select(v => v.VodId).OrderBy(v => v).ToList(), x.CopiedFromProjectId,
                x.CreatedAtUtc, x.UpdatedAtUtc, CanEdit(team, x), CanDelete(team, x), withContent ? ProjectContent.Read(x.Content) : null,
                x.VodId, x.EntryId, x.VideoId)
            {
                CanCopy = CanCopy(team, x),
                CanDownload = CanDownload(team, x),
                Vertical = x.Vertical,
            }).ToList();
        }

        private static async Task<ProjectDto> ToDtoAsync(LibraryDbContext db, EditProject project, TeamContext team, CancellationToken cancellationToken) =>
            (await ToDtosAsync(db, [project], team, true, cancellationToken))[0];

        /// <param name="userId">Only this member's.</param>
        /// <param name="vodId">Only projects of this VOD (or using it).</param>
        /// <param name="vodIds">Only projects of these VODs (comma separated, up to 200).</param>
        /// <param name="entryId">Only edits of this clip.</param>
        /// <param name="videoId">Only edits of this video.</param>
        /// <param name="vertical">Only vertical (true) or horizontal (false) edits.</param>
        private static async Task<IResult> List(int? userId, long? vodId, string? vodIds, int? entryId, int? videoId, bool? vertical, int? limit, TeamContext team,
            LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (ids, problem) = TimelineEndpoints.ParseVodIds(vodIds);
            if (problem is not null)
                return problem;

            var query = db.Projects.Include(x => x.Vods).AsNoTracking();
            if (userId is { } user)
                query = query.Where(x => x.UserId == user);
            if (vodId is { } vod)
                query = query.Where(x => x.VodId == vod || x.Vods.Any(v => v.VodId == vod));
            if (ids.Count > 0)
                query = query.Where(x => ids.Contains(x.VodId));
            if (entryId is { } entry)
                query = query.Where(x => x.EntryId == entry);
            if (videoId is { } video)
                query = query.Where(x => x.VideoId == video);
            if (vertical is { } isVertical)
                query = query.Where(x => x.Vertical == isVertical);
            if (!VerticalAccess.CanSeeTeam(team))
            {
                // Teammates' vertical edits need a role that shows them; horizontal ones are everyone's
                var own = team.UserId;
                query = query.Where(x => x.UserId == own || !x.Vertical);
            }

            var projects = await query.OrderByDescending(x => x.UpdatedAtUtc).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync(cancellationToken);
            return Results.Ok(await ToDtosAsync(db, projects, team, false, cancellationToken));
        }

        private static async Task<IResult> Get(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var project = await db.Projects.Include(x => x.Vods).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return project is null || !CanSee(team, project) ? NotFound() : Results.Ok(await ToDtoAsync(db, project, team, cancellationToken));
        }

        private static IResult NotFound() => Results.Problem(detail: "Project not found.", statusCode: StatusCodes.Status404NotFound);

        private static IResult Invalid(string field, string error) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });

        /// <summary>Checks the title (trimmed; null when not sent).</summary>
        private static (string? Title, IResult? Problem) CheckTitle(string? title)
        {
            if (title is null)
                return (null, null);
            title = title.Trim();
            return title.Length > EditProject.MaxTitleLength
                ? (null, Invalid("title", $"Keep the title to {EditProject.MaxTitleLength} characters."))
                : (title, null);
        }

        /// <summary>
        /// Checks the content and that every VOD it uses that <paramref name="known"/> doesn't already have is one the team may open
        /// (a VOD already in the project stays usable, like other work on a channel the Owner stopped).
        /// </summary>
        private static async Task<IResult?> CheckContentAsync(ProjectContent content, IReadOnlyCollection<long> known, TeamContext team,
            TeamChannelAccess access, CancellationToken cancellationToken)
        {
            if (content.Validate() is { } error)
                return Invalid("content", error);

            // An edit belongs to its VOD; other VODs come in through "Sync with" (badrfly 2026-11-20: the same moment from another
            // streamer's point of view, on a row of its own). Any VOD the team may open is fine.
            foreach (var vodId in content.VodIds.Where(x => !known.Contains(x)))
                if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vodId, cancellationToken), vodId) is { } refused)
                    return refused;
            return null;
        }

        /// <summary>Stores the content and what's read from it (a horizontal edit keeps no layouts).</summary>
        private static IResult? SetContent(EditProject project, ProjectContent content)
        {
            if (!project.Vertical)
                content = content.WithoutLayouts();
            var json = content.Write();
            if (json.Length > EditProject.MaxContentLength)
                return Invalid("content", "The project is too big to save.");

            project.Content = json;
            project.LengthSeconds = content.LengthSeconds;
            project.PieceCount = content.Pieces?.Count ?? 0;
            var vodIds = content.VodIds;
            project.Vods.RemoveAll(x => !vodIds.Contains(x.VodId));
            foreach (var vodId in vodIds.Where(v => project.Vods.All(x => x.VodId != v)))
                project.Vods.Add(new ProjectVod { VodId = vodId });
            return null;
        }

        private static async Task<IResult> Create(ProjectRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            CancellationToken cancellationToken)
        {
            var (title, titleProblem) = CheckTitle(request.Title);
            if (titleProblem is not null)
                return titleProblem;

            if (await db.Projects.CountAsync(x => x.UserId == team.UserId, cancellationToken) >= EditProject.MaxProjectsPerMember)
                return Invalid("project", $"You have {EditProject.MaxProjectsPerMember} projects. Delete some first.");

            var content = request.Content ?? ProjectContent.Empty;

            // Where it belongs: the clip or video named, else the VOD named, else the content's first piece
            long vodId = 0;
            int? entryId = null, videoId = null;
            if (request.EntryId is { } askedEntry)
            {
                var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == askedEntry, cancellationToken);
                if (entry is null || entry.VodId <= 0)
                    return Invalid("entryId", entry is null ? "Clip not found." : "Twitch didn't say which VOD this clip is from.");
                (vodId, entryId) = (entry.VodId, entry.Id);
            }
            else if (request.VideoId is { } askedVideo)
            {
                var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == askedVideo, cancellationToken);
                if (video is null)
                    return Invalid("videoId", "Video not found.");
                (vodId, videoId) = (video.VodId, video.Id);
            }
            else if (request.VodId is > 0)
                vodId = request.VodId.Value;
            else if (await ProjectParents.FindAsync(db, team.TeamId, content, null, cancellationToken) is { } found)
                (vodId, entryId, videoId) = (found.VodId, found.EntryId, found.VideoId);

            if (vodId != 0 && TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vodId, cancellationToken), vodId) is { } refused)
                return refused;
            if (await CheckContentAsync(content, [], team, access, cancellationToken) is { } problem)
                return problem;

            var now = DateTime.UtcNow;
            var project = new EditProject
            {
                Title = title ?? "", Revision = 1, CreatedAtUtc = now, UpdatedAtUtc = now, VodId = vodId, EntryId = entryId, VideoId = videoId,
                Vertical = request.Vertical == true,
            };
            if (SetContent(project, content) is { } tooBig)
                return tooBig;
            db.Projects.Add(project);
            // The clip or video may move on to the team's "edit made" status
            await ItemStatuses.AdvanceAsync(db, team.TeamId, project.Vertical ? StatusTrigger.VerticalEditMade : StatusTrigger.EditMade,
                entryId is { } madeOfEntry ? [madeOfEntry] : null, videoId is { } madeOfVideo ? [madeOfVideo] : null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.ProjectAdded, null, $"{(project.Vertical ? "vertical" : "horizontal")} project {project.Id}{TitleText(project)}, {project.PieceCount} piece{(project.PieceCount == 1 ? "" : "s")}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/projects/{project.Id}", await ToDtoAsync(db, project, team, cancellationToken));
        }

        private static async Task<IResult> Update(int id, ProjectRequest request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            CancellationToken cancellationToken)
        {
            var project = await db.Projects.Include(x => x.Vods).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (project is null || !CanSee(team, project))
                return NotFound();
            if (!CanEdit(team, project))
                return Results.Problem(detail: "You can't change this project. Make your own copy.", statusCode: StatusCodes.Status403Forbidden);

            if (request.Revision is { } revision && revision != project.Revision)
                return Results.Problem(detail: "This project was changed somewhere else.", statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "project-changed",
                        ["project"] = await ToDtoAsync(db, project, team, cancellationToken),
                    });

            var (title, titleProblem) = CheckTitle(request.Title);
            if (titleProblem is not null)
                return titleProblem;

            var changed = false;
            if (title is not null && title != project.Title)
            {
                project.Title = title;
                changed = true;
            }

            if (request.Content is { } content)
            {
                if (await CheckContentAsync(content, project.Vods.Select(x => x.VodId).ToList(), team, access, cancellationToken) is { } problem)
                    return problem;
                if (project.VodId == 0)
                    project.VodId = content.VodIds.FirstOrDefault();
                var before = project.Content;
                if (SetContent(project, content) is { } tooBig)
                    return tooBig;
                if (project.Content != before)
                {
                    project.Revision++;
                    changed = true;
                }
            }

            if (changed)
            {
                project.UpdatedAtUtc = DateTime.UtcNow;
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another save of the same revision got in between our read and write (Revision is a concurrency token): same
                    // answer as a stale revision, with the project as it is now
                    db.ChangeTracker.Clear();
                    var current = await db.Projects.Include(x => x.Vods).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                    if (current is null)
                        return NotFound();
                    return Results.Problem(detail: "This project was changed somewhere else.", statusCode: StatusCodes.Status409Conflict,
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "project-changed",
                            ["project"] = await ToDtoAsync(db, current, team, cancellationToken),
                        });
                }
            }
            return Results.Ok(await ToDtoAsync(db, project, team, cancellationToken));
        }

        private static async Task<IResult> Delete(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (project is null || !CanSee(team, project))
                return NotFound();
            if (!CanDelete(team, project))
                return Results.Problem(detail: "You can't delete this project.", statusCode: StatusCodes.Status403Forbidden);

            db.Projects.Remove(project);
            db.Audit(AuditActions.ProjectDeleted, null, $"project {project.Id}{TitleText(project)}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        /// <summary>The caller's own copy of a project they may copy (with a new title, or "Copy of …").</summary>
        private static async Task<IResult> Copy(int id, ProjectRequest? request, TeamContext team, LibraryDbContext db, TeamChannelAccess access,
            CancellationToken cancellationToken)
        {
            var source = await db.Projects.Include(x => x.Vods).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (source is null || !CanSee(team, source))
                return NotFound();
            if (!CanCopy(team, source))
                return Results.Problem(detail: "Your role doesn't let you copy teammates' vertical edits. Start your own from the original clip.",
                    statusCode: StatusCodes.Status403Forbidden);

            var (title, titleProblem) = CheckTitle(request?.Title);
            if (titleProblem is not null)
                return titleProblem;
            if (await db.Projects.CountAsync(x => x.UserId == team.UserId, cancellationToken) >= EditProject.MaxProjectsPerMember)
                return Invalid("project", $"You have {EditProject.MaxProjectsPerMember} projects. Delete some first.");

            // The copy is new work, so its VODs must be open to the team now
            var content = ProjectContent.Read(source.Content);
            if (await CheckContentAsync(content, source.Vods.Select(x => x.VodId).ToList(), team, access, cancellationToken) is { } problem)
                return problem;
            foreach (var vodId in content.VodIds)
                if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vodId, cancellationToken), vodId) is { } refused)
                    return refused;

            var now = DateTime.UtcNow;
            var copy = new EditProject
            {
                Title = string.IsNullOrEmpty(title) ? CopyTitle(source.Title) : title,
                Revision = 1,
                CopiedFromProjectId = source.Id,
                VodId = source.VodId,
                EntryId = source.EntryId,
                VideoId = source.VideoId,
                Vertical = source.Vertical,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            SetContent(copy, content);
            db.Projects.Add(copy);
            await ItemStatuses.AdvanceAsync(db, team.TeamId, copy.Vertical ? StatusTrigger.VerticalEditMade : StatusTrigger.EditMade,
                copy.EntryId is { } madeOfEntry ? [madeOfEntry] : null, copy.VideoId is { } madeOfVideo ? [madeOfVideo] : null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.ProjectAdded, null, $"project {copy.Id}{TitleText(copy)} (copy of project {source.Id})");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/projects/{copy.Id}", await ToDtoAsync(db, copy, team, cancellationToken));
        }

        private static string CopyTitle(string title)
        {
            var copy = string.IsNullOrEmpty(title) ? "Copy of untitled project" : "Copy of " + title;
            return copy.Length > EditProject.MaxTitleLength ? copy[..EditProject.MaxTitleLength] : copy;
        }

        private static string TitleText(EditProject project) => string.IsNullOrEmpty(project.Title) ? "" : $" \"{project.Title}\"";
    }
}
