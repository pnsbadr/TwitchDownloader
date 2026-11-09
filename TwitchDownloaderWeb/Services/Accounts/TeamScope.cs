using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <summary>
    /// The logged-in user and the team a library request works in, with their permissions there. Put a <c>TeamContext team</c> parameter on
    /// an endpoint to get it; <see cref="TeamScope"/> has already checked the login, membership and the endpoint's required permission.
    /// </summary>
    public sealed record TeamContext(int UserId, string Username, int TeamId, string TeamName, bool IsOwner, TeamPermission Permissions)
    {
        private const string ItemKey = nameof(TeamContext);

        public static ValueTask<TeamContext?> BindAsync(HttpContext context, ParameterInfo parameter) =>
            ValueTask.FromResult(context.Items[ItemKey] as TeamContext
                                 ?? throw new InvalidOperationException("TeamContext used on an endpoint outside the team scope."));

        internal void Store(HttpContext context) => context.Items[ItemKey] = this;

        public bool Can(TeamPermission permission) => (Permissions & permission) == permission;

        public string UserIdText => UserId.ToString();

        /// <summary>For own/any permission pairs: may this member act on something added by <paramref name="addedByUserId"/>?</summary>
        public bool CanOnOwnOrAny(string? addedByUserId, TeamPermission own, TeamPermission any) =>
            Can(any) || (Can(own) && addedByUserId == UserIdText);
    }

    /// <summary>Endpoint metadata: the team permissions an endpoint needs (any one of <see cref="AnyOf"/>), or the team owner.</summary>
    public sealed record TeamPermissionRequirement(TeamPermission AnyOf, bool OwnerOnly = false);

    public static class TeamPermissionExtensions
    {
        /// <summary>The member needs at least one of the flags in <paramref name="anyOf"/>.</summary>
        public static TBuilder RequireTeam<TBuilder>(this TBuilder builder, TeamPermission anyOf) where TBuilder : IEndpointConventionBuilder =>
            builder.WithMetadata(new TeamPermissionRequirement(anyOf));

        public static TBuilder RequireTeamOwner<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
            builder.WithMetadata(new TeamPermissionRequirement(TeamPermission.None, OwnerOnly: true));
    }

    /// <summary>
    /// Gatekeeper for the library API: every /api request except login, one-time links and the staff API needs a logged-in user who is
    /// a member of an enabled team, and the permission the endpoint declares with <see cref="TeamPermissionExtensions.RequireTeam"/>.
    /// An endpoint that declares nothing is refused (fail closed). Picks the team from the <c>clipco.team</c> cookie (the page's team
    /// switcher), else the user's first. Also marks every request on <see cref="RequestActor"/>, which makes <see cref="LibraryDbContext"/>
    /// show only that team's rows.
    /// </summary>
    public static class TeamScope
    {
        public const string TeamCookie = "clipco.team";

        /// <summary>No team login needed: logging in, opening links, and the staff API (which checks staff logins itself).</summary>
        private static readonly string[] OpenPrefixes = ["/api/auth", "/api/links", "/api/admin"];

        public static IApplicationBuilder UseTeamScope(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            var actor = context.RequestServices.GetRequiredService<RequestActor>();
            actor.IsRequest = true;

            var path = context.Request.Path;
            if (!path.StartsWithSegments("/api") || OpenPrefixes.Any(x => path.StartsWithSegments(x)))
            {
                await next(context);
                return;
            }

            if (AuthSetup.GetId(context.User) is not { } userId || context.User.Identity?.AuthenticationType != AuthSchemes.User)
            {
                await Problem(context, StatusCodes.Status401Unauthorized, "login", "Log in first.");
                return;
            }

            var db = context.RequestServices.GetRequiredService<LibraryDbContext>();
            var memberships = await db.TeamMembers.AsNoTracking()
                .Where(x => x.UserId == userId && x.Team!.DisabledAtUtc == null)
                .OrderBy(x => x.JoinedAtUtc)
                .Select(x => new { x.TeamId, x.Team!.Name, x.Team.OwnerUserId, Permissions = x.Role == null ? TeamPermission.None : x.Role.Permissions })
                .ToListAsync(context.RequestAborted);

            var wanted = int.TryParse(context.Request.Cookies[TeamCookie], out var cookieTeam) ? cookieTeam : (int?)null;
            var membership = memberships.FirstOrDefault(x => x.TeamId == wanted) ?? memberships.FirstOrDefault();
            if (membership is null)
            {
                await Problem(context, StatusCodes.Status403Forbidden, "no-team", "You aren't in a team yet. Ask a team owner for an invite link.");
                return;
            }

            var isOwner = membership.OwnerUserId == userId;
            var team = new TeamContext(userId, context.User.Identity?.Name ?? "", membership.TeamId, membership.Name, isOwner,
                isOwner ? TeamPermission.All : membership.Permissions & TeamPermission.All);
            team.Store(context);
            actor.UserId = userId;
            actor.TeamId = team.TeamId;

            if (team.Permissions == TeamPermission.None)
            {
                await Problem(context, StatusCodes.Status403Forbidden, "no-access", $"You don't have any access in {team.TeamName} yet. Ask its owner for a role.");
                return;
            }

            var requirement = context.GetEndpoint()?.Metadata.GetMetadata<TeamPermissionRequirement>();
            var allowed = requirement switch
            {
                null => false,
                { OwnerOnly: true } => team.IsOwner,
                _ => (team.Permissions & requirement.AnyOf) != 0,
            };
            if (!allowed)
            {
                await Problem(context, StatusCodes.Status403Forbidden, "forbidden", "Your role doesn't allow that.");
                return;
            }

            await next(context);
        });

        public static Task Problem(HttpContext context, int status, string code, string detail)
        {
            context.Response.StatusCode = status;
            return context.Response.WriteAsJsonAsync(new { status, code, detail }, options: null, contentType: "application/problem+json");
        }
    }
}
