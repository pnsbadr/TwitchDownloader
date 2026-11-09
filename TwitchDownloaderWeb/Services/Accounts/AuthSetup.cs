using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Accounts
{
    public static class AuthSchemes
    {
        /// <summary>Team users (the main page). Default scheme.</summary>
        public const string User = "User";

        /// <summary>App staff (/admin.html). A separate cookie, so being logged in as staff never makes you a team user or the other way round.</summary>
        public const string Staff = "Staff";

        public const string StampClaim = "stamp";
        public const string LoginRateLimit = "login";
    }

    public static class AuthSetup
    {
        public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
        private static readonly TimeSpan ActivityWriteInterval = TimeSpan.FromMinutes(2);

        /// <param name="loginAttemptsPerMinute">Per IP, for login and link endpoints (<c>Auth:LoginAttemptsPerMinute</c>, default 10).</param>
        public static IServiceCollection AddClipcoAuth(this IServiceCollection services, int loginAttemptsPerMinute = 10)
        {
            services.AddScoped<RequestActor>();

            services.AddAuthentication(AuthSchemes.User)
                .AddCookie(AuthSchemes.User, options => Configure(options, "clipco.user", ValidateUserAsync))
                .AddCookie(AuthSchemes.Staff, options => Configure(options, "clipco.staff", ValidateStaffAsync));
            services.AddAuthorization();

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                // Per IP: slows down password guessing across accounts (each account also locks after a few wrong passwords)
                options.AddPolicy(AuthSchemes.LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = loginAttemptsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });

            return services;
        }

        private static void Configure(CookieAuthenticationOptions options, string cookieName, Func<CookieValidatePrincipalContext, Task> validate)
        {
            options.Cookie.Name = cookieName;
            options.Cookie.HttpOnly = true;
            // Strict: the cookie is never sent on requests started by other sites, which is what blocks CSRF here
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = SessionLifetime;
            options.SlidingExpiration = true;
            // An API: answer 401/403 instead of redirecting to a login page
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = validate;
        }

        public static ClaimsPrincipal Principal(string scheme, int id, string username, string stamp) =>
            new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, id.ToString()),
                new Claim(ClaimTypes.Name, username),
                new Claim(AuthSchemes.StampClaim, stamp),
            ], scheme));

        public static AuthenticationProperties PersistentSession() => new() { IsPersistent = true };

        public static int? GetId(ClaimsPrincipal? principal) =>
            int.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

        /// <summary>Every request: the account must still exist, be enabled and have the same security stamp (password changes sign other sessions out).</summary>
        private static async Task ValidateUserAsync(CookieValidatePrincipalContext context)
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<LibraryDbContext>();
            var id = GetId(context.Principal);
            var user = id is null ? null : await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (user is null || user.DisabledAtUtc is not null || user.SecurityStamp != context.Principal!.FindFirstValue(AuthSchemes.StampClaim))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(AuthSchemes.User);
                return;
            }

            var now = DateTime.UtcNow;
            if (user.LastActiveAtUtc is null || now - user.LastActiveAtUtc > ActivityWriteInterval)
            {
                user.LastActiveAtUtc = now;
                await db.SaveChangesAsync();
            }
        }

        private static async Task ValidateStaffAsync(CookieValidatePrincipalContext context)
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<LibraryDbContext>();
            var id = GetId(context.Principal);
            var staff = id is null ? null : await db.StaffAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (staff is null || staff.DisabledAtUtc is not null || staff.SecurityStamp != context.Principal!.FindFirstValue(AuthSchemes.StampClaim))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(AuthSchemes.Staff);
            }
        }
    }
}
