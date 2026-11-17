using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="Content">The whole timeline (null keeps it on PUT; an empty project on POST).</param>
    /// <param name="Revision">PUT: the revision the page last saw; a newer one on the server → 409 <c>project-changed</c> with the project.</param>
    public sealed record ProjectRequest(string? Title, ProjectContent? Content = null, int? Revision = null);

    /// <param name="Content">Only from <c>GET /api/projects/{id}</c>, POST, PUT and copy (not in lists).</param>
    public sealed record ProjectDto(int Id, int UserId, string? CreatedBy, string Title, int Revision, double LengthSeconds, int PieceCount,
        IReadOnlyList<long> VodIds, int? CopiedFromProjectId, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool CanEdit, bool CanDelete,
        ProjectContent? Content)
    {
        /// <summary>May make their own copy of it (own: AddEntries; a teammate's: also see it and EditAnyEntries).</summary>
        public bool CanCopy { get; init; }

        /// <summary>May put it on the export list (own, or a teammate's with DownloadTeamVerticals).</summary>
        public bool CanDownload { get; init; }
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

        // Projects are vertical edits: a teammate's are seen, changed, copied, deleted and exported only as VerticalAccess allows
        // (badrfly 2026-11-17); one the caller may not see answers 404 like a missing one
        internal static bool CanSee(TeamContext team, EditProject project) => VerticalAccess.CanSee(team, project.UserId);

        internal static bool CanEdit(TeamContext team, EditProject project) =>
            VerticalAccess.IsOwn(team, project.UserId) ? team.Can(TeamPermission.AddEntries) : VerticalAccess.CanReEditTeam(team);

        private static bool CanDelete(TeamContext team, EditProject project) =>
            VerticalAccess.IsOwn(team, project.UserId) ? team.Can(TeamPermission.AddEntries) : VerticalAccess.CanDeleteTeam(team);

        private static bool CanCopy(TeamContext team, EditProject project) =>
            !team.IsSupport && team.Can(TeamPermission.AddEntries) && (VerticalAccess.IsOwn(team, project.UserId) || VerticalAccess.CanReEditTeam(team));

        internal static bool CanDownload(TeamContext team, EditProject project) => VerticalAccess.CanDownload(team, project.UserId);

        private static async Task<List<ProjectDto>> ToDtosAsync(LibraryDbContext db, IReadOnlyList<EditProject> projects, TeamContext team,
            bool withContent, CancellationToken cancellationToken)
        {
            var names = await TimelineEndpoints.UserNamesAsync(db, projects.Select(x => x.UserId), cancellationToken);
            return projects.Select(x => new ProjectDto(x.Id, x.UserId, names.GetValueOrDefault(x.UserId), x.Title, x.Revision,
                Math.Round(x.LengthSeconds, 2), x.PieceCount, x.Vods.Select(v => v.VodId).OrderBy(v => v).ToList(), x.CopiedFromProjectId,
                x.CreatedAtUtc, x.UpdatedAtUtc, CanEdit(team, x), CanDelete(team, x), withContent ? ProjectContent.Read(x.Content) : null)
            {
                CanCopy = CanCopy(team, x),
                CanDownload = CanDownload(team, x),
            }).ToList();
        }

        private static async Task<ProjectDto> ToDtoAsync(LibraryDbContext db, EditProject project, TeamContext team, CancellationToken cancellationToken) =>
            (await ToDtosAsync(db, [project], team, true, cancellationToken))[0];

        /// <param name="userId">Only this member's.</param>
        /// <param name="vodId">Only projects using this VOD.</param>
        private static async Task<IResult> List(int? userId, long? vodId, int? limit, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var query = db.Projects.Include(x => x.Vods).AsNoTracking();
            if (userId is { } user)
                query = query.Where(x => x.UserId == user);
            if (vodId is { } vod)
                query = query.Where(x => x.Vods.Any(v => v.VodId == vod));
            if (!VerticalAccess.CanSeeTeam(team))
            {
                var own = team.UserId;
                query = query.Where(x => x.UserId == own);
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

            foreach (var vodId in content.VodIds.Where(x => !known.Contains(x)))
                if (TeamChannelAccess.Refusal(await access.CheckVodAsync(team.TeamId, vodId, cancellationToken), vodId) is { } refused)
                    return refused;
            return null;
        }

        /// <summary>Stores the content and what's read from it.</summary>
        private static IResult? SetContent(EditProject project, ProjectContent content)
        {
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
            if (await CheckContentAsync(content, [], team, access, cancellationToken) is { } problem)
                return problem;

            var now = DateTime.UtcNow;
            var project = new EditProject { Title = title ?? "", Revision = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
            if (SetContent(project, content) is { } tooBig)
                return tooBig;
            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.ProjectAdded, null, $"project {project.Id}{TitleText(project)}, {project.PieceCount} piece{(project.PieceCount == 1 ? "" : "s")}");
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
            if (await CheckContentAsync(content, [], team, access, cancellationToken) is { } problem)
                return problem;

            var now = DateTime.UtcNow;
            var copy = new EditProject
            {
                Title = string.IsNullOrEmpty(title) ? CopyTitle(source.Title) : title,
                Revision = 1,
                CopiedFromProjectId = source.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            SetContent(copy, content);
            db.Projects.Add(copy);
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
