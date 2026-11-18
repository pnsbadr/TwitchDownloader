using Microsoft.AspNetCore.Routing;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Services.ErrorReports
{
    /// <summary>
    /// Turns every Error/Critical log line of the app into an <see cref="ErrorReport"/>: a request's (with who made it, the endpoint and the
    /// browser) or a background job's (with the entry, export or VOD it was about, from the log line's values). Crashes of a request are
    /// logged by ASP.NET's exception handler, so they come through here too. EF Core's own errors and the writer's are skipped (no loops).
    /// </summary>
    public sealed class ErrorReportLoggerProvider(IHttpContextAccessor httpContextAccessor, ErrorReportSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || categoryName.StartsWith(typeof(ErrorReportWriter).FullName!, StringComparison.Ordinal)
                ? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
                : new ReportLogger(categoryName, httpContextAccessor, sink);

        public void Dispose()
        {
        }

        private sealed class ReportLogger(string category, IHttpContextAccessor httpContextAccessor, ErrorReportSink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                try
                {
                    sink.Add(Build(state, exception, formatter(state, exception)));
                }
                catch
                {
                    // A report that can't be made must never break the code that logged
                }
            }

            private ErrorReport Build<TState>(TState state, Exception? exception, string line)
            {
                var root = exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerException! : exception;
                var report = new ErrorReport
                {
                    CreatedAtUtc = DateTime.UtcNow,
                    ErrorType = root?.GetType().Name,
                    Message = ReportScrubber.Clean(root?.Message is { Length: > 0 } m ? m : line, ErrorReport.MaxMessageLength) ?? "",
                    Details = ReportScrubber.Clean(exception is null ? line : $"{line}\n\n{exception}", ErrorReport.MaxDetailsLength),
                };

                if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    foreach (var (key, value) in values)
                    {
                        switch (key)
                        {
                            case "EntryId" when ToLong(value) is { } entryId: report.EntryId = (int)entryId; break;
                            case "VodId" when ToLong(value) is { } vodId: report.VodId = vodId; break;
                            case "BatchId" when ToLong(value) is { } batchId: report.ExportId = (int)batchId; break;
                            case "ExportId" when ToLong(value) is { } exportId:
                                // Segment downloads log ExportId; exports log BatchId
                                if (category.Contains("Segment", StringComparison.Ordinal)) report.SegmentDownloadId = (int)exportId;
                                else report.ExportId = (int)exportId;
                                break;
                        }
                    }
                }

                var context = httpContextAccessor.HttpContext;
                if (context is null)
                {
                    report.Kind = ErrorReportKind.JobError;
                    report.Area = ShortCategory(category);
                    return report;
                }

                report.Kind = ErrorReportKind.ServerError;
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                report.Area = ReportScrubber.Cut($"{context.Request.Method} {(route is null ? context.Request.Path.Value : "/" + route.TrimStart('/'))}", 200);
                report.Url = ReportScrubber.CleanUrl(context.Request.Path + context.Request.QueryString);
                report.RequestId = ReportScrubber.Cut(context.TraceIdentifier, 100);
                report.StatusCode = category.Contains("ExceptionHandler", StringComparison.Ordinal) ? 500
                    : context.Response.StatusCode >= 400 ? context.Response.StatusCode : null;
                var userAgent = context.Request.Headers.UserAgent.ToString();
                report.UserAgent = string.IsNullOrEmpty(userAgent) ? null : ReportScrubber.Cut(userAgent, 500);
                (report.Browser, report.Os) = UserAgentInfo.Read(userAgent);
                if (context.Request.RouteValues.TryGetValue("vodId", out var routeVod) && ToLong(routeVod) is { } vod)
                    report.VodId ??= vod;

                try
                {
                    if (TeamContext.Of(context) is { } team)
                    {
                        report.TeamId = team.TeamId;
                        report.TeamName = team.TeamName;
                        if (team.UserId > 0)
                        {
                            report.UserId = team.UserId;
                            report.Username = team.Username;
                        }
                        report.StaffId = team.SupportStaffId;
                    }
                    else if (context.RequestServices.GetService<RequestActor>() is { } actor)
                    {
                        report.UserId = actor.UserId;
                        report.TeamId = actor.TeamId;
                        report.StaffId = actor.StaffId;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The request is over; the writer fills in what it can
                }
                return report;
            }

            private static long? ToLong(object? value) => value switch
            {
                int i => i,
                long l => l,
                string s when long.TryParse(s, out var parsed) => parsed,
                _ => null,
            };

            private static string ShortCategory(string category)
            {
                var dot = category.LastIndexOf('.');
                return ReportScrubber.Cut(dot >= 0 ? category[(dot + 1)..] : category, 200);
            }
        }
    }
}
