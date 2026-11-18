using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services.ErrorReports;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="Kind">"error" (a script error or crash on the page, sent by the page itself) or "bug" (someone described a problem).</param>
    /// <param name="Note">What the person wrote (bug reports; required for them).</param>
    /// <param name="Url">The page's address; the fragment (which can hold a link token) is dropped.</param>
    /// <param name="Context">What was going on: tab, what each player had loaded, screen size, recent requests and errors.</param>
    /// <param name="VisitId">Random per page load, so reports from one visit can be put together.</param>
    public sealed record PageReportRequest(string? Kind, string? Message, string? ErrorType, string? Details, string? Note, string? Url,
        JsonElement? Context, long? VodId, int? EntryId, string? VisitId);

    /// <summary>Filters and sorting for the Reports tab (all optional). <c>from</c>/<c>to</c> are instants with an offset.</summary>
    public sealed class ReportQuery
    {
        public string? Kind { get; set; }
        public int? UserId { get; set; }
        public int? TeamId { get; set; }
        public int? StaffId { get; set; }
        public string? Area { get; set; }
        public string? ErrorType { get; set; }
        public string? Browser { get; set; }
        public string? Os { get; set; }
        public string? AppVersion { get; set; }
        public long? VodId { get; set; }
        public string? Fingerprint { get; set; }
        public string? Search { get; set; }
        public DateTimeOffset? From { get; set; }
        public DateTimeOffset? To { get; set; }

        /// <summary>time (default), user, team, kind, area, type, browser, os, version.</summary>
        public string? Sort { get; set; }

        /// <summary>desc (default) or asc.</summary>
        public string? Order { get; set; }

        public int? Offset { get; set; }
        public int? Limit { get; set; }
    }

    public sealed record ErrorReportDto(long Id, DateTime CreatedAtUtc, string Kind, string Area, string? ErrorType, string Message, string? Note,
        int? UserId, string? Username, int? TeamId, string? TeamName, int? StaffId, string? StaffUsername, int? StatusCode, string? Url,
        string? Browser, string? Os, long? VodId, int? EntryId, int? ExportId, int? SegmentDownloadId, string? AppVersion, string Fingerprint,
        string? RequestId, bool HasDetails, string? Details = null, string? Context = null, string? UserAgent = null);

    public sealed record FacetDto(string Value, string Label, int Count);

    public sealed record ReportFacetsDto(IReadOnlyList<FacetDto> Kinds, IReadOnlyList<FacetDto> Users, IReadOnlyList<FacetDto> Teams,
        IReadOnlyList<FacetDto> Areas, IReadOnlyList<FacetDto> ErrorTypes, IReadOnlyList<FacetDto> Browsers, IReadOnlyList<FacetDto> Systems,
        IReadOnlyList<FacetDto> AppVersions);

    public sealed record ReportListDto(int Total, IReadOnlyList<ErrorReportDto> Items, ReportFacetsDto Facets);

    /// <param name="People">How many different people it happened to.</param>
    public sealed record ReportGroupDto(string Fingerprint, string Kind, string Area, string? ErrorType, string Message, int Count, int People,
        IReadOnlyList<string> Usernames, DateTime FirstAtUtc, DateTime LastAtUtc, long LatestId);

    /// <summary>
    /// Bugs, errors and crashes (badrfly, 2026-11-18): pages send their own script errors and "Report a problem" notes
    /// (<c>POST /api/auth/reports</c>, open to anyone, rate limited, so a crash on the login page is caught too); the server's own errors come
    /// from <see cref="ErrorReportLoggerProvider"/>. Staff with <see cref="StaffPermission.ViewErrorReports"/> (the Owner always) list, group,
    /// sort, download (CSV/JSON) and delete them on the Admin page's Reports tab.
    /// </summary>
    public static class ErrorReportEndpoints
    {
        private const int MaxDownload = 20_000;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public static IEndpointRouteBuilder MapErrorReportEndpoints(this IEndpointRouteBuilder app)
        {
            // Under /api/auth: no team login needed (TeamScope leaves /api/auth alone)
            app.MapPost("/api/auth/reports", AddPageReport).RequireRateLimiting(AuthSchemes.ReportRateLimit);

            var staff = app.MapGroup("/api/admin/reports");
            staff.MapGet("", List).RequireStaff(StaffPermission.ViewErrorReports);
            staff.MapGet("/groups", Groups).RequireStaff(StaffPermission.ViewErrorReports);
            staff.MapGet("/download", Download).RequireStaff(StaffPermission.ViewErrorReports);
            staff.MapGet("/{id:long}", Get).RequireStaff(StaffPermission.ViewErrorReports);
            staff.MapDelete("/{id:long}", Delete).RequireStaff(StaffPermission.ViewErrorReports);
            staff.MapDelete("", DeleteMatching).RequireStaff(StaffPermission.ViewErrorReports);
            return app;
        }

        // ---------- from pages ----------

        private static async Task<IResult> AddPageReport(PageReportRequest request, HttpContext context, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var isBug = string.Equals(request.Kind, "bug", StringComparison.OrdinalIgnoreCase);
            if (!isBug && !string.Equals(request.Kind, "error", StringComparison.OrdinalIgnoreCase))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Use \"error\" or \"bug\"."] });
            var note = request.Note?.Trim();
            if (isBug && string.IsNullOrEmpty(note))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Say what went wrong."] });
            if (!isBug && string.IsNullOrWhiteSpace(request.Message))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["message"] = ["Send the error's message."] });

            var url = ReportScrubber.CleanUrl(request.Url);
            var path = url is null ? "" : url.Split('?')[0];
            var userAgent = context.Request.Headers.UserAgent.ToString();
            var (browser, os) = UserAgentInfo.Read(userAgent);
            var report = new ErrorReport
            {
                CreatedAtUtc = DateTime.UtcNow,
                Kind = isBug ? ErrorReportKind.BugReport : ErrorReportKind.PageError,
                Area = ReportScrubber.Cut(string.IsNullOrEmpty(path) ? "(page)" : path, 200),
                ErrorType = isBug ? null : ReportScrubber.Clean(request.ErrorType?.Trim(), 200),
                Message = ReportScrubber.Clean(isBug ? FirstLine(note!) : request.Message!.Trim(), ErrorReport.MaxMessageLength) ?? "",
                Details = ReportScrubber.Clean(request.Details, ErrorReport.MaxDetailsLength),
                Note = note is null ? null : ReportScrubber.Clean(note, ErrorReport.MaxNoteLength),
                Url = url,
                UserAgent = string.IsNullOrEmpty(userAgent) ? null : ReportScrubber.Cut(userAgent, 500),
                Browser = browser,
                Os = os,
                VodId = request.VodId is > 0 ? request.VodId : null,
                EntryId = request.EntryId is > 0 ? request.EntryId : null,
                Context = request.Context is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } contextJson
                    ? ReportScrubber.Clean(contextJson.GetRawText(), ErrorReport.MaxContextLength) : null,
                RequestId = request.VisitId is { Length: > 0 } visit ? ReportScrubber.Cut(visit, 100) : context.TraceIdentifier,
                AppVersion = ReportScrubber.AppVersion,
            };
            report.Fingerprint = ReportScrubber.Fingerprint(report.Kind, report.ErrorType, report.Area,
                isBug ? $"bug {report.Id} {Guid.NewGuid()}" : report.Message);

            // Who: the team login (and the team picked in the switcher), and/or a staff login
            if (AuthSetup.GetId(context.User) is { } userId && context.User.Identity?.AuthenticationType == AuthSchemes.User)
            {
                report.UserId = userId;
                report.Username = context.User.Identity?.Name;
                var memberships = await db.TeamMembers.AsNoTracking().Where(x => x.UserId == userId)
                    .OrderBy(x => x.JoinedAtUtc).Select(x => new { x.TeamId, x.Team!.Name }).ToListAsync(cancellationToken);
                var wanted = int.TryParse(context.Request.Cookies[TeamScope.TeamCookie], out var cookieTeam) ? cookieTeam : (int?)null;
                var team = memberships.FirstOrDefault(x => x.TeamId == wanted) ?? memberships.FirstOrDefault();
                (report.TeamId, report.TeamName) = (team?.TeamId, team?.Name);
            }
            var staffLogin = await context.AuthenticateAsync(AuthSchemes.Staff);
            if (staffLogin.Succeeded && AuthSetup.GetId(staffLogin.Principal) is { } staffId)
            {
                report.StaffId = staffId;
                report.StaffUsername = staffLogin.Principal!.Identity?.Name;
            }

            db.ErrorReports.Add(report);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/reports/{report.Id}", new { report.Id });
        }

        private static string FirstLine(string text)
        {
            var line = text.Split('\n', 2)[0].Trim();
            return line.Length > 200 ? line[..199] + "…" : line;
        }

        // ---------- staff ----------

        private static async Task<IResult> List([AsParameters] ReportQuery query, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (Check(query) is { } problem)
                return problem;
            var filtered = Filter(db.ErrorReports.AsNoTracking(), query);
            var total = await filtered.CountAsync(cancellationToken);
            var rows = await Sort(filtered, query).Skip(Math.Max(0, query.Offset ?? 0)).Take(Math.Clamp(query.Limit ?? 100, 1, 500))
                .ToListAsync(cancellationToken);
            var facets = await FacetsAsync(db, query, cancellationToken);
            return Results.Ok(new ReportListDto(total, rows.Select(x => ToDto(x, full: false)).ToList(), facets));
        }

        private static async Task<IResult> Get(long id, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var report = await db.ErrorReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return report is null
                ? Results.Problem(detail: "That report is gone.", statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(ToDto(report, full: true));
        }

        /// <summary>The same error together: how often, to how many people, first and last time. Sort: count (default), last, first, people.</summary>
        private static async Task<IResult> Groups([AsParameters] ReportQuery query, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (Check(query) is { } problem)
                return problem;
            var rows = await Filter(db.ErrorReports.AsNoTracking(), query).OrderByDescending(x => x.Id).Take(MaxDownload)
                .Select(x => new { x.Id, x.Fingerprint, x.Kind, x.Area, x.ErrorType, x.Message, x.UserId, x.Username, x.StaffUsername, x.CreatedAtUtc })
                .ToListAsync(cancellationToken);
            var groups = rows.GroupBy(x => x.Fingerprint).Select(g =>
            {
                var latest = g.First();
                var people = g.Select(x => x.Username ?? (x.StaffUsername is null ? null : $"{x.StaffUsername} (staff)")).Where(x => x is not null)
                    .GroupBy(x => x!).OrderByDescending(x => x.Count()).Select(x => x.Key).ToList();
                return new ReportGroupDto(g.Key, latest.Kind.ToString(), latest.Area, latest.ErrorType, latest.Message, g.Count(),
                    g.Select(x => x.UserId).Where(x => x is not null).Distinct().Count(), people.Take(5).ToList(),
                    g.Min(x => x.CreatedAtUtc), g.Max(x => x.CreatedAtUtc), latest.Id);
            });
            var ascending = string.Equals(query.Order, "asc", StringComparison.OrdinalIgnoreCase);
            groups = (query.Sort?.ToLowerInvariant()) switch
            {
                "last" or "time" => ascending ? groups.OrderBy(x => x.LastAtUtc) : groups.OrderByDescending(x => x.LastAtUtc),
                "first" => ascending ? groups.OrderBy(x => x.FirstAtUtc) : groups.OrderByDescending(x => x.FirstAtUtc),
                "people" => ascending ? groups.OrderBy(x => x.People).ThenBy(x => x.Count) : groups.OrderByDescending(x => x.People).ThenByDescending(x => x.Count),
                _ => ascending ? groups.OrderBy(x => x.Count).ThenBy(x => x.LastAtUtc) : groups.OrderByDescending(x => x.Count).ThenByDescending(x => x.LastAtUtc),
            };
            return Results.Ok(groups.Take(Math.Clamp(query.Limit ?? 200, 1, 1000)).ToList());
        }

        /// <param name="format">csv (default; opens in Excel or Sheets) or json.</param>
        private static async Task<IResult> Download([AsParameters] ReportQuery query, string? format, StaffContext me, LibraryDbContext db,
            CancellationToken cancellationToken)
        {
            if (Check(query) is { } problem)
                return problem;
            var asJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            var rows = await Sort(Filter(db.ErrorReports.AsNoTracking(), query), query).Take(MaxDownload).ToListAsync(cancellationToken);
            db.Audit(AuditActions.ErrorReportsDownloaded, null, $"{rows.Count} reports as {(asJson ? "JSON" : "CSV")} by {me.Username}");
            await db.SaveChangesAsync(cancellationToken);

            var name = $"clipco-reports-{DateTime.UtcNow:yyyy-MM-dd-HHmm}";
            if (asJson)
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(rows.Select(x => ToDto(x, full: true)), Json), "application/json", $"{name}.json");

            var csv = new StringBuilder();
            string[] header = ["id", "time_utc", "kind", "user", "user_id", "team", "team_id", "staff", "where", "error_type", "message", "note",
                "status", "url", "browser", "system", "vod_id", "entry_id", "export_id", "segment_download_id", "app_version", "fingerprint",
                "request_id", "user_agent", "details", "context"];
            csv.AppendJoin(',', header).Append("\r\n");
            foreach (var x in rows)
            {
                string?[] cells = [x.Id.ToString(CultureInfo.InvariantCulture), x.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    x.Kind.ToString(), x.Username, x.UserId?.ToString(CultureInfo.InvariantCulture), x.TeamName,
                    x.TeamId?.ToString(CultureInfo.InvariantCulture), x.StaffUsername, x.Area, x.ErrorType, x.Message, x.Note,
                    x.StatusCode?.ToString(CultureInfo.InvariantCulture), x.Url, x.Browser, x.Os, x.VodId?.ToString(CultureInfo.InvariantCulture),
                    x.EntryId?.ToString(CultureInfo.InvariantCulture), x.ExportId?.ToString(CultureInfo.InvariantCulture),
                    x.SegmentDownloadId?.ToString(CultureInfo.InvariantCulture), x.AppVersion, x.Fingerprint, x.RequestId, x.UserAgent, x.Details, x.Context];
                csv.AppendJoin(',', cells.Select(CsvCell)).Append("\r\n");
            }
            // With a BOM so Excel reads it as UTF-8
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", $"{name}.csv");
        }

        private static async Task<IResult> Delete(long id, StaffContext me, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var deleted = await db.ErrorReports.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
            if (deleted == 0)
                return Results.Problem(detail: "That report is gone.", statusCode: StatusCodes.Status404NotFound);
            db.Audit(AuditActions.ErrorReportsDeleted, null, $"Report {id} by {me.Username}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        /// <summary>Deletes every report the filters match (the list as shown). Returns how many.</summary>
        private static async Task<IResult> DeleteMatching([AsParameters] ReportQuery query, StaffContext me, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (Check(query) is { } problem)
                return problem;
            var deleted = await Filter(db.ErrorReports, query).ExecuteDeleteAsync(cancellationToken);
            db.Audit(AuditActions.ErrorReportsDeleted, null, $"{deleted} reports by {me.Username}");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { deleted });
        }

        // ---------- helpers ----------

        private static IResult? Check(ReportQuery query)
        {
            if (query.Kind is { Length: > 0 } kind && !Enum.TryParse<ErrorReportKind>(kind, ignoreCase: true, out _))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["kind"] = [$"Use one of: {string.Join(", ", Enum.GetNames<ErrorReportKind>())}."] });
            return null;
        }

        private static IQueryable<ErrorReport> Filter(IQueryable<ErrorReport> reports, ReportQuery query, bool timeOnly = false)
        {
            if (query.From is { } from)
            {
                var utc = from.UtcDateTime;
                reports = reports.Where(x => x.CreatedAtUtc >= utc);
            }
            if (query.To is { } to)
            {
                var utc = to.UtcDateTime;
                reports = reports.Where(x => x.CreatedAtUtc < utc);
            }
            if (timeOnly)
                return reports;
            if (query.Kind is { Length: > 0 } kindText && Enum.TryParse<ErrorReportKind>(kindText, ignoreCase: true, out var kind))
                reports = reports.Where(x => x.Kind == kind);
            if (query.UserId is { } userId)
                reports = userId == 0 ? reports.Where(x => x.UserId == null) : reports.Where(x => x.UserId == userId);
            if (query.TeamId is { } teamId)
                reports = teamId == 0 ? reports.Where(x => x.TeamId == null) : reports.Where(x => x.TeamId == teamId);
            if (query.StaffId is { } staffId)
                reports = reports.Where(x => x.StaffId == staffId);
            if (query.Area is { Length: > 0 } area)
                reports = reports.Where(x => x.Area == area);
            if (query.ErrorType is { Length: > 0 } errorType)
                reports = errorType == "-" ? reports.Where(x => x.ErrorType == null) : reports.Where(x => x.ErrorType == errorType);
            if (query.Browser is { Length: > 0 } browser)
                reports = reports.Where(x => x.Browser == browser);
            if (query.Os is { Length: > 0 } os)
                reports = reports.Where(x => x.Os == os);
            if (query.AppVersion is { Length: > 0 } version)
                reports = reports.Where(x => x.AppVersion == version);
            if (query.VodId is { } vodId)
                reports = reports.Where(x => x.VodId == vodId);
            if (query.Fingerprint is { Length: > 0 } fingerprint)
                reports = reports.Where(x => x.Fingerprint == fingerprint);
            if (query.Search?.Trim() is { Length: > 0 } search)
            {
                var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
                reports = reports.Where(x => EF.Functions.Like(x.Message, pattern, "\\") || EF.Functions.Like(x.Note!, pattern, "\\")
                    || EF.Functions.Like(x.Area, pattern, "\\") || EF.Functions.Like(x.ErrorType!, pattern, "\\")
                    || EF.Functions.Like(x.Username!, pattern, "\\") || EF.Functions.Like(x.TeamName!, pattern, "\\"));
            }
            return reports;
        }

        private static IQueryable<ErrorReport> Sort(IQueryable<ErrorReport> reports, ReportQuery query)
        {
            var ascending = string.Equals(query.Order, "asc", StringComparison.OrdinalIgnoreCase);
            IOrderedQueryable<ErrorReport> ordered = (query.Sort?.ToLowerInvariant()) switch
            {
                "user" => ascending ? reports.OrderBy(x => x.Username ?? x.StaffUsername) : reports.OrderByDescending(x => x.Username ?? x.StaffUsername),
                "team" => ascending ? reports.OrderBy(x => x.TeamName) : reports.OrderByDescending(x => x.TeamName),
                "kind" => ascending ? reports.OrderBy(x => x.Kind) : reports.OrderByDescending(x => x.Kind),
                "area" => ascending ? reports.OrderBy(x => x.Area) : reports.OrderByDescending(x => x.Area),
                "type" => ascending ? reports.OrderBy(x => x.ErrorType) : reports.OrderByDescending(x => x.ErrorType),
                "browser" => ascending ? reports.OrderBy(x => x.Browser) : reports.OrderByDescending(x => x.Browser),
                "os" => ascending ? reports.OrderBy(x => x.Os) : reports.OrderByDescending(x => x.Os),
                "version" => ascending ? reports.OrderBy(x => x.AppVersion) : reports.OrderByDescending(x => x.AppVersion),
                _ => ascending ? reports.OrderBy(x => x.Id) : reports.OrderByDescending(x => x.Id),
            };
            // Within the sorted field: newest first (or oldest, ascending by time)
            return query.Sort is null or "time" ? ordered : ordered.ThenByDescending(x => x.Id);
        }

        /// <summary>What there is to filter on in the chosen time range (other filters ignored, so every choice stays visible).</summary>
        private static async Task<ReportFacetsDto> FacetsAsync(LibraryDbContext db, ReportQuery query, CancellationToken cancellationToken)
        {
            var rows = await Filter(db.ErrorReports.AsNoTracking(), query, timeOnly: true).OrderByDescending(x => x.Id).Take(MaxDownload)
                .Select(x => new { x.Kind, x.UserId, x.Username, x.StaffUsername, x.TeamId, x.TeamName, x.Area, x.ErrorType, x.Browser, x.Os, x.AppVersion })
                .ToListAsync(cancellationToken);

            static List<FacetDto> Count<T>(IEnumerable<T> items, Func<T, string?> value, Func<T, string?> label) =>
                items.GroupBy(value).Where(g => g.Key is not null)
                    .Select(g => new FacetDto(g.Key!, label(g.First()) ?? g.Key!, g.Count()))
                    .OrderByDescending(x => x.Count).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();

            return new ReportFacetsDto(
                Count(rows, x => x.Kind.ToString(), x => x.Kind.ToString()),
                Count(rows, x => (x.UserId ?? 0).ToString(CultureInfo.InvariantCulture),
                    x => x.UserId is null ? (x.StaffUsername is null ? "Nobody logged in / background" : "Staff") : x.Username ?? $"User {x.UserId}"),
                Count(rows, x => (x.TeamId ?? 0).ToString(CultureInfo.InvariantCulture), x => x.TeamId is null ? "No team" : x.TeamName ?? $"Team {x.TeamId}"),
                Count(rows, x => x.Area, x => x.Area),
                Count(rows, x => x.ErrorType ?? "-", x => x.ErrorType ?? "(none)"),
                Count(rows, x => x.Browser, x => x.Browser),
                Count(rows, x => x.Os, x => x.Os),
                Count(rows, x => x.AppVersion, x => x.AppVersion));
        }

        private static ErrorReportDto ToDto(ErrorReport x, bool full) => new(x.Id, x.CreatedAtUtc, x.Kind.ToString(), x.Area, x.ErrorType,
            full ? x.Message : ReportScrubber.Cut(x.Message, 300), full ? x.Note : x.Note is null ? null : ReportScrubber.Cut(x.Note, 300),
            x.UserId, x.Username, x.TeamId, x.TeamName, x.StaffId, x.StaffUsername, x.StatusCode, x.Url, x.Browser, x.Os, x.VodId, x.EntryId,
            x.ExportId, x.SegmentDownloadId, x.AppVersion, x.Fingerprint, x.RequestId, x.Details is not null || x.Context is not null,
            full ? x.Details : null, full ? x.Context : null, full ? x.UserAgent : null);

        /// <summary>A CSV cell: quoted when needed, and text that a spreadsheet would run as a formula is prefixed with '.</summary>
        private static string CsvCell(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                value = "'" + value;
            return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }
    }
}
