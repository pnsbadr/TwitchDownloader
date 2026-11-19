namespace TwitchDownloaderWeb.Data
{
    /// <summary>What made an <see cref="ErrorReport"/>.</summary>
    public enum ErrorReportKind
    {
        /// <summary>An error logged while the server answered a request (a crash of an API call is one).</summary>
        ServerError,

        /// <summary>An error logged by a background job (downloads, exports, renders, audio saving, cleanup).</summary>
        JobError,

        /// <summary>A script error or unhandled promise rejection on a page (the page crashed or a button did nothing).</summary>
        PageError,

        /// <summary>Someone described a problem with "Report a problem" in the account menu.</summary>
        BugReport,
    }

    /// <summary>
    /// A bug, error or crash for the app Owner's Reports tab (badrfly, 2026-11-18): who it happened to, when, where, and what was going on.
    /// Text is scrubbed of tokens before it's stored (<see cref="Services.ErrorReports.ReportScrubber"/>); never put tokens or passwords here.
    /// Names are copied when the report is made, so a report still says who it was after the account is gone.
    /// </summary>
    public sealed class ErrorReport
    {
        public const int MaxMessageLength = 2000;
        public const int MaxDetailsLength = 32_000;
        public const int MaxNoteLength = 4000;
        public const int MaxContextLength = 16_000;

        public long Id { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public ErrorReportKind Kind { get; set; }

        /// <summary>Where: "GET /api/entries/{id}" for a request, the job ("DownloadWorker") or the page ("/", "/team.html").</summary>
        public string Area { get; set; } = "";

        /// <summary>The exception or script error type (InvalidOperationException, TypeError); null for a bug report or a plain log line.</summary>
        public string? ErrorType { get; set; }

        public string Message { get; set; } = "";

        /// <summary>Stack trace, inner exceptions, the log line.</summary>
        public string? Details { get; set; }

        /// <summary>What the person wrote (bug reports).</summary>
        public string? Note { get; set; }

        public int? UserId { get; set; }
        public string? Username { get; set; }
        public int? TeamId { get; set; }
        public string? TeamName { get; set; }
        public int? StaffId { get; set; }
        public string? StaffUsername { get; set; }

        /// <summary>The answer's status for request errors (500 for a crash).</summary>
        public int? StatusCode { get; set; }

        /// <summary>The page (path and query, never the fragment) or request path; scrubbed.</summary>
        public string? Url { get; set; }

        /// <summary>"Chrome 131", "Firefox 132"… read from the user agent.</summary>
        public string? Browser { get; set; }

        /// <summary>"Windows", "macOS", "Android"… read from the user agent.</summary>
        public string? Os { get; set; }

        public string? UserAgent { get; set; }

        /// <summary>The VOD, entry, export or segment download it was about, when known.</summary>
        public long? VodId { get; set; }
        public int? EntryId { get; set; }
        public int? ExportId { get; set; }
        public int? SegmentDownloadId { get; set; }

        /// <summary>JSON from the page: which tab, what was loaded in each player, the screen size, recent requests and errors.</summary>
        public string? Context { get; set; }

        /// <summary>Which build of the app it happened on (the git commit when known).</summary>
        public string? AppVersion { get; set; }

        /// <summary>The same error (kind, type, where, message with numbers taken out) has the same fingerprint, so repeats group together.</summary>
        public string Fingerprint { get; set; } = "";

        /// <summary>The request's trace ID (matches the server log), or the page's visit ID.</summary>
        public string? RequestId { get; set; }
    }
}
