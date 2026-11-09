using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <summary>
    /// The logged-in user and the team a library request works in. Put a <c>TeamContext team</c> parameter on an endpoint to get it;
    /// <see cref="TeamScope"/> has already checked the login and membership by the time the endpoint runs.
    /// </summary>
    public sealed record TeamContext(int UserId, string Username, int TeamId, string TeamName, bool IsOwner)
    {
        private const string ItemKey = nameof(TeamContext);

        public static ValueTask<TeamContext?> BindAsync(HttpContext context, ParameterInfo parameter) =>
            ValueTask.FromResult(context.Items[ItemKey] as TeamContext
                                 ?? throw new InvalidOperationException("TeamContext used on an endpoint outside the team scope."));

        internal void Store(HttpContext context) => context.Items[ItemKey] = this;
    }

    /// <summary>
    /// Gatekeeper for the library API: every /api request except login, one-time links and the staff API needs a logged-in user who is
    /// a member of an enabled team. Picks the team from the <c>clipco.team</c> cookie (the page's team switcher), else the user's first.
    /// Also marks every request on <see cref="RequestActor"/>, which makes <see cref="LibraryDbContext"/> show only that team's rows.
    /// </summary>
    public static class TeamScope
    {
        public const string TeamCookie = "clipco.team";

        /// <summary>No team login needed: logging in, opening links, and the staff API (which checks staff logins itself).</summary>
        private static readonly string[] OpenPrefixes = ["/api/auth/", "/api/links/", "/api/admin/"];

        public static IApplicationBuilder UseTeamScope(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            var actor = context.RequestServices.GetRequiredService<RequestActor>();
            actor.IsRequest = true;

            var path = context.Request.Path;
            if (!path.StartsWithSegments("/api") || OpenPrefixes.Any(x => path.StartsWithSegments(x.TrimEnd('/'))))
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
                .Select(x => new { x.TeamId, x.Team!.Name, x.Team.OwnerUserId })
                .ToListAsync(context.RequestAborted);

            var wanted = int.TryParse(context.Request.Cookies[TeamCookie], out var cookieTeam) ? cookieTeam : (int?)null;
            var membership = memberships.FirstOrDefault(x => x.TeamId == wanted) ?? memberships.FirstOrDefault();
            if (membership is null)
            {
                await Problem(context, StatusCodes.Status403Forbidden, "no-team", "You aren't in a team yet. Ask a team owner for an invite link.");
                return;
            }

            var team = new TeamContext(userId, context.User.Identity?.Name ?? "", membership.TeamId, membership.Name, membership.OwnerUserId == userId);
            team.Store(context);
            actor.UserId = userId;
            actor.TeamId = team.TeamId;

            // Until roles exist only the team owner can do anything; members join with no permissions
            if (!team.IsOwner)
            {
                await Problem(context, StatusCodes.Status403Forbidden, "no-access", $"The owner of {team.TeamName} hasn't given you access yet.");
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
