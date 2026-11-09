using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record TeamMemberDto(int UserId, string Username, bool IsOwner, int? RoleId, string? RoleName, DateTime JoinedAtUtc, DateTime? LastActiveAtUtc,
        bool CanManage);

    public sealed record TeamRoleDto(int Id, string Name, IReadOnlyList<string> Permissions, int MemberCount, bool CanEdit);

    /// <param name="MyPermissions">What the caller may do here, so the page only shows what works.</param>
    public sealed record TeamDto(int Id, string Name, int? OwnerUserId, IReadOnlyList<string> MyPermissions, IReadOnlyList<TeamMemberDto> Members,
        IReadOnlyList<TeamRoleDto> Roles, IReadOnlyList<TeamPermissionInfo> PermissionCatalog);

    public sealed record SaveRoleRequest(string? Name, List<string>? Permissions);

    public sealed record AssignRoleRequest(int? RoleId);

    public sealed record TeamSettingsDto(bool AutoSaveAudio, bool AutoSaveAllowedByApp);

    public sealed record UpdateTeamSettingsRequest(bool AutoSaveAudio);

    /// <param name="Counts">Actions in the period, by kind: added, edited, deleted, downloaded, generated, vertical.</param>
    /// <param name="DiskBytes">Files of entries they added that are on disk now: stored, on-demand and vertical renders.</param>
    public sealed record MemberActivityDto(int UserId, string Username, string? RoleName, bool IsOwner, DateTime JoinedAtUtc, DateTime? LastActiveAtUtc,
        IReadOnlyDictionary<string, int> Counts, int EntriesAdded, long DiskBytes);

    public sealed record TeamActivityDto(int Days, DateTime SinceUtc, IReadOnlyList<MemberActivityDto> Members);

    public sealed record TeamAuditEventDto(long Id, DateTime TimestampUtc, string Action, int? EntryId, string? EntryTitle, string? UserId, string? Username,
        string? Details);

    /// <summary>
    /// The current team's Team page: members, roles, invite links, activity, the team log and team settings. Each action needs its own
    /// permission, and the guard rails keep a member from granting more than they have: they can only give roles (and edit roles) whose
    /// permissions they hold themselves, never touch their own role or the team owner, and only manage members whose role is within theirs.
    /// </summary>
    public static class TeamEndpoints
    {
        /// <summary>Used/revoked/expired invites stay listed this long so the owner can see who used which.</summary>
        private static readonly TimeSpan ClosedLinkListTime = TimeSpan.FromDays(30);
        private static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromHours(24);
        private const int MaxRoleNameLength = 50;
        private const int MaxRolesPerTeam = 30;

        public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
        {
            var team = app.MapGroup("/api/team");
            team.MapGet("/", GetTeam).RequireTeam(TeamPermission.TeamPage);
            team.MapGet("/settings", GetSettings).RequireTeam(TeamPermission.All);
            team.MapPut("/settings", UpdateSettings).RequireTeam(TeamPermission.ManageAudioSettings);

            team.MapDelete("/members/{userId:int}", RemoveMember).RequireTeam(TeamPermission.RemoveMembers);
            team.MapPut("/members/{userId:int}/role", AssignRole).RequireTeam(TeamPermission.AssignRoles);
            team.MapPost("/members/{userId:int}/password-reset", CreateResetLink).RequireTeam(TeamPermission.ResetPasswords);

            team.MapPost("/roles", CreateRole).RequireTeam(TeamPermission.ManageRoles);
            team.MapPut("/roles/{id:int}", UpdateRole).RequireTeam(TeamPermission.ManageRoles);
            team.MapDelete("/roles/{id:int}", DeleteRole).RequireTeam(TeamPermission.ManageRoles);

            team.MapGet("/invites", ListInvites).RequireTeam(TeamPermission.InviteMembers);
            team.MapPost("/invites", CreateInvite).RequireTeam(TeamPermission.InviteMembers);
            team.MapDelete("/invites/{id:int}", RevokeInvite).RequireTeam(TeamPermission.InviteMembers);

            team.MapGet("/activity", GetActivity).RequireTeam(TeamPermission.ViewActivity);
            team.MapGet("/audit", GetAudit).RequireTeam(TeamPermission.ViewActivity);
            return app;
        }

        // ---------- members and roles ----------

        private static async Task<IResult> GetTeam(TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var row = await db.Teams.AsNoTracking().FirstAsync(x => x.Id == team.TeamId, cancellationToken);
            var roles = await db.TeamRoles.AsNoTracking().Where(x => x.TeamId == team.TeamId).OrderBy(x => x.Name).ToListAsync(cancellationToken);
            var members = await db.TeamMembers.AsNoTracking().Where(x => x.TeamId == team.TeamId)
                .OrderBy(x => x.JoinedAtUtc)
                .Select(x => new { x.UserId, x.User!.Username, x.RoleId, x.JoinedAtUtc, x.User.LastActiveAtUtc })
                .ToListAsync(cancellationToken);
            var roleById = roles.ToDictionary(x => x.Id);

            var memberDtos = members.Select(x =>
            {
                var role = x.RoleId is { } id ? roleById.GetValueOrDefault(id) : null;
                var isOwner = x.UserId == row.OwnerUserId;
                return new TeamMemberDto(x.UserId, x.Username, isOwner, x.RoleId, role?.Name, x.JoinedAtUtc, x.LastActiveAtUtc,
                    CanManage(team, x.UserId, isOwner, role?.Permissions ?? TeamPermission.None));
            }).ToList();

            var myRoleId = members.FirstOrDefault(x => x.UserId == team.UserId)?.RoleId;
            var roleDtos = roles.Select(x => new TeamRoleDto(x.Id, x.Name, TeamPermissions.ToKeys(x.Permissions), members.Count(m => m.RoleId == x.Id),
                CanEditRole(team, x, myRoleId))).ToList();

            return Results.Ok(new TeamDto(row.Id, row.Name, row.OwnerUserId, TeamPermissions.ToKeys(team.Permissions), memberDtos, roleDtos,
                TeamPermissions.Catalog.Select(x => x.Info).ToList()));
        }

        /// <summary>Not the owner, not yourself, and their role gives nothing you don't have (the owner can manage everyone else).</summary>
        private static bool CanManage(TeamContext team, int userId, bool isOwner, TeamPermission theirPermissions) =>
            !team.IsSupport && !isOwner && userId != team.UserId && (team.IsOwner || TeamPermissions.IsWithin(theirPermissions, team.Permissions));

        /// <summary>Not your own role (you'd be changing your own access), and nothing in it you don't have.</summary>
        private static bool CanEditRole(TeamContext team, TeamRole role, int? myRoleId) =>
            !team.IsSupport && (team.IsOwner || (role.Id != myRoleId && TeamPermissions.IsWithin(role.Permissions, team.Permissions)));

        /// <summary>Loads a member of the current team with their role, and checks the caller may manage them.</summary>
        private static async Task<(TeamMember? Member, IResult? Error)> LoadManageableAsync(TeamContext team, int userId, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var member = await db.TeamMembers.Include(x => x.User).Include(x => x.Role)
                .FirstOrDefaultAsync(x => x.TeamId == team.TeamId && x.UserId == userId, cancellationToken);
            if (member is null)
                return (null, Results.Problem(detail: "That person isn't in this team.", statusCode: StatusCodes.Status404NotFound));

            var isOwner = await db.Teams.AnyAsync(x => x.Id == team.TeamId && x.OwnerUserId == userId, cancellationToken);
            if (isOwner)
                return (null, Forbidden("The team owner can't be changed by members."));
            if (userId == team.UserId)
                return (null, Forbidden("You can't change your own access."));
            if (!CanManage(team, userId, false, member.Role?.Permissions ?? TeamPermission.None))
                return (null, Forbidden($"{member.User!.Username}'s role has permissions you don't have."));

            return (member, null);
        }

        private static async Task<IResult> RemoveMember(int userId, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (member, error) = await LoadManageableAsync(team, userId, db, cancellationToken);
            if (error is not null)
                return error;

            db.TeamMembers.Remove(member!);
            db.Audit(AuditActions.TeamMemberRemoved, null, $"{member!.User!.Username} removed from \"{team.TeamName}\" by {team.Username}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        private static async Task<IResult> AssignRole(int userId, AssignRoleRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var (member, error) = await LoadManageableAsync(team, userId, db, cancellationToken);
            if (error is not null)
                return error;

            TeamRole? role = null;
            if (request.RoleId is { } roleId)
            {
                role = await db.TeamRoles.FirstOrDefaultAsync(x => x.Id == roleId && x.TeamId == team.TeamId, cancellationToken);
                if (role is null)
                    return Results.Problem(detail: "That role doesn't exist.", statusCode: StatusCodes.Status404NotFound);
                if (!team.IsOwner && !TeamPermissions.IsWithin(role.Permissions, team.Permissions))
                    return Forbidden($"\"{role.Name}\" has permissions you don't have, so you can't give it.");
            }

            if (member!.RoleId != role?.Id)
            {
                db.Audit(AuditActions.MemberRoleChanged, null,
                    $"{member.User!.Username}: {member.Role?.Name ?? "no role"} -> {role?.Name ?? "no role"} (by {team.Username})");
                member.RoleId = role?.Id;
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        private static async Task<IResult> CreateRole(SaveRoleRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (ValidateRole(request, team, out var error) is not var (name, permissions))
                return error!;

            if (await db.TeamRoles.CountAsync(x => x.TeamId == team.TeamId, cancellationToken) >= MaxRolesPerTeam)
                return Results.Problem(detail: $"A team can have at most {MaxRolesPerTeam} roles.", statusCode: StatusCodes.Status409Conflict);
            if (await db.TeamRoles.AnyAsync(x => x.TeamId == team.TeamId && x.Name == name, cancellationToken))
                return RoleNameTaken(name);

            var now = DateTime.UtcNow;
            var role = new TeamRole { TeamId = team.TeamId, Name = name, Permissions = permissions, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.TeamRoles.Add(role);
            db.Audit(AuditActions.RoleCreated, null, $"\"{name}\": {string.Join(", ", TeamPermissions.ToKeys(permissions))}");
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return RoleNameTaken(name);
            }
            return Results.Created($"/api/team/roles/{role.Id}", new TeamRoleDto(role.Id, role.Name, TeamPermissions.ToKeys(role.Permissions), 0, true));
        }

        private static async Task<IResult> UpdateRole(int id, SaveRoleRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (ValidateRole(request, team, out var error) is not var (name, permissions))
                return error!;

            var role = await db.TeamRoles.FirstOrDefaultAsync(x => x.Id == id && x.TeamId == team.TeamId, cancellationToken);
            if (role is null)
                return Results.Problem(detail: "That role doesn't exist.", statusCode: StatusCodes.Status404NotFound);

            var myRoleId = await MyRoleIdAsync(team, db, cancellationToken);
            if (!CanEditRole(team, role, myRoleId))
                return Forbidden(role.Id == myRoleId ? "You can't change your own role." : $"\"{role.Name}\" has permissions you don't have.");

            if (role.Name != name && await db.TeamRoles.AnyAsync(x => x.TeamId == team.TeamId && x.Name == name && x.Id != id, cancellationToken))
                return RoleNameTaken(name);

            if (role.Name != name || role.Permissions != permissions)
            {
                db.Audit(AuditActions.RoleChanged, null,
                    $"\"{role.Name}\"{(role.Name != name ? $" -> \"{name}\"" : "")}: {string.Join(", ", TeamPermissions.ToKeys(permissions))}");
                role.Name = name;
                role.Permissions = permissions;
                role.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        private static async Task<IResult> DeleteRole(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var role = await db.TeamRoles.FirstOrDefaultAsync(x => x.Id == id && x.TeamId == team.TeamId, cancellationToken);
            if (role is null)
                return Results.Problem(detail: "That role doesn't exist.", statusCode: StatusCodes.Status404NotFound);

            var myRoleId = await MyRoleIdAsync(team, db, cancellationToken);
            if (!CanEditRole(team, role, myRoleId))
                return Forbidden(role.Id == myRoleId ? "You can't delete your own role." : $"\"{role.Name}\" has permissions you don't have.");

            // Members with it are left with no role (no access), like new members
            await db.TeamMembers.Where(x => x.RoleId == id).ExecuteUpdateAsync(x => x.SetProperty(m => m.RoleId, (int?)null), cancellationToken);
            db.TeamRoles.Remove(role);
            db.Audit(AuditActions.RoleDeleted, null, $"\"{role.Name}\"");
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        private static Task<int?> MyRoleIdAsync(TeamContext team, LibraryDbContext db, CancellationToken cancellationToken) =>
            db.TeamMembers.Where(x => x.TeamId == team.TeamId && x.UserId == team.UserId).Select(x => x.RoleId).FirstOrDefaultAsync(cancellationToken);

        private static (string Name, TeamPermission Permissions)? ValidateRole(SaveRoleRequest request, TeamContext team, out IResult? error)
        {
            error = null;
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxRoleNameLength)
            {
                error = Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [$"Role names are 1–{MaxRoleNameLength} characters."] });
                return null;
            }

            if (TeamPermissions.FromKeys(request.Permissions) is not { } permissions)
            {
                error = Results.ValidationProblem(new Dictionary<string, string[]> { ["permissions"] = ["Unknown permission."] });
                return null;
            }

            if (!team.IsOwner && !TeamPermissions.IsWithin(permissions, team.Permissions))
            {
                error = Forbidden("You can only give a role permissions you have yourself.");
                return null;
            }

            return (name, permissions);
        }

        private static IResult RoleNameTaken(string name) =>
            Results.Problem(detail: $"There is already a role called \"{name}\".", statusCode: StatusCodes.Status409Conflict);

        private static IResult Forbidden(string detail) => Results.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden);

        // ---------- password reset links ----------

        /// <summary>
        /// A one-time link that sets a new password for a member. Only for members in no other team: the account is shared across teams,
        /// so one team must not be able to take over someone who also works for another (app staff handle those).
        /// </summary>
        private static async Task<IResult> CreateResetLink(int userId, TeamContext team, HttpRequest httpRequest, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var (member, error) = await LoadManageableAsync(team, userId, db, cancellationToken);
            if (error is not null)
                return error;

            if (await db.TeamMembers.AnyAsync(x => x.UserId == userId && x.TeamId != team.TeamId, cancellationToken))
                return Results.Problem(detail: $"{member!.User!.Username} is also in another team, so only app staff can reset their password.",
                    statusCode: StatusCodes.Status409Conflict);

            var now = DateTime.UtcNow;
            var (link, token) = LinkUrls.New(AccountLinkKind.PasswordReset, team.TeamId, $"Password reset for {member!.User!.Username}", now);
            link.ExpiresAtUtc = now + ResetLinkLifetime;
            link.TargetUserId = userId;
            link.CreatedByUserId = team.UserId;
            db.AccountLinks.Add(link);
            db.Audit(AuditActions.PasswordResetLinkCreated, null, $"For {member.User.Username} by {team.Username}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/team/members/{userId}/password-reset", LinkUrls.ToDto(link, null, LinkUrls.Build(httpRequest, token)));
        }

        // ---------- invites ----------

        private static async Task<IResult> ListInvites(TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var since = DateTime.UtcNow - ClosedLinkListTime;
            var links = await db.AccountLinks.AsNoTracking()
                .Where(x => x.TeamId == team.TeamId && x.Kind == AccountLinkKind.TeamInvite && x.CreatedAtUtc > since)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            var usedBy = await UsernamesAsync(db, links, cancellationToken);
            return Results.Ok(links.Select(x => LinkUrls.ToDto(x, x.UsedByUserId is { } id ? usedBy.GetValueOrDefault(id) : null)));
        }

        private static async Task<IResult> CreateInvite(CreateInviteRequest? request, TeamContext team, HttpRequest httpRequest, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            var (link, token) = LinkUrls.New(AccountLinkKind.TeamInvite, team.TeamId, request?.Note, DateTime.UtcNow);
            link.CreatedByUserId = team.UserId;
            db.AccountLinks.Add(link);
            await db.SaveChangesAsync(cancellationToken);

            db.Audit(AuditActions.LinkCreated, null, $"Invite link {link.Id} for \"{team.TeamName}\"{(link.Note is null ? "" : $" ({link.Note})")}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/team/invites/{link.Id}", LinkUrls.ToDto(link, null, LinkUrls.Build(httpRequest, token)));
        }

        private static async Task<IResult> RevokeInvite(int id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var link = await db.AccountLinks.FirstOrDefaultAsync(x => x.Id == id && x.TeamId == team.TeamId && x.Kind == AccountLinkKind.TeamInvite, cancellationToken);
            if (link is null)
                return Results.Problem(detail: "Invite link not found.", statusCode: StatusCodes.Status404NotFound);

            if (link.UsedAtUtc is null && link.RevokedAtUtc is null)
            {
                link.RevokedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.LinkRevoked, null, $"Invite link {link.Id} for \"{team.TeamName}\"");
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        internal static async Task<Dictionary<int, string>> UsernamesAsync(LibraryDbContext db, IEnumerable<AccountLink> links, CancellationToken cancellationToken)
        {
            var ids = links.Where(x => x.UsedByUserId is not null).Select(x => x.UsedByUserId!.Value).Distinct().ToList();
            return ids.Count == 0 ? [] : await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
        }

        // ---------- settings ----------

        private static async Task<IResult> GetSettings(TeamContext team, LibraryDbContext db, IOptions<AudioRescueSettings> rescue, CancellationToken cancellationToken)
        {
            var row = await db.Teams.AsNoTracking().FirstAsync(x => x.Id == team.TeamId, cancellationToken);
            return Results.Ok(new TeamSettingsDto(row.AutoSaveAudio, rescue.Value.Enabled));
        }

        private static async Task<IResult> UpdateSettings(UpdateTeamSettingsRequest request, TeamContext team, LibraryDbContext db,
            IOptions<AudioRescueSettings> rescue, Services.AudioRescue.AudioRescueService rescueService, CancellationToken cancellationToken)
        {
            var row = await db.Teams.FirstAsync(x => x.Id == team.TeamId, cancellationToken);
            if (row.AutoSaveAudio != request.AutoSaveAudio)
            {
                row.AutoSaveAudio = request.AutoSaveAudio;
                db.Audit(AuditActions.TeamSettingsChanged, null, $"Automatic audio saving {(request.AutoSaveAudio ? "on" : "off")} (by {team.Username})");
                await db.SaveChangesAsync(cancellationToken);
                rescueService.Wake();
            }
            return Results.Ok(new TeamSettingsDto(row.AutoSaveAudio, rescue.Value.Enabled));
        }

        // ---------- activity ----------

        /// <summary>Per member: when they were last active, what they did in the last <paramref name="days"/> days, and their disk use now.</summary>
        private static async Task<IResult> GetActivity(int? days, TeamContext team, LibraryDbContext db, ShortformService shortform,
            CancellationToken cancellationToken)
        {
            var period = Math.Clamp(days ?? 7, 1, 365);
            var since = DateTime.UtcNow.AddDays(-period);

            var row = await db.Teams.AsNoTracking().FirstAsync(x => x.Id == team.TeamId, cancellationToken);
            var members = await db.TeamMembers.AsNoTracking().Where(x => x.TeamId == team.TeamId).OrderBy(x => x.JoinedAtUtc)
                .Select(x => new { x.UserId, x.User!.Username, RoleName = x.Role == null ? null : x.Role.Name, x.JoinedAtUtc, x.User.LastActiveAtUtc })
                .ToListAsync(cancellationToken);

            var activity = await ActivityReport.ComputeAsync(db, shortform, since, team.TeamId, null, cancellationToken);
            var result = members.Select(m =>
            {
                var a = activity.GetValueOrDefault((team.TeamId, m.UserId.ToString())) ?? UserActivity.Empty;
                return new MemberActivityDto(m.UserId, m.Username, m.RoleName, m.UserId == row.OwnerUserId, m.JoinedAtUtc, m.LastActiveAtUtc, a.Counts,
                    a.EntriesAdded, a.DiskBytes);
            }).ToList();

            return Results.Ok(new TeamActivityDto(period, since, result));
        }

        /// <summary>The team's audit log, newest first; <paramref name="userId"/> narrows it to one member. Page with <paramref name="before"/> (an event ID).</summary>
        private static async Task<IResult> GetAudit(int? userId, long? before, int? limit, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            // What the team's members did; app staff actions (support visits included) are only in the staff log
            var query = db.AuditEvents.AsNoTracking().Where(x => x.TeamId == team.TeamId && x.StaffId == null);
            if (userId is { } uid)
            {
                var text = uid.ToString();
                query = query.Where(x => x.UserId == text);
            }
            if (before is { } b)
                query = query.Where(x => x.Id < b);

            var events = await query.OrderByDescending(x => x.Id).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(cancellationToken);

            var names = await EntryEndpoints.UsernamesAsync(db, events.Select(x => x.UserId), cancellationToken);
            var entryIds = events.Where(x => x.EntryId is not null).Select(x => x.EntryId!.Value).Distinct().ToList();
            var titles = await db.Entries.AsNoTracking().Where(x => entryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Title, cancellationToken);

            return Results.Ok(events.Select(x => new TeamAuditEventDto(x.Id, x.TimestampUtc, x.Action, x.EntryId,
                x.EntryId is { } e ? titles.GetValueOrDefault(e) : null, x.UserId, x.UserId is { } u ? names.GetValueOrDefault(u) : null, x.Details)));
        }
    }
}
