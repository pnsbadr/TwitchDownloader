using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record StaffSetupRequest(string? Username, string? Password);

    /// <param name="Needed">No staff account exists yet.</param>
    /// <param name="AllowedHere">Setup is only accepted from the machine running the app (see <see cref="AdminEndpoints"/>).</param>
    public sealed record StaffSetupDto(bool Needed, bool AllowedHere);

    public sealed record StaffMeDto(int Id, string Username, bool IsOwner, IReadOnlyList<string> Permissions, IReadOnlyList<StaffPermissionInfo> PermissionCatalog);

    public sealed record AdminTeamDto(int Id, string Name, DateTime CreatedAtUtc, int? OwnerUserId, string? OwnerUsername, int MemberCount, int EntryCount,
        DateTime? DisabledAtUtc, bool AutoSaveAudio, long DiskBytes, DateTime? LastActiveAtUtc);

    /// <param name="OtherTeams">How many other teams this person is in.</param>
    public sealed record AdminTeamMemberDto(int UserId, string Username, bool IsOwner, string? RoleName, DateTime JoinedAtUtc, DateTime? LastActiveAtUtc,
        DateTime? DisabledAtUtc, int OtherTeams, IReadOnlyDictionary<string, int> Counts, int EntriesAdded, long DiskBytes);

    public sealed record AdminTeamDetailDto(AdminTeamDto Team, int Days, IReadOnlyList<AdminTeamMemberDto> Members);

    public sealed record AdminUserTeamDto(int TeamId, string TeamName, bool IsOwner, string? RoleName, DateTime JoinedAtUtc, bool TeamDisabled);

    /// <param name="RecentActions">Counted actions (added, edited, …) in the last 7 days, all teams.</param>
    /// <param name="DiskBytes">Files of entries they added, all teams.</param>
    public sealed record AdminUserDto(int Id, string Username, DateTime CreatedAtUtc, DateTime? LastLoginAtUtc, DateTime? LastActiveAtUtc,
        DateTime? DisabledAtUtc, DateTime? LockedUntilUtc, IReadOnlyList<AdminUserTeamDto> Teams, int RecentActions, int EntriesAdded, long DiskBytes);

    public sealed record AdminUserTeamActivityDto(int TeamId, string TeamName, IReadOnlyDictionary<string, int> Counts, int EntriesAdded, long DiskBytes);

    public sealed record AdminUserDetailDto(AdminUserDto User, int Days, IReadOnlyList<AdminUserTeamActivityDto> Activity);

    public sealed record SetDisabledRequest(bool Disabled);

    public sealed record AdminStaffDto(int Id, string Username, bool IsOwner, IReadOnlyList<string> Permissions, DateTime CreatedAtUtc, DateTime? LastLoginAtUtc,
        DateTime? DisabledAtUtc);

    public sealed record SetStaffPermissionsRequest(List<string>? Permissions);

    public sealed record AdminAuditEventDto(long Id, DateTime TimestampUtc, string Action, int? EntryId, int? TeamId, string? TeamName, string? UserId,
        string? Username, int? StaffId, string? StaffUsername, string? Details);

    public sealed record StartSupportRequest(int TeamId);

    /// <param name="Kind">"registration" (new team) or "ownership" (owner of TeamId).</param>
    public sealed record CreateStaffLinkRequest(string? Kind, int? TeamId, string? Note);

    /// <summary>
    /// The app staff API (/admin.html). Staff use their own cookie and tables; a staff login is never a team login. The app Owner sets up
    /// their account from the machine running the app and is the only one who manages app moderators (invite links, permissions, disabling,
    /// password resets). Every other endpoint declares the staff permission it needs (<see cref="StaffScope"/>, fail closed).
    /// </summary>
    public static class AdminEndpoints
    {
        private static readonly TimeSpan ClosedLinkListTime = TimeSpan.FromDays(30);
        private static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromHours(24);
        private const int RecentActionDays = 7;

        public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
        {
            var open = app.MapGroup("/api/admin").AllowWithoutStaff();
            open.MapGet("/setup", GetSetup);
            open.MapPost("/setup", Setup).RequireRateLimiting(AuthSchemes.LoginRateLimit);
            open.MapPost("/login", Login).RequireRateLimiting(AuthSchemes.LoginRateLimit);
            open.MapPost("/logout", Logout);
            // Ending a support visit only clears its cookie, so it works even after the staff login is gone
            open.MapDelete("/support", EndSupport);

            // StaffScope (middleware) checks the staff login and each endpoint's declared permission
            var staff = app.MapGroup("/api/admin");
            staff.MapGet("/me", Me).RequireAnyStaff();
            staff.MapPost("/password", ChangePassword).RequireAnyStaff().RequireRateLimiting(AuthSchemes.LoginRateLimit);

            staff.MapGet("/teams", ListTeams)
                .RequireStaff(StaffPermission.ViewTeamsAndUsers | StaffPermission.ManageTeamLinks | StaffPermission.DisableTeams | StaffPermission.ViewAsSupport);
            staff.MapGet("/teams/{id:int}", GetTeam).RequireStaff(StaffPermission.ViewTeamsAndUsers);
            staff.MapPut("/teams/{id:int}/disabled", SetTeamDisabled).RequireStaff(StaffPermission.DisableTeams);

            staff.MapGet("/users", ListUsers)
                .RequireStaff(StaffPermission.ViewTeamsAndUsers | StaffPermission.ResetUserPasswords | StaffPermission.DisableUsers);
            staff.MapGet("/users/{id:int}", GetUser).RequireStaff(StaffPermission.ViewTeamsAndUsers);
            staff.MapPut("/users/{id:int}/disabled", SetUserDisabled).RequireStaff(StaffPermission.DisableUsers);
            staff.MapPost("/users/{id:int}/password-reset", CreateUserResetLink).RequireStaff(StaffPermission.ResetUserPasswords);

            staff.MapGet("/links", ListLinks).RequireStaff(StaffPermission.ManageTeamLinks | StaffPermission.ResetUserPasswords);
            staff.MapPost("/links", CreateLink).RequireStaff(StaffPermission.ManageTeamLinks);
            staff.MapDelete("/links/{id:int}", RevokeLink).RequireStaff(StaffPermission.ManageTeamLinks | StaffPermission.ResetUserPasswords);

            staff.MapGet("/audit", GetAudit).RequireStaff(StaffPermission.ViewAuditLog);
            staff.MapPost("/support", StartSupport).RequireStaff(StaffPermission.ViewAsSupport);

            staff.MapGet("/staff", ListStaff).RequireStaffOwner();
            staff.MapPost("/staff/invites", CreateStaffInvite).RequireStaffOwner();
            staff.MapPut("/staff/{id:int}/permissions", SetStaffPermissions).RequireStaffOwner();
            staff.MapPut("/staff/{id:int}/disabled", SetStaffDisabled).RequireStaffOwner();
            staff.MapPost("/staff/{id:int}/password-reset", CreateStaffResetLink).RequireStaffOwner();
            return app;
        }

        // ---------- setup, login, own account ----------

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
            SupportView.End(context);
            return Results.NoContent();
        }

        private static IResult Me(StaffContext staff) =>
            Results.Ok(new StaffMeDto(staff.Id, staff.Username, staff.IsOwner, StaffPermissions.ToKeys(staff.Permissions),
                StaffPermissions.Catalog.Select(x => x.Info).ToList()));

        /// <summary>Needs the current password. Signs out the account's other sessions and keeps this one.</summary>
        private static async Task<IResult> ChangePassword(ChangePasswordRequest request, StaffContext me, HttpContext context, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            if (AccountRules.PasswordError(request.NewPassword) is { } passwordError)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [passwordError] });

            var staff = await db.StaffAccounts.FirstAsync(x => x.Id == me.Id, cancellationToken);
            var outcome = LoginCheck.Check(staff, request.CurrentPassword ?? "", DateTime.UtcNow);
            if (outcome != LoginOutcome.Success)
            {
                await db.SaveChangesAsync(cancellationToken);
                return outcome == LoginOutcome.WrongPassword
                    ? Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Your current password is wrong."] })
                    : LoginCheck.FailureResult(outcome);
            }

            SetPassword(staff, request.NewPassword!);
            db.Audit(AuditActions.StaffPasswordChanged, null, staff.Username);
            await db.SaveChangesAsync(cancellationToken);
            await SignInAsync(context, staff);
            return Results.NoContent();
        }

        internal static void SetPassword(StaffAccount staff, string password)
        {
            staff.PasswordHash = AccountRules.HashPassword(password);
            staff.SecurityStamp = AccountRules.NewSecurityStamp();
            staff.FailedLoginCount = 0;
            staff.LockedUntilUtc = null;
        }

        // ---------- teams ----------

        private static async Task<List<AdminTeamDto>> TeamDtosAsync(LibraryDbContext db, ShortformService shortform, int? onlyTeamId,
            CancellationToken cancellationToken)
        {
            var query = db.Teams.AsNoTracking();
            if (onlyTeamId is { } id)
                query = query.Where(x => x.Id == id);
            var teams = await query.OrderBy(x => x.Name)
                .Select(x => new
                {
                    x.Id, x.Name, x.CreatedAtUtc, x.OwnerUserId, OwnerUsername = x.OwnerUser!.Username, MemberCount = x.Members.Count,
                    EntryCount = db.Entries.IgnoreQueryFilters().Count(e => e.TeamId == x.Id), x.DisabledAtUtc, x.AutoSaveAudio,
                })
                .ToListAsync(cancellationToken);
            var lastActive = (await db.TeamMembers.AsNoTracking().Select(x => new { x.TeamId, x.User!.LastActiveAtUtc }).ToListAsync(cancellationToken))
                .GroupBy(x => x.TeamId).ToDictionary(x => x.Key, x => x.Max(m => m.LastActiveAtUtc));
            var disk = await ActivityReport.TeamDiskAsync(db, shortform, cancellationToken);
            return teams.Select(x => new AdminTeamDto(x.Id, x.Name, x.CreatedAtUtc, x.OwnerUserId, x.OwnerUsername, x.MemberCount, x.EntryCount,
                x.DisabledAtUtc, x.AutoSaveAudio, disk.GetValueOrDefault(x.Id), lastActive.GetValueOrDefault(x.Id))).ToList();
        }

        private static async Task<IResult> ListTeams(LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken) =>
            Results.Ok(await TeamDtosAsync(db, shortform, null, cancellationToken));

        /// <summary>One team's members with what each did in the last <paramref name="days"/> days and their disk use there.</summary>
        private static async Task<IResult> GetTeam(int id, int? days, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            var team = (await TeamDtosAsync(db, shortform, id, cancellationToken)).FirstOrDefault();
            if (team is null)
                return Results.Problem(detail: "Team not found.", statusCode: StatusCodes.Status404NotFound);

            var period = Math.Clamp(days ?? 7, 1, 365);
            var members = await db.TeamMembers.AsNoTracking().Where(x => x.TeamId == id).OrderBy(x => x.JoinedAtUtc)
                .Select(x => new
                {
                    x.UserId, x.User!.Username, RoleName = x.Role == null ? null : x.Role.Name, x.JoinedAtUtc, x.User.LastActiveAtUtc, x.User.DisabledAtUtc,
                    OtherTeams = db.TeamMembers.Count(o => o.UserId == x.UserId && o.TeamId != id),
                })
                .ToListAsync(cancellationToken);
            var activity = await ActivityReport.ComputeAsync(db, shortform, DateTime.UtcNow.AddDays(-period), id, null, cancellationToken);

            return Results.Ok(new AdminTeamDetailDto(team, period, members.Select(m =>
            {
                var a = activity.GetValueOrDefault((id, m.UserId.ToString())) ?? UserActivity.Empty;
                return new AdminTeamMemberDto(m.UserId, m.Username, m.UserId == team.OwnerUserId, m.RoleName, m.JoinedAtUtc, m.LastActiveAtUtc, m.DisabledAtUtc,
                    m.OtherTeams, a.Counts, a.EntriesAdded, a.DiskBytes);
            }).ToList()));
        }

        private static async Task<IResult> SetTeamDisabled(int id, SetDisabledRequest request, StaffContext me, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var team = await db.Teams.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (team is null)
                return Results.Problem(detail: "Team not found.", statusCode: StatusCodes.Status404NotFound);

            if ((team.DisabledAtUtc is not null) != request.Disabled)
            {
                team.DisabledAtUtc = request.Disabled ? DateTime.UtcNow : null;
                db.Audit(request.Disabled ? AuditActions.TeamDisabled : AuditActions.TeamEnabled, null, $"\"{team.Name}\" by {me.Username}", teamId: team.Id);
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        // ---------- users ----------

        private static async Task<List<AdminUserDto>> UserDtosAsync(LibraryDbContext db, ShortformService shortform, string? search, int? onlyUserId,
            CancellationToken cancellationToken)
        {
            var query = db.Users.AsNoTracking();
            if (onlyUserId is { } id)
                query = query.Where(x => x.Id == id);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
                query = query.Where(x => EF.Functions.Like(x.NormalizedUsername, pattern, "\\"));
            }

            var users = await query.OrderBy(x => x.NormalizedUsername).Take(200)
                .ToListAsync(cancellationToken);
            var userIds = users.Select(x => x.Id).ToList();
            var memberships = (await db.TeamMembers.AsNoTracking().Where(m => userIds.Contains(m.UserId)).OrderBy(m => m.JoinedAtUtc)
                    .Select(m => new
                    {
                        m.UserId,
                        Team = new AdminUserTeamDto(m.TeamId, m.Team!.Name, m.Team.OwnerUserId == m.UserId, m.Role == null ? null : m.Role.Name,
                            m.JoinedAtUtc, m.Team.DisabledAtUtc != null),
                    })
                    .ToListAsync(cancellationToken))
                .ToLookup(x => x.UserId, x => x.Team);

            var activity = await ActivityReport.ComputeAsync(db, shortform, DateTime.UtcNow.AddDays(-RecentActionDays), null, onlyUserId, cancellationToken);
            var byUser = activity.GroupBy(x => x.Key.UserId).ToDictionary(x => x.Key, x => (
                Actions: x.Sum(a => a.Value.Counts.Values.Sum()), Added: x.Sum(a => a.Value.EntriesAdded), Disk: x.Sum(a => a.Value.DiskBytes)));

            return users.Select(x =>
            {
                var a = byUser.GetValueOrDefault(x.Id.ToString());
                return new AdminUserDto(x.Id, x.Username, x.CreatedAtUtc, x.LastLoginAtUtc, x.LastActiveAtUtc, x.DisabledAtUtc,
                    x.LockedUntilUtc > DateTime.UtcNow ? x.LockedUntilUtc : null, memberships[x.Id].ToList(), a.Actions, a.Added, a.Disk);
            }).ToList();
        }

        private static async Task<IResult> ListUsers(string? search, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken) =>
            Results.Ok(await UserDtosAsync(db, shortform, search, null, cancellationToken));

        /// <summary>One user with what they did in each team in the last <paramref name="days"/> days and their disk use there.</summary>
        private static async Task<IResult> GetUser(int id, int? days, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            var user = (await UserDtosAsync(db, shortform, null, id, cancellationToken)).FirstOrDefault();
            if (user is null)
                return Results.Problem(detail: "User not found.", statusCode: StatusCodes.Status404NotFound);

            var period = Math.Clamp(days ?? 7, 1, 365);
            var activity = await ActivityReport.ComputeAsync(db, shortform, DateTime.UtcNow.AddDays(-period), null, id, cancellationToken);
            // Their teams, plus teams they left but still have entries or actions in
            var teamIds = user.Teams.Select(x => x.TeamId).Concat(activity.Keys.Select(x => x.TeamId)).Distinct().ToList();
            var names = await db.Teams.AsNoTracking().Where(x => teamIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

            var rows = teamIds.Select(t =>
            {
                var a = activity.GetValueOrDefault((t, id.ToString())) ?? UserActivity.Empty;
                return new AdminUserTeamActivityDto(t, names.GetValueOrDefault(t) ?? $"Team {t}", a.Counts, a.EntriesAdded, a.DiskBytes);
            }).ToList();
            return Results.Ok(new AdminUserDetailDto(user, period, rows));
        }

        /// <summary>Disabling also changes the security stamp, so every session of the account ends at once.</summary>
        private static async Task<IResult> SetUserDisabled(int id, SetDisabledRequest request, StaffContext me, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (user is null)
                return Results.Problem(detail: "User not found.", statusCode: StatusCodes.Status404NotFound);

            if ((user.DisabledAtUtc is not null) != request.Disabled)
            {
                user.DisabledAtUtc = request.Disabled ? DateTime.UtcNow : null;
                if (request.Disabled)
                    user.SecurityStamp = AccountRules.NewSecurityStamp();
                db.Audit(request.Disabled ? AuditActions.UserDisabled : AuditActions.UserEnabled, null, $"{user.Username} by {me.Username}");
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        /// <summary>Any user, including people in several teams (which a team can't reset). Lives 24 hours.</summary>
        private static async Task<IResult> CreateUserResetLink(int id, StaffContext me, HttpRequest httpRequest, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (user is null)
                return Results.Problem(detail: "User not found.", statusCode: StatusCodes.Status404NotFound);

            var now = DateTime.UtcNow;
            var (link, token) = LinkUrls.New(AccountLinkKind.PasswordReset, null, $"Password reset for {user.Username}", now);
            link.ExpiresAtUtc = now + ResetLinkLifetime;
            link.TargetUserId = user.Id;
            link.CreatedByStaffId = me.Id;
            db.AccountLinks.Add(link);
            db.Audit(AuditActions.PasswordResetLinkCreated, null, $"For {user.Username} by staff {me.Username}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/users/{id}/password-reset", LinkUrls.ToDto(link, null, LinkUrls.Build(httpRequest, token)));
        }

        // ---------- links ----------

        /// <summary>Which staff-made link kinds this staff account may see and revoke.</summary>
        private static bool MayHandle(StaffContext staff, AccountLinkKind kind) => kind switch
        {
            AccountLinkKind.TeamRegistration or AccountLinkKind.TeamOwnership => staff.Can(StaffPermission.ManageTeamLinks),
            AccountLinkKind.PasswordReset => staff.Can(StaffPermission.ResetUserPasswords),
            AccountLinkKind.StaffInvite or AccountLinkKind.StaffPasswordReset => staff.IsOwner,
            _ => false,
        };

        private static async Task<IResult> ListLinks(StaffContext me, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var since = DateTime.UtcNow - ClosedLinkListTime;
            var links = (await db.AccountLinks.AsNoTracking().Include(x => x.Team)
                    .Where(x => x.CreatedByStaffId != null && x.CreatedAtUtc > since)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .ToListAsync(cancellationToken))
                .Where(x => MayHandle(me, x.Kind))
                .ToList();
            var users = await TeamEndpoints.UsernamesAsync(db, links, cancellationToken);
            var staffIds = links.Where(x => x.UsedByStaffId is not null).Select(x => x.UsedByStaffId!.Value).Distinct().ToList();
            var staffNames = await db.StaffAccounts.AsNoTracking().Where(x => staffIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
            return Results.Ok(links.Select(x => LinkUrls.ToDto(x,
                x.UsedByStaffId is { } s ? staffNames.GetValueOrDefault(s) : x.UsedByUserId is { } u ? users.GetValueOrDefault(u) : null)));
        }

        private static async Task<IResult> CreateLink(CreateStaffLinkRequest request, StaffContext me, HttpContext context, LibraryDbContext db,
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
            link.CreatedByStaffId = me.Id;
            db.AccountLinks.Add(link);
            await db.SaveChangesAsync(cancellationToken);

            db.Audit(AuditActions.LinkCreated, null,
                $"{kind} link {link.Id}{(team is null ? "" : $" for \"{team.Name}\"")}{(link.Note is null ? "" : $" ({link.Note})")}", teamId: team?.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/links/{link.Id}", LinkUrls.ToDto(link, null, LinkUrls.Build(context.Request, token)));
        }

        private static async Task<IResult> RevokeLink(int id, StaffContext me, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var link = await db.AccountLinks.FirstOrDefaultAsync(x => x.Id == id && x.CreatedByStaffId != null, cancellationToken);
            if (link is null || !MayHandle(me, link.Kind))
                return Results.Problem(detail: "Link not found.", statusCode: StatusCodes.Status404NotFound);

            if (link.UsedAtUtc is null && link.RevokedAtUtc is null)
            {
                link.RevokedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.LinkRevoked, null, $"{link.Kind} link {link.Id}", teamId: link.TeamId);
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        // ---------- app log ----------

        /// <summary>Every audit event, newest first, optionally for one team, user or staff account. Page with <paramref name="before"/>.</summary>
        private static async Task<IResult> GetAudit(int? teamId, int? userId, int? staffId, long? before, int? limit, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var query = db.AuditEvents.AsNoTracking();
            if (teamId is { } t)
                query = query.Where(x => x.TeamId == t);
            if (userId is { } u)
            {
                var text = u.ToString();
                query = query.Where(x => x.UserId == text);
            }
            if (staffId is { } s)
                query = query.Where(x => x.StaffId == s);
            if (before is { } b)
                query = query.Where(x => x.Id < b);

            var events = await query.OrderByDescending(x => x.Id).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(cancellationToken);
            var users = await EntryEndpoints.UsernamesAsync(db, events.Select(x => x.UserId), cancellationToken);
            var staffIds = events.Where(x => x.StaffId is not null).Select(x => x.StaffId!.Value).Distinct().ToList();
            var staffNames = await db.StaffAccounts.AsNoTracking().Where(x => staffIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
            var teamIds = events.Where(x => x.TeamId is not null).Select(x => x.TeamId!.Value).Distinct().ToList();
            var teamNames = await db.Teams.AsNoTracking().Where(x => teamIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

            return Results.Ok(events.Select(x => new AdminAuditEventDto(x.Id, x.TimestampUtc, x.Action, x.EntryId, x.TeamId,
                x.TeamId is { } ti ? teamNames.GetValueOrDefault(ti) : null, x.UserId, x.UserId is { } ui ? users.GetValueOrDefault(ui) : null,
                x.StaffId, x.StaffId is { } si ? staffNames.GetValueOrDefault(si) : null, x.Details)));
        }

        // ---------- view as support ----------

        private static async Task<IResult> StartSupport(StartSupportRequest request, StaffContext me, HttpContext context, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.TeamId, cancellationToken);
            if (team is null)
                return Results.Problem(detail: "Team not found.", statusCode: StatusCodes.Status404NotFound);

            SupportView.Start(context, team.Id);
            db.Audit(AuditActions.SupportViewStarted, null, $"{me.Username} viewing \"{team.Name}\"", teamId: team.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        private static async Task<IResult> EndSupport(HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (int.TryParse(context.Request.Cookies[SupportView.Cookie], out var teamId) && await StaffContext.FromRequestAsync(context, db) is { } staff)
            {
                db.Audit(AuditActions.SupportViewEnded, null, staff.Username, teamId: teamId, staffId: staff.Id);
                await db.SaveChangesAsync(cancellationToken);
            }
            SupportView.End(context);
            return Results.NoContent();
        }

        // ---------- app moderators (Owner only) ----------

        private static async Task<IResult> ListStaff(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var staff = await db.StaffAccounts.AsNoTracking().OrderByDescending(x => x.IsOwner).ThenBy(x => x.NormalizedUsername).ToListAsync(cancellationToken);
            return Results.Ok(staff.Select(x => new AdminStaffDto(x.Id, x.Username, x.IsOwner,
                StaffPermissions.ToKeys(x.IsOwner ? StaffPermission.All : x.Permissions), x.CreatedAtUtc, x.LastLoginAtUtc, x.DisabledAtUtc)));
        }

        /// <summary>A one-time link that makes a new moderator account. Like team invites, it never carries permissions: the Owner picks them after.</summary>
        private static async Task<IResult> CreateStaffInvite(CreateInviteRequest? request, StaffContext me, HttpRequest httpRequest, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var (link, token) = LinkUrls.New(AccountLinkKind.StaffInvite, null, request?.Note, DateTime.UtcNow);
            link.CreatedByStaffId = me.Id;
            db.AccountLinks.Add(link);
            await db.SaveChangesAsync(cancellationToken);

            db.Audit(AuditActions.LinkCreated, null, $"Moderator invite link {link.Id}{(link.Note is null ? "" : $" ({link.Note})")}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/links/{link.Id}", LinkUrls.ToDto(link, null, LinkUrls.Build(httpRequest, token)));
        }

        private static async Task<(StaffAccount? Staff, IResult? Error)> LoadModeratorAsync(int id, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var staff = await db.StaffAccounts.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (staff is null)
                return (null, Results.Problem(detail: "Staff account not found.", statusCode: StatusCodes.Status404NotFound));
            if (staff.IsOwner)
                return (null, Results.Problem(detail: "The Owner account can't be changed here.", statusCode: StatusCodes.Status403Forbidden));
            return (staff, null);
        }

        private static async Task<IResult> SetStaffPermissions(int id, SetStaffPermissionsRequest request, StaffContext me, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            if (StaffPermissions.FromKeys(request.Permissions) is not { } permissions)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["permissions"] = ["Unknown permission."] });

            var (staff, error) = await LoadModeratorAsync(id, db, cancellationToken);
            if (error is not null)
                return error;

            if (staff!.Permissions != permissions)
            {
                staff.Permissions = permissions;
                db.Audit(AuditActions.StaffPermissionsChanged, null,
                    $"{staff.Username}: {(permissions == StaffPermission.None ? "none" : string.Join(", ", StaffPermissions.ToKeys(permissions)))} (by {me.Username})");
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        private static async Task<IResult> SetStaffDisabled(int id, SetDisabledRequest request, StaffContext me, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var (staff, error) = await LoadModeratorAsync(id, db, cancellationToken);
            if (error is not null)
                return error;

            if ((staff!.DisabledAtUtc is not null) != request.Disabled)
            {
                staff.DisabledAtUtc = request.Disabled ? DateTime.UtcNow : null;
                if (request.Disabled)
                    staff.SecurityStamp = AccountRules.NewSecurityStamp();
                db.Audit(request.Disabled ? AuditActions.StaffDisabled : AuditActions.StaffEnabled, null, $"{staff.Username} by {me.Username}");
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        private static async Task<IResult> CreateStaffResetLink(int id, StaffContext me, HttpRequest httpRequest, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var (staff, error) = await LoadModeratorAsync(id, db, cancellationToken);
            if (error is not null)
                return error;

            var now = DateTime.UtcNow;
            var (link, token) = LinkUrls.New(AccountLinkKind.StaffPasswordReset, null, $"Password reset for {staff!.Username}", now);
            link.ExpiresAtUtc = now + ResetLinkLifetime;
            link.TargetStaffId = staff.Id;
            link.CreatedByStaffId = me.Id;
            db.AccountLinks.Add(link);
            db.Audit(AuditActions.StaffPasswordResetLinkCreated, null, $"For {staff.Username} by {me.Username}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/staff/{id}/password-reset", LinkUrls.ToDto(link, null, LinkUrls.Build(httpRequest, token)));
        }

        public static Task SignInAsync(HttpContext context, StaffAccount staff) =>
            context.SignInAsync(AuthSchemes.Staff, AuthSetup.Principal(AuthSchemes.Staff, staff.Id, staff.Username, staff.SecurityStamp), AuthSetup.PersistentSession());
    }
}
