using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="Name">The app's name (start screen, top left). Blank = the default from configuration.</param>
    /// <param name="X">The X link in the start screen's corner. Blank = hidden.</param>
    /// <param name="Discord">The Discord link. Blank = hidden.</param>
    public sealed record AppSettingsRequest(string? Name, string? X, string? Discord);

    public sealed record AppLinksDto(string? X, string? Discord);

    public sealed record AppInfoDto(string Name, AppLinksDto Links);

    public static class AppSettingsRules
    {
        public const int MaxNameLength = 60;
        public const int MaxUrlLength = 300;
        public const string DefaultName = "Clipco";

        /// <summary>An http(s) link, "https://" added when it has no scheme ("x.com/clipco"). Null when blank or not a web link.</summary>
        public static string? CleanUrl(string? value)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Contains('.') ? uri.ToString() : null;
        }
    }

    /// <summary>
    /// The app's name and the start screen's corner links (badrfly 2026-11-19): read by every page (<c>GET /api/auth/app</c>, no login),
    /// set by the app Owner on the Admin page's App tab. The <see cref="AppSettings"/> row wins over configuration once it's saved.
    /// </summary>
    public static class AppSettingsEndpoints
    {
        public static IEndpointRouteBuilder MapAppSettingsEndpoints(this IEndpointRouteBuilder app)
        {
            var admin = app.MapGroup("/api/admin/app");
            admin.MapGet("/", (LibraryDbContext db, IConfiguration configuration, CancellationToken cancellationToken) => GetAsync(db, configuration, cancellationToken))
                .RequireStaffOwner();
            admin.MapPut("/", Save).RequireStaffOwner();
            return app;
        }

        public static async Task<AppInfoDto> ReadAsync(LibraryDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
            var name = row?.Name is { Length: > 0 } saved ? saved
                : configuration["App:Name"] is { Length: > 0 } configured ? configured : AppSettingsRules.DefaultName;
            // A saved row decides the links (blank = hidden), else configuration
            return new AppInfoDto(name, row is null
                ? new AppLinksDto(AppSettingsRules.CleanUrl(configuration["App:Links:X"]), AppSettingsRules.CleanUrl(configuration["App:Links:Discord"]))
                : new AppLinksDto(AppSettingsRules.CleanUrl(row.XUrl), AppSettingsRules.CleanUrl(row.DiscordUrl)));
        }

        private static async Task<IResult> GetAsync(LibraryDbContext db, IConfiguration configuration, CancellationToken cancellationToken) =>
            Results.Ok(await ReadAsync(db, configuration, cancellationToken));

        private static async Task<IResult> Save(AppSettingsRequest request, StaffContext staff, LibraryDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var errors = new Dictionary<string, string[]>();
            var name = request.Name?.Trim();
            if (name is { Length: > AppSettingsRules.MaxNameLength })
                errors["name"] = [$"The name can be at most {AppSettingsRules.MaxNameLength} characters."];
            var x = Link("x", request.X, errors);
            var discord = Link("discord", request.Discord, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var row = await db.AppSettings.FirstOrDefaultAsync(r => r.Id == 1, cancellationToken);
            if (row is null) db.AppSettings.Add(row = new AppSettings { Id = 1 });
            row.Name = string.IsNullOrEmpty(name) ? null : name;
            row.XUrl = x;
            row.DiscordUrl = discord;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedByStaffId = staff.Id;
            db.Audit(AuditActions.AppSettingsChanged, null,
                $"App name {(row.Name is null ? "default" : $"\"{row.Name}\"")}; X {x ?? "hidden"}; Discord {discord ?? "hidden"}", staffId: staff.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await ReadAsync(db, configuration, cancellationToken));
        }

        /// <summary>Blank = "" (hidden); anything else must be a web link.</summary>
        private static string Link(string field, string? value, Dictionary<string, string[]> errors)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var url = AppSettingsRules.CleanUrl(value);
            if (url is null || url.Length > AppSettingsRules.MaxUrlLength)
                errors[field] = [$"Enter a web link (https://…, at most {AppSettingsRules.MaxUrlLength} characters), or leave it blank to hide the icon."];
            return url ?? "";
        }
    }
}
