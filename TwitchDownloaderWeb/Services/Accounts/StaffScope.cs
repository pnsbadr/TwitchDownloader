using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <summary>The logged-in staff account on a staff endpoint (bind it as a parameter). The Owner has every permission.</summary>
    public sealed record StaffContext(int Id, string Username, bool IsOwner, StaffPermission Permissions)
    {
        private const string ItemKey = nameof(StaffContext);

        public static ValueTask<StaffContext?> BindAsync(HttpContext context, ParameterInfo parameter) =>
            ValueTask.FromResult(context.Items[ItemKey] as StaffContext
                                 ?? throw new InvalidOperationException("StaffContext used on an endpoint without the staff filter."));

        internal void Store(HttpContext context) => context.Items[ItemKey] = this;

        public bool Can(StaffPermission permission) => (Permissions & permission) == permission;

        public static StaffContext From(StaffAccount staff) =>
            new(staff.Id, staff.Username, staff.IsOwner, staff.IsOwner ? StaffPermission.All : staff.Permissions & StaffPermission.All);

        /// <summary>The staff session in this request (the staff cookie), or null. The cookie handler has already checked the security stamp.</summary>
        public static async Task<StaffContext?> FromRequestAsync(HttpContext context, LibraryDbContext db)
        {
            var result = await context.AuthenticateAsync(AuthSchemes.Staff);
            if (!result.Succeeded || AuthSetup.GetId(result.Principal) is not { } staffId)
                return null;

            var staff = await db.StaffAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == staffId && x.DisabledAtUtc == null, context.RequestAborted);
            return staff is null ? null : From(staff);
        }
    }

    /// <summary>
    /// Endpoint metadata for the staff API: the staff permissions an endpoint needs (any one of <see cref="AnyOf"/>), the Owner only,
    /// just a staff login (<see cref="AnyStaff"/>), or no staff login at all (<see cref="Open"/>: setup, login, logout).
    /// </summary>
    public sealed record StaffRequirement(StaffPermission AnyOf, bool OwnerOnly = false, bool AnyStaff = false, bool Open = false);

    public static class StaffScope
    {
        public const string PathPrefix = "/api/admin";

        public static TBuilder RequireStaff<TBuilder>(this TBuilder builder, StaffPermission anyOf) where TBuilder : IEndpointConventionBuilder =>
            builder.WithMetadata(new StaffRequirement(anyOf));

        public static TBuilder RequireStaffOwner<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
            builder.WithMetadata(new StaffRequirement(StaffPermission.None, OwnerOnly: true));

        /// <summary>Any logged-in staff account, whatever its permissions (own account, the page's "who am I").</summary>
        public static TBuilder RequireAnyStaff<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
            builder.WithMetadata(new StaffRequirement(StaffPermission.None, AnyStaff: true));

        public static TBuilder AllowWithoutStaff<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
            builder.WithMetadata(new StaffRequirement(StaffPermission.None, Open: true));

        /// <summary>
        /// Gatekeeper for /api/admin: needs a valid staff session and the permission the endpoint declares; an endpoint that declares
        /// nothing is refused (fail closed). Stores the <see cref="StaffContext"/> for the endpoint and the staff ID for audit events.
        /// Middleware rather than an endpoint filter because parameters (StaffContext) are bound before filters run.
        /// </summary>
        public static IApplicationBuilder UseStaffScope(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(PathPrefix))
            {
                await next(context);
                return;
            }

            var requirement = context.GetEndpoint()?.Metadata.GetMetadata<StaffRequirement>();
            if (requirement is { Open: true } || context.GetEndpoint() is null)
            {
                await next(context); // open endpoints, and unknown paths (404)
                return;
            }

            var db = context.RequestServices.GetRequiredService<LibraryDbContext>();
            if (await StaffContext.FromRequestAsync(context, db) is not { } staff)
            {
                await TeamScope.Problem(context, StatusCodes.Status401Unauthorized, "staff-login", "Log in as app staff first.");
                return;
            }

            context.RequestServices.GetRequiredService<RequestActor>().StaffId = staff.Id;
            staff.Store(context);

            var allowed = requirement switch
            {
                null => false,
                { AnyStaff: true } => true,
                { OwnerOnly: true } => staff.IsOwner,
                _ => (staff.Permissions & requirement.AnyOf) != 0,
            };
            if (!allowed)
            {
                await TeamScope.Problem(context, StatusCodes.Status403Forbidden, "forbidden",
                    requirement is { OwnerOnly: true } ? "Only the app Owner can do that." : "Your staff permissions don't allow that.");
                return;
            }

            await next(context);
        });
    }
}
