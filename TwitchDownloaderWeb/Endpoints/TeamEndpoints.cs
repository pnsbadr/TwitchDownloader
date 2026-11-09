using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record TeamMemberDto(int UserId, string Username, bool IsOwner, DateTime JoinedAtUtc, DateTime? LastActiveAtUtc);

    public sealed record TeamDto(int Id, string Name, int? OwnerUserId, IReadOnlyList<TeamMemberDto> Members);

    /// <summary>The current team's members and invite links. Team owner only for now (TeamScope lets nobody else past until roles exist).</summary>
    public static class TeamEndpoints
    {
        /// <summary>Used/revoked/expired invites stay listed this long so the owner can see who used which.</summary>
        private static readonly TimeSpan ClosedLinkListTime = TimeSpan.FromDays(30);

        public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
        {
            var team = app.MapGroup("/api/team");
            team.MapGet("/", GetTeam);
            team.MapDelete("/members/{userId:int}", RemoveMember);
            team.MapGet("/invites", ListInvites);
            team.MapPost("/invites", CreateInvite);
            team.MapDelete("/invites/{id:int}", RevokeInvite);
            return app;
        }

        private static async Task<IResult> GetTeam(TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var row = await db.Teams.AsNoTracking().FirstAsync(x => x.Id == team.TeamId, cancellationToken);
            var members = await db.TeamMembers.AsNoTracking().Where(x => x.TeamId == team.TeamId)
                .OrderBy(x => x.JoinedAtUtc)
                .Select(x => new TeamMemberDto(x.UserId, x.User!.Username, x.UserId == row.OwnerUserId, x.JoinedAtUtc, x.User.LastActiveAtUtc))
                .ToListAsync(cancellationToken);
            return Results.Ok(new TeamDto(row.Id, row.Name, row.OwnerUserId, members));
        }

        private static async Task<IResult> RemoveMember(int userId, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var member = await db.TeamMembers.Include(x => x.User).FirstOrDefaultAsync(x => x.TeamId == team.TeamId && x.UserId == userId, cancellationToken);
            if (member is null)
                return Results.Problem(detail: "That person isn't in this team.", statusCode: StatusCodes.Status404NotFound);

            if (await db.Teams.AnyAsync(x => x.Id == team.TeamId && x.OwnerUserId == userId, cancellationToken))
                return Results.Problem(detail: "The team owner can't be removed.", statusCode: StatusCodes.Status409Conflict);

            db.TeamMembers.Remove(member);
            db.Audit(AuditActions.TeamMemberRemoved, null, $"{member.User!.Username} removed from \"{team.TeamName}\" by {team.Username}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

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
    }
}
