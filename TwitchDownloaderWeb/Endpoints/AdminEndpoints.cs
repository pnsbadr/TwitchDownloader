using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record StaffSetupRequest(string? Username, string? Password);

    /// <param name="Needed">No staff account exists yet.</param>
    /// <param name="AllowedHere">Setup is only accepted from the machine running the app (see <see cref="AdminEndpoints"/>).</param>
    public sealed record StaffSetupDto(bool Needed, bool AllowedHere);

    public sealed record StaffMeDto(int Id, string Username, bool IsOwner);

    public sealed record AdminTeamDto(int Id, string Name, DateTime CreatedAtUtc, int? OwnerUserId, string? OwnerUsername, int MemberCount, int EntryCount,
        DateTime? DisabledAtUtc);

    /// <param name="Kind">"registration" (new team) or "ownership" (owner of TeamId).</param>
    public sealed record CreateStaffLinkRequest(string? Kind, int? TeamId, string? Note);

    /// <summary>
    /// The app staff API (/admin.html): the app Owner sets up their staff account, logs in, sees the teams and makes team registration
    /// and team owner links. Staff use their own cookie and tables; a staff login is never a team login.
    /// </summary>
    public static class AdminEndpoints
    {
        private static readonly TimeSpan ClosedLinkListTime = TimeSpan.FromDays(30);

        public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
        {
            var open = app.MapGroup("/api/admin");
            open.MapGet("/setup", GetSetup);
            open.MapPost("/setup", Setup).RequireRateLimiting(AuthSchemes.LoginRateLimit);
            open.MapPost("/login", Login).RequireRateLimiting(AuthSchemes.LoginRateLimit);
            open.MapPost("/logout", Logout);

            var staff = app.MapGroup("/api/admin").AddEndpointFilter(RequireStaff);
            staff.MapGet("/me", Me);
            staff.MapGet("/teams", ListTeams);
            staff.MapGet("/links", ListLinks);
            staff.MapPost("/links", CreateLink);
            staff.MapDelete("/links/{id:int}", RevokeLink);
            return app;
        }

        /// <summary>Lets the request through only with a valid staff session, and records the staff ID for audit events.</summary>
        private static async ValueTask<object?> RequireStaff(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        {
            var context = invocation.HttpContext;
            var result = await context.AuthenticateAsync(AuthSchemes.Staff);
            if (!result.Succeeded || AuthSetup.GetId(result.Principal) is not { } staffId)
                return Results.Problem(detail: "Log in as app staff first.", statusCode: StatusCodes.Status401Unauthorized);

            context.RequestServices.GetRequiredService<RequestActor>().StaffId = staffId;
            context.Items[nameof(StaffMeDto)] = new StaffMeDto(staffId, result.Principal!.Identity?.Name ?? "", false);
            return await next(invocation);
        }

        /// <summary>
        /// The first staff account (the app Owner) can only be made from the machine the app runs on, and only while none exists. A request
        /// carrying X-Forwarded-For came through a proxy, so it doesn't count as local even if the proxy itself is.
        /// </summary>
        private static bool IsLocalRequest(HttpContext context) =>
            context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) && !context.Request.Headers.ContainsKey("X-Forwarded-For");

        private static async Task<IResult> GetSetup(HttpContext context, LibraryDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(new StaffSetupDto(!await db.StaffAccounts.AnyAsync(cancellationToken), IsLocalRequest(context)));

        private static async Task<IResult> Setup(StaffSetupRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (!IsLocalRequest(context))
                return Results.Problem(detail: "Set up the Owner account from the computer running the app (open it as localhost).",
                    statusCode: StatusCodes.Status403Forbidden);

            var username = request.Username?.Trim();
            if (AccountRules.UsernameError(username) is { } usernameError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = [usernameError] });
            if (AccountRules.PasswordError(request.Password) is { } passwordError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [passwordError] });

            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            if (await db.StaffAccounts.AnyAsync(cancellationToken))
                return Results.Problem(detail: "The Owner account already exists. Log in instead.", statusCode: StatusCodes.Status409Conflict);

            var now = DateTime.UtcNow;
            var owner = new StaffAccount
            {
                Username = username!,
                NormalizedUsername = AccountRules.Normalize(username!),
                PasswordHash = AccountRules.HashPassword(request.Password!),
                SecurityStamp = AccountRules.NewSecurityStamp(),
                IsOwner = true,
                CreatedAtUtc = now,
                LastLoginAtUtc = now,
            };
            db.StaffAccounts.Add(owner);
            await db.SaveChangesAsync(cancellationToken);
            db.Audit(AuditActions.StaffSetUp, null, $"Owner account {owner.Username}", staffId: owner.Id);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await SignInAsync(context, owner);
            return Results.NoContent();
        }

        private static async Task<IResult> Login(LoginRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["Enter your username and password."] });

            var normalized = AccountRules.Normalize(request.Username.Trim());
            var staff = await db.StaffAccounts.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, cancellationToken);
            var outcome = LoginCheck.Check(staff, request.Password, DateTime.UtcNow);
            db.Audit(outcome == LoginOutcome.Success ? AuditActions.StaffLoggedIn : AuditActions.StaffLoginFailed, null,
                staff is null ? "Unknown username" : outcome == LoginOutcome.Success ? staff.Username : $"{staff.Username}: {outcome}", staffId: staff?.Id);
            await db.SaveChangesAsync(cancellationToken);

            if (outcome != LoginOutcome.Success)
                return LoginCheck.FailureResult(outcome);

            await SignInAsync(context, staff!);
            return Results.NoContent();
        }

        private static async Task<IResult> Logout(HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var result = await context.AuthenticateAsync(AuthSchemes.Staff);
            if (result.Succeeded && AuthSetup.GetId(result.Principal) is { } id)
            {
                db.Audit(AuditActions.StaffLoggedOut, null, result.Principal!.Identity?.Name, staffId: id);
                await db.SaveChangesAsync(cancellationToken);
            }

            await context.SignOutAsync(AuthSchemes.Staff);
            return Results.NoContent();
        }

        private static async Task<IResult> Me(HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var id = ((StaffMeDto)context.Items[nameof(StaffMeDto)]!).Id;
            var staff = await db.StaffAccounts.AsNoTracking().FirstAsync(x => x.Id == id, cancellationToken);
            return Results.Ok(new StaffMeDto(staff.Id, staff.Username, staff.IsOwner));
        }

        private static async Task<IResult> ListTeams(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var teams = await db.Teams.AsNoTracking().OrderBy(x => x.Name)
                .Select(x => new AdminTeamDto(x.Id, x.Name, x.CreatedAtUtc, x.OwnerUserId, x.OwnerUser!.Username, x.Members.Count,
                    db.Entries.IgnoreQueryFilters().Count(e => e.TeamId == x.Id), x.DisabledAtUtc))
                .ToListAsync(cancellationToken);
            return Results.Ok(teams);
        }

        private static async Task<IResult> ListLinks(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var since = DateTime.UtcNow - ClosedLinkListTime;
            var links = await db.AccountLinks.AsNoTracking().Include(x => x.Team)
                .Where(x => x.CreatedByStaffId != null && x.CreatedAtUtc > since)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            var usedBy = await TeamEndpoints.UsernamesAsync(db, links, cancellationToken);
            return Results.Ok(links.Select(x => LinkUrls.ToDto(x, x.UsedByUserId is { } id ? usedBy.GetValueOrDefault(id) : null)));
        }

        private static async Task<IResult> CreateLink(CreateStaffLinkRequest request, HttpContext context, LibraryDbContext db, RequestActor actor,
            CancellationToken cancellationToken)
        {
            AccountLinkKind kind;
            Team? team = null;
            switch (request.Kind?.ToLowerInvariant())
            {
                case "registration":
                    kind = AccountLinkKind.TeamRegistration;
                    break;
                case "ownership":
                    kind = AccountLinkKind.TeamOwnership;
                    team = request.TeamId is { } teamId ? await db.Teams.FirstOrDefaultAsync(x => x.Id == teamId, cancellationToken) : null;
                    if (team is null)
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["teamId"] = ["Pick an existing team."] });
                    break;
                default:
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Kind must be registration or ownership."] });
            }

            var (link, token) = LinkUrls.New(kind, team?.Id, request.Note, DateTime.UtcNow);
            link.CreatedByStaffId = actor.StaffId;
            db.AccountLinks.Add(link);
            await db.SaveChangesAsync(cancellationToken);

            db.Audit(AuditActions.LinkCreated, null,
                $"{kind} link {link.Id}{(team is null ? "" : $" for \"{team.Name}\"")}{(link.Note is null ? "" : $" ({link.Note})")}", teamId: team?.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/links/{link.Id}", LinkUrls.ToDto(link, null, LinkUrls.Build(context.Request, token)));
        }

        private static async Task<IResult> RevokeLink(int id, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var link = await db.AccountLinks.FirstOrDefaultAsync(x => x.Id == id && x.CreatedByStaffId != null, cancellationToken);
            if (link is null)
                return Results.Problem(detail: "Link not found.", statusCode: StatusCodes.Status404NotFound);

            if (link.UsedAtUtc is null && link.RevokedAtUtc is null)
            {
                link.RevokedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.LinkRevoked, null, $"{link.Kind} link {link.Id}", teamId: link.TeamId);
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        private static Task SignInAsync(HttpContext context, StaffAccount staff) =>
            context.SignInAsync(AuthSchemes.Staff, AuthSetup.Principal(AuthSchemes.Staff, staff.Id, staff.Username, staff.SecurityStamp), AuthSetup.PersistentSession());
    }
}
