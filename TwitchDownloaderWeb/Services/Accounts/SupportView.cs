using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <summary>
    /// "View as support": app staff with <see cref="StaffPermission.ViewAsSupport"/> open a team's library and Team page read-only, to see
    /// how teams use the app. Starting it (on the Admin page) sets the <see cref="Cookie"/> to the team's ID; while it is there, library
    /// requests act as that team with <see cref="Permissions"/> only, and anything but GET/HEAD is refused. The staff login is checked on
    /// every request, so logging out of staff or losing the permission ends it. Starting and ending are audited, and so is whatever the
    /// visit audits anyway (file downloads), with the staff ID and no user.
    /// </summary>
    public static class SupportView
    {
        public const string Cookie = "clipco.support";
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

        /// <summary>What a support visit can see: the library (watching and files included) and the Team page's members, activity and log.</summary>
        public const TeamPermission Permissions = TeamPermission.ViewLibrary | TeamPermission.ViewActivity;

        public sealed record Session(StaffContext Staff, int TeamId, string TeamName);

        public static bool Requested(HttpContext context) => context.Request.Cookies.ContainsKey(Cookie);

        /// <summary>The support visit this request is part of, or null if there is none or it isn't allowed any more.</summary>
        public static async Task<Session?> ResolveAsync(HttpContext context, LibraryDbContext db)
        {
            if (!int.TryParse(context.Request.Cookies[Cookie], out var teamId))
                return null;

            var staff = await StaffContext.FromRequestAsync(context, db);
            if (staff is null || !staff.Can(StaffPermission.ViewAsSupport))
                return null;

            var teamName = await db.Teams.AsNoTracking().Where(x => x.Id == teamId).Select(x => x.Name).FirstOrDefaultAsync(context.RequestAborted);
            return teamName is null ? null : new Session(staff, teamId, teamName);
        }

        public static void Start(HttpContext context, int teamId) =>
            context.Response.Cookies.Append(Cookie, teamId.ToString(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                MaxAge = Lifetime,
            });

        public static void End(HttpContext context) =>
            context.Response.Cookies.Delete(Cookie, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps });
    }
}
