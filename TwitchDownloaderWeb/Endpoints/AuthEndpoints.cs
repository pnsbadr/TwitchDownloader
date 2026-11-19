using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record SwitchTeamRequest(int TeamId);

    public sealed record MeTeamDto(int Id, string Name, bool IsOwner);

    /// <param name="CurrentTeamId">The team library requests use (switcher cookie, else the first team). Null when not in any team.</param>
    /// <param name="Permissions">The caller's permissions in that team (all of them for its owner); the page hides what they can't do.</param>
    /// <param name="TimeZone">"local" or "pacific".</param>
    /// <param name="Support">Set during a support visit (app staff viewing a team read-only); Id is then 0 and Username the staff account's.</param>
    /// <param name="CombineSegmentDownloads">The current team may download picked segments joined into one file (the app Owner turns it on per team).</param>
    public sealed record MeDto(int Id, string Username, IReadOnlyList<MeTeamDto> Teams, int? CurrentTeamId, IReadOnlyList<string> Permissions, string TimeZone,
        MeSupportDto? Support = null, bool CombineSegmentDownloads = false)
    {
        /// <summary>The stored-clip limit (Downloads:MaxStoredClipSeconds): clips over it are "Long" references on the page, whether or not the
        /// team stores clips (<see cref="Data.Team.StoreClips"/>); shorter references look like any clip.</summary>
        public int MaxStoredClipSeconds { get; init; }
    }

    public sealed record MeSupportDto(string StaffUsername, int TeamId, string TeamName);

    public sealed record UserSettingsRequest(string? TimeZone);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    public sealed record LinkTokenRequest(string? Token);

    /// <param name="NewAccount">True creates an account with Username/Password; false logs in to an existing one (or uses the
    /// account already logged in when Username is blank).</param>
    /// <param name="TeamName">Team registration links only: the new team's name.</param>
    public sealed record AcceptLinkRequest(string? Token, bool NewAccount, string? Username, string? Password, string? TeamName);

    /// <param name="LoggedInAs">The user already logged in in this browser, if any, so the page can offer "join as …".</param>
    /// <param name="ForUsername">Password reset links: whose password it sets.</param>
    /// <param name="Channels">Team links: names of the Twitch channels the team gets.</param>
    public sealed record LinkInfoDto(string Kind, string? TeamName, DateTime ExpiresAtUtc, string? LoggedInAs, string? ForUsername,
        IReadOnlyList<string> Channels)
    {
        /// <summary>A team may pick a new name once every this many days (picking one here counts).</summary>
        public int TeamRenameDays => AccountRules.TeamRenameDays;
    }

    /// <summary>A team card on the start screen (badrfly 2026-11-19).</summary>
    /// <param name="Channel">The channel the team is known by (<see cref="TeamLabels.Primary"/>), or null.</param>
    /// <param name="HasAccess">The member has a role with at least one permission (else the editor shows "Waiting for access").</param>
    /// <param name="CanOpenTeamPage">The member may open the Team page (owner, or a role with any team-page permission).</param>
    /// <param name="TeamLastActiveUtc">The last change any member made to the team's work (staff visits don't count).</param>
    /// <param name="MyLastActiveUtc">The last change the member made to it.</param>
    public sealed record StartTeamDto(int Id, string Name, bool IsOwner, bool IsCurrent, bool HasAccess, bool CanOpenTeamPage, StartChannelDto? Channel,
        DateTime? TeamLastActiveUtc, DateTime? MyLastActiveUtc);

    public sealed record StartChannelDto(string Login, string DisplayName);

    /// <summary>Team user login and one-time links (team registration, team ownership, team invites).</summary>
    public static class AuthEndpoints
    {
        public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
        {
            var auth = app.MapGroup("/api/auth");
            auth.MapPost("/login", Login).RequireRateLimiting(AuthSchemes.LoginRateLimit);
            auth.MapPost("/logout", Logout);
            auth.MapGet("/me", Me);
            auth.MapGet("/teams", ListTeams);
            auth.MapGet("/teams/{id:int}/channel", TeamChannelCard);
            auth.MapGet("/app", AppInfo);
            auth.MapPost("/team", SwitchTeam);
            auth.MapPut("/settings", UpdateSettings);
            auth.MapPost("/password", ChangePassword).RequireRateLimiting(AuthSchemes.LoginRateLimit);

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

        private static async Task<IResult> Me(HttpContext context, LibraryDbContext db, Microsoft.Extensions.Options.IOptions<DownloadSettings> downloads,
            CancellationToken cancellationToken)
        {
            var limit = downloads.Value.MaxStoredClipSeconds;
            if (SupportView.Requested(context))
            {
                if (await SupportView.ResolveAsync(context, db) is not { } support)
                    return Results.Json(new { status = 403, code = "support-ended", detail = "This support view has ended. Open it again from the Admin page." },
                        statusCode: StatusCodes.Status403Forbidden, contentType: "application/problem+json");
                return Results.Ok(new MeDto(0, support.Staff.Username, [new MeTeamDto(support.TeamId, support.TeamName, false)], support.TeamId,
                    TeamPermissions.ToKeys(SupportView.Permissions), "local", new MeSupportDto(support.Staff.Username, support.TeamId, support.TeamName)) { MaxStoredClipSeconds = limit });
            }

            if (AuthSetup.GetId(context.User) is not { } id)
                return Results.Problem(detail: "Log in first.", statusCode: StatusCodes.Status401Unauthorized);

            var user = await db.Users.AsNoTracking().FirstAsync(x => x.Id == id, cancellationToken);
            var memberships = await db.TeamMembers.AsNoTracking()
                .Where(x => x.UserId == id && x.Team!.DisabledAtUtc == null)
                .OrderBy(x => x.JoinedAtUtc)
                .Select(x => new
                {
                    x.TeamId, x.Team!.Name, IsOwner = x.Team.OwnerUserId == id, Permissions = x.Role == null ? TeamPermission.None : x.Role.Permissions,
                    x.Team.CombineSegmentDownloads,
                })
                .ToListAsync(cancellationToken);

            var wanted = int.TryParse(context.Request.Cookies[TeamScope.TeamCookie], out var cookieTeam) ? cookieTeam : (int?)null;
            var current = memberships.FirstOrDefault(x => x.TeamId == wanted) ?? memberships.FirstOrDefault();
            var permissions = current is null ? TeamPermission.None : current.IsOwner ? TeamPermission.All : current.Permissions;
            return Results.Ok(new MeDto(user.Id, user.Username, memberships.Select(x => new MeTeamDto(x.TeamId, x.Name, x.IsOwner)).ToList(), current?.TeamId,
                TeamPermissions.ToKeys(permissions), user.TimeZone ?? "local", CombineSegmentDownloads: current?.CombineSegmentDownloads == true) { MaxStoredClipSeconds = limit });
        }

        /// <summary>Audited actions that change a team's work: what "last active" on the start screen counts.</summary>
        private static readonly string[] WorkChanges =
        [
            AuditActions.EntryAdded, AuditActions.EntryRenamed, AuditActions.EntryRetrimmed, AuditActions.EntryDeleted, AuditActions.EntryMovedRow,
            AuditActions.MarkerAdded, AuditActions.MarkerChanged, AuditActions.MarkerDeleted, AuditActions.RowDeleted,
            AuditActions.VideoAdded, AuditActions.VideoChanged, AuditActions.VideoDeleted,
            AuditActions.EditAdded, AuditActions.EditDeleted, AuditActions.EditApplied, AuditActions.EditReverted,
            AuditActions.ProjectAdded, AuditActions.ProjectDeleted, AuditActions.ShortformRequested, AuditActions.ShortformDeleted,
        ];

        /// <summary>
        /// The start screen's team cards: the caller's teams (not disabled) with their channel and when the team and the caller last changed
        /// its work. Reads every one of the caller's teams, so it filters by team itself instead of the per-request team filter.
        /// </summary>
        private static async Task<IResult> ListTeams(HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (AuthSetup.GetId(context.User) is not { } id)
                return Results.Problem(detail: "Log in first.", statusCode: StatusCodes.Status401Unauthorized);

            var memberships = await db.TeamMembers.AsNoTracking()
                .Where(x => x.UserId == id && x.Team!.DisabledAtUtc == null)
                .OrderBy(x => x.JoinedAtUtc)
                .Select(x => new { x.TeamId, x.Team!.Name, IsOwner = x.Team.OwnerUserId == id, Permissions = x.Role == null ? TeamPermission.None : x.Role.Permissions })
                .ToListAsync(cancellationToken);
            var teamIds = memberships.Select(x => x.TeamId).ToList();
            var me = id.ToString();

            var channels = (await db.Channels.IgnoreQueryFilters().AsNoTracking().Where(x => teamIds.Contains(x.TeamId)).ToListAsync(cancellationToken))
                .ToLookup(x => x.TeamId);
            var changes = db.AuditEvents.AsNoTracking()
                .Where(x => x.TeamId != null && teamIds.Contains(x.TeamId.Value) && x.StaffId == null && x.UserId != null && WorkChanges.Contains(x.Action));
            var teamLast = await changes.GroupBy(x => x.TeamId!.Value).Select(g => new { g.Key, Last = g.Max(x => x.TimestampUtc) })
                .ToDictionaryAsync(x => x.Key, x => (DateTime?)x.Last, cancellationToken);
            var myLast = await changes.Where(x => x.UserId == me).GroupBy(x => x.TeamId!.Value).Select(g => new { g.Key, Last = g.Max(x => x.TimestampUtc) })
                .ToDictionaryAsync(x => x.Key, x => (DateTime?)x.Last, cancellationToken);
            // Player 2's saves aren't audited (the page saves as you work): an edit's last save counts for its owner
            var projects = db.Projects.IgnoreQueryFilters().AsNoTracking().Where(x => teamIds.Contains(x.TeamId));
            var teamProjects = await projects.GroupBy(x => x.TeamId).Select(g => new { g.Key, Last = g.Max(x => x.UpdatedAtUtc) })
                .ToDictionaryAsync(x => x.Key, x => (DateTime?)x.Last, cancellationToken);
            var myProjects = await projects.Where(x => x.UserId == id).GroupBy(x => x.TeamId).Select(g => new { g.Key, Last = g.Max(x => x.UpdatedAtUtc) })
                .ToDictionaryAsync(x => x.Key, x => (DateTime?)x.Last, cancellationToken);

            var wanted = int.TryParse(context.Request.Cookies[TeamScope.TeamCookie], out var cookieTeam) ? cookieTeam : (int?)null;
            var current = memberships.FirstOrDefault(x => x.TeamId == wanted)?.TeamId ?? memberships.FirstOrDefault()?.TeamId;
            static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

            return Results.Ok(memberships.Select(x =>
            {
                var permissions = x.IsOwner ? TeamPermission.All : x.Permissions;
                var channel = TeamLabels.Primary(channels[x.TeamId]);
                return new StartTeamDto(x.TeamId, x.Name, x.IsOwner, x.TeamId == current, permissions != TeamPermission.None,
                    (permissions & TeamPermission.TeamPage) != 0,
                    channel is null ? null : new StartChannelDto(channel.Login, channel.DisplayName),
                    Latest(teamLast.GetValueOrDefault(x.TeamId), teamProjects.GetValueOrDefault(x.TeamId)),
                    Latest(myLast.GetValueOrDefault(x.TeamId), myProjects.GetValueOrDefault(x.TeamId)));
            }).ToList());
        }

        /// <summary>A team card's channel facts from Twitch (picture, live or last live), read separately so the cards show at once.</summary>
        private static async Task<IResult> TeamChannelCard(int id, HttpContext context, LibraryDbContext db, IChannelCardSource cards,
            CancellationToken cancellationToken)
        {
            if (AuthSetup.GetId(context.User) is not { } userId)
                return Results.Problem(detail: "Log in first.", statusCode: StatusCodes.Status401Unauthorized);
            if (!await db.TeamMembers.AnyAsync(x => x.TeamId == id && x.UserId == userId && x.Team!.DisabledAtUtc == null, cancellationToken))
                return Results.Problem(detail: "Team not found.", statusCode: StatusCodes.Status404NotFound);

            var channels = await db.Channels.IgnoreQueryFilters().AsNoTracking().Where(x => x.TeamId == id).ToListAsync(cancellationToken);
            if (TeamLabels.Primary(channels) is not { } channel)
                return Results.Problem(detail: "The team has no channel.", statusCode: StatusCodes.Status404NotFound);
            return Results.Ok(await cards.GetAsync(channel.Login, channel.TwitchUserId, cancellationToken));
        }

        /// <summary>For every page, logged in or not: the app's name and the start screen's corner links (Admin → App, else <c>App:*</c>; blank = hidden).</summary>
        private static async Task<IResult> AppInfo(LibraryDbContext db, IConfiguration configuration, CancellationToken cancellationToken) =>
            Results.Ok(await AppSettingsEndpoints.ReadAsync(db, configuration, cancellationToken));

        private static async Task<IResult> UpdateSettings(UserSettingsRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (AuthSetup.GetId(context.User) is not { } id)
                return Results.Problem(detail: "Log in first.", statusCode: StatusCodes.Status401Unauthorized);
            if (request.TimeZone is not ("local" or "pacific"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["timeZone"] = ["Time zone must be local or pacific."] });

            var user = await db.Users.FirstAsync(x => x.Id == id, cancellationToken);
            user.TimeZone = request.TimeZone;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        /// <summary>Needs the current password. Signs out the account's other sessions (new security stamp) and keeps this one.</summary>
        private static async Task<IResult> ChangePassword(ChangePasswordRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (AuthSetup.GetId(context.User) is not { } id)
                return Results.Problem(detail: "Log in first.", statusCode: StatusCodes.Status401Unauthorized);
            if (AccountRules.PasswordError(request.NewPassword) is { } passwordError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [passwordError] });

            var user = await db.Users.FirstAsync(x => x.Id == id, cancellationToken);
            var outcome = LoginCheck.Check(user, request.CurrentPassword ?? "", DateTime.UtcNow);
            if (outcome != LoginOutcome.Success)
            {
                await db.SaveChangesAsync(cancellationToken);
                return outcome == LoginOutcome.WrongPassword
                    ? Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Your current password is wrong."] })
                    : LoginCheck.FailureResult(outcome);
            }

            SetPassword(user, request.NewPassword!);
            db.Audit(AuditActions.PasswordChanged, null, user.Username);
            await db.SaveChangesAsync(cancellationToken);
            await SignInAsync(context, user);
            return Results.NoContent();
        }

        /// <summary>New hash and security stamp (every other session ends), and any lockout cleared.</summary>
        private static void SetPassword(UserAccount user, string password)
        {
            user.PasswordHash = AccountRules.HashPassword(password);
            user.SecurityStamp = AccountRules.NewSecurityStamp();
            user.FailedLoginCount = 0;
            user.LockedUntilUtc = null;
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

            var forUsername = link.TargetUserId is { } target
                ? await db.Users.Where(x => x.Id == target).Select(x => x.Username).FirstOrDefaultAsync(cancellationToken)
                : link.TargetStaffId is { } staffTarget
                    ? await db.StaffAccounts.Where(x => x.Id == staffTarget).Select(x => x.Username).FirstOrDefaultAsync(cancellationToken)
                    : null;
            return Results.Ok(new LinkInfoDto(link.Kind.ToString(), link.Team?.Name ?? link.TeamName, link.ExpiresAtUtc,
                AuthSetup.GetId(context.User) is not null ? context.User.Identity?.Name : null, forUsername,
                LinkChannel.Read(link.ChannelsJson).Select(x => x.DisplayName).ToList()));
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

            switch (link.Kind)
            {
                case AccountLinkKind.PasswordReset:
                    return await ResetPasswordAsync(link, request, context, db, cancellationToken);
                case AccountLinkKind.StaffInvite:
                    return await AcceptStaffInviteAsync(link, request, context, db, cancellationToken);
                case AccountLinkKind.StaffPasswordReset:
                    return await ResetStaffPasswordAsync(link, request, context, db, cancellationToken);
            }

            string? teamName = null;
            var pickedName = false;
            if (link.Kind == AccountLinkKind.TeamRegistration && link.TeamName is not null)
            {
                // The link suggests "{channel}'s Team"; the person opening it may change it, which counts as the team's rename (once every
                // 14 days after). badrfly 2026-11-19.
                teamName = link.TeamName;
                if (!string.IsNullOrWhiteSpace(request.TeamName) && request.TeamName.Trim() != link.TeamName)
                {
                    if (AccountRules.TeamNameError(request.TeamName) is { } nameError)
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["teamName"] = [nameError] });
                    teamName = request.TeamName.Trim();
                    pickedName = true;
                }
            }
            else if (link.Kind == AccountLinkKind.TeamRegistration)
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
                    team = new Team { Name = teamName!, CreatedAtUtc = now, OwnerUserId = user.Id, NameChangedAtUtc = pickedName ? now : null };
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

            // Staff team links can carry the channels the team works on (badrfly, 2026-11-15)
            var linkChannels = LinkChannel.Read(link.ChannelsJson);
            if (linkChannels.Count > 0)
            {
                var existing = await db.Channels.IgnoreQueryFilters().Where(x => x.TeamId == team.Id).ToListAsync(cancellationToken);
                foreach (var channel in linkChannels)
                {
                    if (existing.FirstOrDefault(x => x.Login == channel.Login) is { } known)
                    {
                        if (known.AccessEndedAtUtc is not null)
                            AdminEndpoints.Reassign(db, team, known, $"({link.Kind} link {link.Id})");
                    }
                    else
                    {
                        AdminEndpoints.AddChannel(db, team, channel, $"({link.Kind} link {link.Id})");
                    }
                }
                TeamChannelAccess.Forget(team.Id);
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

        private static async Task<IResult> ResetPasswordAsync(AccountLink link, AcceptLinkRequest request, HttpContext context, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            if (AccountRules.PasswordError(request.Password) is { } passwordError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [passwordError] });

            var now = DateTime.UtcNow;
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == link.TargetUserId, cancellationToken);
            if (user is null)
                return LinkInvalid();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var claimed = await db.AccountLinks
                .Where(x => x.Id == link.Id && x.UsedAtUtc == null && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(x => x.SetProperty(l => l.UsedAtUtc, now).SetProperty(l => l.UsedByUserId, user.Id), cancellationToken);
            if (claimed == 0)
                return LinkInvalid();

            SetPassword(user, request.Password!);
            user.LastLoginAtUtc = now;
            db.Audit(AuditActions.PasswordReset, null, $"{user.Username} (reset link {link.Id})", userId: user.Id.ToString(), teamId: link.TeamId);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await SignInAsync(context, user);
            return Results.Ok(new { teamId = link.TeamId, teamName = (string?)null });
        }

        /// <summary>A new app moderator: always a new staff account (staff and team users are separate), with no permissions until the Owner picks them.</summary>
        private static async Task<IResult> AcceptStaffInviteAsync(AccountLink link, AcceptLinkRequest request, HttpContext context, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var username = request.Username?.Trim();
            if (AccountRules.UsernameError(username) is { } usernameError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = [usernameError] });
            if (AccountRules.PasswordError(request.Password) is { } passwordError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [passwordError] });

            var normalized = AccountRules.Normalize(username!);
            if (await db.StaffAccounts.AnyAsync(x => x.NormalizedUsername == normalized, cancellationToken))
                return UsernameTaken();

            var now = DateTime.UtcNow;
            var staff = new StaffAccount
            {
                Username = username!,
                NormalizedUsername = normalized,
                PasswordHash = AccountRules.HashPassword(request.Password!),
                SecurityStamp = AccountRules.NewSecurityStamp(),
                CreatedAtUtc = now,
                LastLoginAtUtc = now,
            };

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.StaffAccounts.Add(staff);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return UsernameTaken();
            }

            var claimed = await db.AccountLinks
                .Where(x => x.Id == link.Id && x.UsedAtUtc == null && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(x => x.SetProperty(l => l.UsedAtUtc, now).SetProperty(l => l.UsedByStaffId, staff.Id), cancellationToken);
            if (claimed == 0)
                return LinkInvalid(); // the transaction rolls the new account back

            db.Audit(AuditActions.StaffJoined, null, $"Moderator {staff.Username} (invite link {link.Id})", staffId: staff.Id);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await AdminEndpoints.SignInAsync(context, staff);
            return Results.Ok(new { staff = true });
        }

        private static async Task<IResult> ResetStaffPasswordAsync(AccountLink link, AcceptLinkRequest request, HttpContext context, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            if (AccountRules.PasswordError(request.Password) is { } passwordError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [passwordError] });

            var now = DateTime.UtcNow;
            var staff = await db.StaffAccounts.FirstOrDefaultAsync(x => x.Id == link.TargetStaffId && !x.IsOwner, cancellationToken);
            if (staff is null)
                return LinkInvalid();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var claimed = await db.AccountLinks
                .Where(x => x.Id == link.Id && x.UsedAtUtc == null && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(x => x.SetProperty(l => l.UsedAtUtc, now).SetProperty(l => l.UsedByStaffId, staff.Id), cancellationToken);
            if (claimed == 0)
                return LinkInvalid();

            AdminEndpoints.SetPassword(staff, request.Password!);
            staff.LastLoginAtUtc = now;
            db.Audit(AuditActions.StaffPasswordReset, null, $"{staff.Username} (reset link {link.Id})", staffId: staff.Id);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await AdminEndpoints.SignInAsync(context, staff);
            return Results.Ok(new { staff = true });
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
