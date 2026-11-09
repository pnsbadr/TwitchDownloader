using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record SwitchTeamRequest(int TeamId);

    public sealed record MeTeamDto(int Id, string Name, bool IsOwner);

    /// <param name="CurrentTeamId">The team library requests use (switcher cookie, else the first team). Null when not in any team.</param>
    public sealed record MeDto(int Id, string Username, IReadOnlyList<MeTeamDto> Teams, int? CurrentTeamId);

    public sealed record LinkTokenRequest(string? Token);

    /// <param name="NewAccount">True creates an account with Username/Password; false logs in to an existing one (or uses the
    /// account already logged in when Username is blank).</param>
    /// <param name="TeamName">Team registration links only: the new team's name.</param>
    public sealed record AcceptLinkRequest(string? Token, bool NewAccount, string? Username, string? Password, string? TeamName);

    /// <param name="LoggedInAs">The user already logged in in this browser, if any, so the page can offer "join as …".</param>
    public sealed record LinkInfoDto(string Kind, string? TeamName, DateTime ExpiresAtUtc, string? LoggedInAs);

    /// <summary>Team user login and one-time links (team registration, team ownership, team invites).</summary>
    public static class AuthEndpoints
    {
        public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
        {
            var auth = app.MapGroup("/api/auth");
            auth.MapPost("/login", Login).RequireRateLimiting(AuthSchemes.LoginRateLimit);
            auth.MapPost("/logout", Logout);
            auth.MapGet("/me", Me);
            auth.MapPost("/team", SwitchTeam);

            // Tokens travel in the body, never the URL, so they can't end up in request logs
            var links = app.MapGroup("/api/links");
            links.MapPost("/inspect", InspectLink).RequireRateLimiting(AuthSchemes.LoginRateLimit);
            links.MapPost("/accept", AcceptLink).RequireRateLimiting(AuthSchemes.LoginRateLimit);

            return app;
        }

        private static async Task<IResult> Login(LoginRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["Enter your username and password."] });

            var normalized = AccountRules.Normalize(request.Username.Trim());
            var user = await db.Users.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, cancellationToken);
            var outcome = LoginCheck.Check(user, request.Password, DateTime.UtcNow);
            if (outcome != LoginOutcome.Success)
            {
                // Never the password; the username only when it's a real account (typos of passwords land in the username field)
                db.Audit(AuditActions.UserLoginFailed, null, user is null ? "Unknown username" : $"{user.Username}: {outcome}",
                    userId: user?.Id.ToString());
                await db.SaveChangesAsync(cancellationToken);
                return LoginCheck.FailureResult(outcome);
            }

            db.Audit(AuditActions.UserLoggedIn, null, user!.Username, userId: user.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            await SignInAsync(context, user);
            return Results.NoContent();
        }

        private static async Task<IResult> Logout(HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (AuthSetup.GetId(context.User) is { } id)
            {
                db.Audit(AuditActions.UserLoggedOut, null, context.User.Identity?.Name, userId: id.ToString());
                await db.SaveChangesAsync(cancellationToken);
            }

            await context.SignOutAsync(AuthSchemes.User);
            return Results.NoContent();
        }

        private static async Task<IResult> Me(HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (AuthSetup.GetId(context.User) is not { } id)
                return Results.Problem(detail: "Log in first.", statusCode: StatusCodes.Status401Unauthorized);

            var user = await db.Users.AsNoTracking().FirstAsync(x => x.Id == id, cancellationToken);
            var teams = await db.TeamMembers.AsNoTracking()
                .Where(x => x.UserId == id && x.Team!.DisabledAtUtc == null)
                .OrderBy(x => x.JoinedAtUtc)
                .Select(x => new MeTeamDto(x.TeamId, x.Team!.Name, x.Team.OwnerUserId == id))
                .ToListAsync(cancellationToken);

            var wanted = int.TryParse(context.Request.Cookies[TeamScope.TeamCookie], out var cookieTeam) ? cookieTeam : (int?)null;
            var current = teams.FirstOrDefault(x => x.Id == wanted) ?? teams.FirstOrDefault();
            return Results.Ok(new MeDto(user.Id, user.Username, teams, current?.Id));
        }

        private static async Task<IResult> SwitchTeam(SwitchTeamRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (AuthSetup.GetId(context.User) is not { } id)
                return Results.Problem(detail: "Log in first.", statusCode: StatusCodes.Status401Unauthorized);

            if (!await db.TeamMembers.AnyAsync(x => x.UserId == id && x.TeamId == request.TeamId && x.Team!.DisabledAtUtc == null, cancellationToken))
                return Results.Problem(detail: "You aren't in that team.", statusCode: StatusCodes.Status404NotFound);

            SetTeamCookie(context, request.TeamId);
            return Results.NoContent();
        }

        private static async Task<IResult> InspectLink(LinkTokenRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var link = await FindUsableLinkAsync(db, request.Token, cancellationToken);
            if (link is null)
                return LinkInvalid();

            return Results.Ok(new LinkInfoDto(link.Kind.ToString(), link.Team?.Name, link.ExpiresAtUtc,
                AuthSetup.GetId(context.User) is not null ? context.User.Identity?.Name : null));
        }

        /// <summary>
        /// Uses a one-time link: makes or logs in to the account, then applies the link (new team / team ownership / membership).
        /// The link is claimed with a conditional update inside the transaction, so two people opening it at once can't both use it.
        /// </summary>
        private static async Task<IResult> AcceptLink(AcceptLinkRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var link = await FindUsableLinkAsync(db, request.Token, cancellationToken);
            if (link is null)
                return LinkInvalid();

            string? teamName = null;
            if (link.Kind == AccountLinkKind.TeamRegistration)
            {
                if (AccountRules.TeamNameError(request.TeamName) is { } nameError)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["teamName"] = [nameError] });
                teamName = request.TeamName!.Trim();
            }

            var now = DateTime.UtcNow;
            UserAccount user;
            if (request.NewAccount)
            {
                var username = request.Username?.Trim();
                if (AccountRules.UsernameError(username) is { } usernameError)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = [usernameError] });
                if (AccountRules.PasswordError(request.Password) is { } passwordError)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [passwordError] });

                var normalized = AccountRules.Normalize(username!);
                if (await db.Users.AnyAsync(x => x.NormalizedUsername == normalized, cancellationToken))
                    return UsernameTaken();

                user = new UserAccount
                {
                    Username = username!,
                    NormalizedUsername = normalized,
                    PasswordHash = AccountRules.HashPassword(request.Password!),
                    SecurityStamp = AccountRules.NewSecurityStamp(),
                    CreatedAtUtc = now,
                    LastLoginAtUtc = now,
                };
            }
            else if (string.IsNullOrEmpty(request.Username) && AuthSetup.GetId(context.User) is { } loggedInId)
            {
                user = await db.Users.FirstAsync(x => x.Id == loggedInId, cancellationToken);
            }
            else
            {
                if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["Enter your username and password."] });

                var normalized = AccountRules.Normalize(request.Username.Trim());
                var existing = await db.Users.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, cancellationToken);
                var outcome = LoginCheck.Check(existing, request.Password, now);
                if (outcome != LoginOutcome.Success)
                {
                    db.Audit(AuditActions.UserLoginFailed, null, existing is null ? "Unknown username (link page)" : $"{existing.Username}: {outcome} (link page)",
                        userId: existing?.Id.ToString());
                    await db.SaveChangesAsync(cancellationToken);
                    return LoginCheck.FailureResult(outcome);
                }
                user = existing!;
            }

            if (link.TeamId is { } linkTeamId && link.Kind == AccountLinkKind.TeamInvite && user.Id != 0
                && await db.TeamMembers.AnyAsync(x => x.TeamId == linkTeamId && x.UserId == user.Id, cancellationToken))
                return Results.Problem(detail: $"{user.Username} is already in {link.Team!.Name}.", statusCode: StatusCodes.Status409Conflict);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (user.Id == 0)
            {
                db.Users.Add(user);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    return UsernameTaken();
                }
                db.Audit(AuditActions.UserSignedUp, null, $"{user.Username} via {link.Kind} link", userId: user.Id.ToString());
            }

            var claimed = await db.AccountLinks
                .Where(x => x.Id == link.Id && x.UsedAtUtc == null && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(x => x.SetProperty(l => l.UsedAtUtc, now).SetProperty(l => l.UsedByUserId, user.Id), cancellationToken);
            if (claimed == 0)
                return LinkInvalid();

            Team team;
            switch (link.Kind)
            {
                case AccountLinkKind.TeamRegistration:
                    team = new Team { Name = teamName!, CreatedAtUtc = now, OwnerUserId = user.Id };
                    db.Teams.Add(team);
                    await db.SaveChangesAsync(cancellationToken);
                    db.Audit(AuditActions.TeamCreated, null, $"\"{team.Name}\" by {user.Username}", userId: user.Id.ToString(), teamId: team.Id);
                    break;

                case AccountLinkKind.TeamOwnership:
                    team = await db.Teams.FirstAsync(x => x.Id == link.TeamId, cancellationToken);
                    team.OwnerUserId = user.Id;
                    db.Audit(AuditActions.TeamOwnerChanged, null, $"\"{team.Name}\" now owned by {user.Username} (owner link)", userId: user.Id.ToString(), teamId: team.Id);
                    break;

                default:
                    team = await db.Teams.FirstAsync(x => x.Id == link.TeamId, cancellationToken);
                    break;
            }

            if (!await db.TeamMembers.AnyAsync(x => x.TeamId == team.Id && x.UserId == user.Id, cancellationToken))
            {
                db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = user.Id, JoinedAtUtc = now });
                db.Audit(AuditActions.TeamMemberJoined, null, $"{user.Username} joined \"{team.Name}\" ({link.Kind} link)", userId: user.Id.ToString(), teamId: team.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await SignInAsync(context, user);
            SetTeamCookie(context, team.Id);
            return Results.Ok(new { teamId = team.Id, teamName = team.Name });
        }

        private static async Task<AccountLink?> FindUsableLinkAsync(LibraryDbContext db, string? token, CancellationToken cancellationToken)
        {
            if (AccountRules.HashTokenOrNull(token) is not { } hash)
                return null;

            var now = DateTime.UtcNow;
            return await db.AccountLinks.Include(x => x.Team)
                .FirstOrDefaultAsync(x => x.TokenHash == hash && x.UsedAtUtc == null && x.RevokedAtUtc == null && x.ExpiresAtUtc > now
                                          && (x.Team == null || x.Team.DisabledAtUtc == null), cancellationToken);
        }

        public static Task SignInAsync(HttpContext context, UserAccount user) =>
            context.SignInAsync(AuthSchemes.User, AuthSetup.Principal(AuthSchemes.User, user.Id, user.Username, user.SecurityStamp), AuthSetup.PersistentSession());

        private static void SetTeamCookie(HttpContext context, int teamId) =>
            context.Response.Cookies.Append(TeamScope.TeamCookie, teamId.ToString(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                MaxAge = TimeSpan.FromDays(365),
            });

        private static IResult LinkInvalid() =>
            Results.Problem(detail: "This link doesn't work anymore. It may have been used, revoked or expired; ask for a new one.",
                statusCode: StatusCodes.Status404NotFound);

        private static IResult UsernameTaken() =>
            Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["That username is taken."] });
    }
}
