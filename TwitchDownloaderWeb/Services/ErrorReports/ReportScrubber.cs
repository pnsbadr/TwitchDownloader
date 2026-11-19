using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.ErrorReports
{
    /// <summary>
    /// Keeps tokens, passwords and signed URLs out of error reports (CLAUDE.md: never log OAuth tokens), trims text to fit, and makes the
    /// fingerprint that groups repeats of one error.
    /// </summary>
    public static partial class ReportScrubber
    {
        public const string Hidden = "[hidden]";

        // token=…, sig=…, password=… in query strings, JSON and log text
        [GeneratedRegex("""(?i)\b(access_token|refresh_token|id_token|token|sig|signature|password|passwd|secret|client_secret|device_code|nauth|nauthsig|authorization|cookie|set-cookie|api_key|apikey)(["']?\s*[=:]\s*["']?)([^&\s"',;}]+)""")]
        private static partial Regex KeyValue();

        // "OAuth abc", "Bearer abc"
        [GeneratedRegex(@"(?i)\b(OAuth|Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}")]
        private static partial Regex AuthHeader();

        // Twitch CDN and usher URLs carry signed tokens in the path or query: keep the host and file name only
        [GeneratedRegex(@"(?i)(https?://[^\s/""']*(?:ttvnw\.net|twitchcdn\.net|cloudfront\.net|jtvnw\.net|twitch\.tv)/)[^\s""']*?([^/\s""'?]*)(\?[^\s""']*)?(?=[\s""']|$)")]
        private static partial Regex TwitchUrl();

        // Long opaque strings (JWTs, base64 blobs): likely secrets
        [GeneratedRegex(@"\b[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}\b")]
        private static partial Regex Jwt();

        /// <summary>Text with secrets replaced by <see cref="Hidden"/>, cut to <paramref name="maxLength"/>.</summary>
        public static string? Clean(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            var cleaned = AuthHeader().Replace(text, m => m.Groups[1].Value + " " + Hidden);
            cleaned = KeyValue().Replace(cleaned, m => m.Groups[1].Value + m.Groups[2].Value + Hidden);
            cleaned = TwitchUrl().Replace(cleaned, m => m.Groups[1].Value + "…/" + m.Groups[2].Value);
            cleaned = Jwt().Replace(cleaned, Hidden);
            return Cut(cleaned, maxLength);
        }

        /// <summary>A page or request URL: path and query only (never the fragment, which holds link tokens), scrubbed.</summary>
        public static string? CleanUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;
            var text = url.Trim();
            var hash = text.IndexOf('#');
            if (hash >= 0)
                text = text[..hash];
            // Only web addresses: on Linux "/page" parses as an absolute file URI
            if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                text = absolute.PathAndQuery;
            return Clean(text, 1000);
        }

        public static string Cut(string text, int maxLength) =>
            text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 1)] + "…";

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();

        [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
        private static partial Regex Guid();

        [GeneratedRegex("\"[^\"]*\"|'[^']*'")]
        private static partial Regex Quoted();

        /// <summary>Same kind, type, place and message (numbers, IDs and quoted names taken out) → same 16-character fingerprint.</summary>
        public static string Fingerprint(ErrorReportKind kind, string? errorType, string? area, string? message)
        {
            var normalized = Digits().Replace(Quoted().Replace(Guid().Replace(message ?? "", "#"), "\"…\""), "#");
            if (normalized.Length > 300)
                normalized = normalized[..300];
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{errorType}|{Digits().Replace(area ?? "", "#")}|{normalized}"));
            return Convert.ToHexStringLower(bytes)[..16];
        }

        /// <summary>This build of the app: the git commit it was built from when the SDK recorded it, else the version.</summary>
        public static string AppVersion { get; } = ReadAppVersion();

        private static string ReadAppVersion()
        {
            var version = typeof(ReportScrubber).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = version.IndexOf('+');
            if (plus >= 0 && version.Length > plus + 1)
            {
                var commit = version[(plus + 1)..];
                return commit.Length > 7 ? commit[..7] : commit;
            }
            return string.IsNullOrEmpty(version) ? "unknown" : Cut(version, 60);
        }
    }

    /// <summary>Browser and system names from a user agent (good enough to sort by; the full string is kept too).</summary>
    public static partial class UserAgentInfo
    {
        [GeneratedRegex(@"(Edg|OPR|Firefox|Chrome|Version|CriOS|FxiOS)/(\d+)")]
        private static partial Regex Product();

        public static (string? Browser, string? Os) Read(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
                return (null, null);
            var ua = userAgent;
            string? browser = null;
            var matches = Product().Matches(ua).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            if (matches.TryGetValue("Edg", out var edge)) browser = $"Edge {edge}";
            else if (matches.TryGetValue("OPR", out var opera)) browser = $"Opera {opera}";
            else if (matches.TryGetValue("Firefox", out var firefox)) browser = $"Firefox {firefox}";
            else if (matches.TryGetValue("FxiOS", out var firefoxIos)) browser = $"Firefox {firefoxIos}";
            else if (matches.TryGetValue("CriOS", out var chromeIos)) browser = $"Chrome {chromeIos}";
            else if (matches.TryGetValue("Chrome", out var chrome)) browser = ua.Contains("Electron/") ? $"Desktop app (Chrome {chrome})" : $"Chrome {chrome}";
            else if (ua.Contains("Safari/") && matches.TryGetValue("Version", out var safari)) browser = $"Safari {safari}";
            else if (ua.Length > 0) browser = "Other";

            var os = ua.Contains("Windows") ? "Windows"
                : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
                : ua.Contains("Mac OS X") || ua.Contains("Macintosh") ? "macOS"
                : ua.Contains("Android") ? "Android"
                : ua.Contains("CrOS") ? "ChromeOS"
                : ua.Contains("Linux") ? "Linux"
                : "Other";
            return (browser, os);
        }
    }
}
