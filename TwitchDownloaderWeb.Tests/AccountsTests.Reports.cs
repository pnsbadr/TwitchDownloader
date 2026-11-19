using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.ErrorReports;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Bugs, errors and crashes for the Owner's Reports tab (badrfly, 2026-11-18).</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public void ReportsHideTokensAndGroupRepeats()
        {
            var text = ReportScrubber.Clean("GET https://usher.ttvnw.net/vod/123.m3u8?sig=abcdef0123&token=%7B%22a%22%7D failed; " +
                "Authorization: OAuth abcdefghijklmnop; password=hunter22; {\"refresh_token\":\"r-123456\"}", 2000)!;
            Assert.DoesNotContain("abcdef0123", text);
            Assert.DoesNotContain("%7B", text);
            Assert.DoesNotContain("abcdefghijklmnop", text);
            Assert.DoesNotContain("hunter22", text);
            Assert.DoesNotContain("r-123456", text);
            Assert.Contains("usher.ttvnw.net/", text);
            Assert.Contains("123.m3u8", text);

            // Link tokens live in the fragment: never kept
            Assert.Equal("/join.html", ReportScrubber.CleanUrl("http://localhost:5254/join.html#secret-link-token"));
            Assert.Equal("/?vod=12", ReportScrubber.CleanUrl("/?vod=12#x"));

            // The same error with other numbers is one problem; another message isn't
            var a = ReportScrubber.Fingerprint(ErrorReportKind.JobError, "IOException", "DownloadWorker", "File 41.mp4 is in use");
            var b = ReportScrubber.Fingerprint(ErrorReportKind.JobError, "IOException", "DownloadWorker", "File 977.mp4 is in use");
            var c = ReportScrubber.Fingerprint(ErrorReportKind.JobError, "IOException", "DownloadWorker", "Disk full");
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);

            Assert.Equal(("Chrome 131", "Windows"), UserAgentInfo.Read(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"));
            Assert.Equal(("Edge 130", "Windows"), UserAgentInfo.Read(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0"));
            Assert.Equal(("Firefox 132", "macOS"), UserAgentInfo.Read("Mozilla/5.0 (Macintosh; Intel Mac OS X 14.1; rv:132.0) Gecko/20100101 Firefox/132.0"));
        }

        [Fact]
        public async Task PagesReportCrashesAndBugsForTheOwner()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("rep1");
            var staff = await StaffClientAsync();

            // A crash on the editor (owner), a bug the member describes, and a crash before anyone logged in
            var crash = await owner.PostAsJsonAsync("/api/auth/reports", new
            {
                kind = "error", message = "Cannot read properties of null (reading 'seek')", errorType = "TypeError",
                details = "TypeError: ...\n    at p2Seek (ui/editor.js:812:10)", url = "http://localhost:5254/?demo#secret-token",
                context = new { view = "editor", vodId = 470, recent = new[] { new { kind = "request", text = "GET /api/vods/470/play.m3u8 → 200" } } },
                vodId = 470, visitId = "visit-1",
            }, ct);
            Assert.Equal(HttpStatusCode.Created, crash.StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/auth/reports",
                new { kind = "bug", note = "The export never finished\nIt sat at 40% for an hour.", url = "/" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await NewClient().PostAsJsonAsync("/api/auth/reports",
                new { kind = "error", message = "x is not defined", errorType = "ReferenceError", url = "/login.html" }, ct)).StatusCode);
            // A bug needs words; a crash needs its message
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/auth/reports", new { kind = "bug", note = " " }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/auth/reports", new { kind = "nope", message = "x" }, ct)).StatusCode);

            var list = await GetJsonAsync(staff, "/api/admin/reports?sort=user&order=asc");
            var items = list.GetProperty("items").EnumerateArray().Where(x => x.GetProperty("kind").GetString() is "PageError" or "BugReport").ToList();
            Assert.Equal(3, items.Count);
            // Sorted by person: nobody last (no name), then rep1member, rep1owner... ascending puts null first in SQLite
            var names = items.Select(x => x.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null).ToList();
            Assert.Equal([null, "rep1member", "rep1owner"], names);

            var ownerCrash = items.Single(x => x.GetProperty("kind").GetString() == "PageError" && x.GetProperty("username").ValueKind == JsonValueKind.String);
            Assert.Equal("/", ownerCrash.GetProperty("area").GetString());
            Assert.Equal("/?demo", ownerCrash.GetProperty("url").GetString());
            Assert.Equal(teamId, ownerCrash.GetProperty("teamId").GetInt32());
            Assert.Equal("rep1 team", ownerCrash.GetProperty("teamName").GetString());
            Assert.Equal(470, ownerCrash.GetProperty("vodId").GetInt64());

            // Details and what was going on come with one report
            var full = await GetJsonAsync(staff, $"/api/admin/reports/{ownerCrash.GetProperty("id").GetInt64()}");
            Assert.Contains("p2Seek", full.GetProperty("details").GetString());
            Assert.Contains("play.m3u8", full.GetProperty("context").GetString());

            // Filter by person, by kind; facets list who there is to pick
            var mine = await GetJsonAsync(staff, $"/api/admin/reports?userId={memberId}");
            Assert.Equal(1, mine.GetProperty("total").GetInt32());
            Assert.Equal("BugReport", mine.GetProperty("items")[0].GetProperty("kind").GetString());
            Assert.Equal("The export never finished", mine.GetProperty("items")[0].GetProperty("message").GetString());
            Assert.Contains(list.GetProperty("facets").GetProperty("users").EnumerateArray(), x => x.GetProperty("label").GetString() == "rep1member");
            Assert.Equal(2, (await GetJsonAsync(staff, "/api/admin/reports?kind=PageError")).GetProperty("total").GetInt32());
            Assert.Equal(1, (await GetJsonAsync(staff, "/api/admin/reports?search=export%20never")).GetProperty("total").GetInt32());
            Assert.Equal(0, (await GetJsonAsync(staff, $"/api/admin/reports?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"))}"))
                .GetProperty("total").GetInt32());

            // Downloads: CSV and JSON of what the filters match
            var csv = await staff.GetAsync("/api/admin/reports/download?kind=BugReport", ct);
            Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
            var csvText = await csv.Content.ReadAsStringAsync(ct);
            Assert.Contains("id,time_utc,kind,user", csvText);
            Assert.Contains("rep1member", csvText);
            Assert.Contains("\"The export never finished\nIt sat at 40% for an hour.\"", csvText);
            var json = await staff.GetFromJsonAsync<JsonElement>("/api/admin/reports/download?format=json&kind=PageError", ct);
            Assert.Equal(2, json.GetArrayLength());

            // Groups: the same crash twice is one problem, counted
            Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/auth/reports", new
                { kind = "error", message = "Cannot read properties of null (reading 'seek')", errorType = "TypeError", url = "/" }, ct)).StatusCode);
            var groups = await staff.GetFromJsonAsync<JsonElement>("/api/admin/reports/groups?kind=PageError", ct);
            var seek = groups.EnumerateArray().Single(x => x.GetProperty("message").GetString()!.Contains("seek"));
            Assert.Equal(2, seek.GetProperty("count").GetInt32());
            Assert.Equal(2, seek.GetProperty("people").GetInt32());

            // Moderators need the permission; the Owner deletes
            var (_, moderator, moderatorId) = await OwnerWithModeratorAsync("repmod");
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.GetAsync("/api/admin/reports", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(staff, moderatorId, "ViewErrorReports"));
            Assert.Equal(HttpStatusCode.OK, (await moderator.GetAsync("/api/admin/reports", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/admin/reports", ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/admin/reports/{ownerCrash.GetProperty("id").GetInt64()}", ct)).StatusCode);
            var deleted = await (await staff.DeleteAsync("/api/admin/reports?kind=PageError", ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(2, deleted.GetProperty("deleted").GetInt32());
            Assert.Equal(0, (await GetJsonAsync(staff, "/api/admin/reports?kind=PageError")).GetProperty("total").GetInt32());
        }

        [Fact]
        public async Task ServerAndJobErrorsBecomeReports()
        {
            var ct = TestContext.Current.CancellationToken;
            var (_, _, teamId, memberId) = await TeamWithMemberAsync("rep2");
            var entryId = AddEntry(teamId, memberId, "Broken clip", 471);

            // A background job's error names its entry: the report says whose clip it was
            var logger = _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TwitchDownloaderWeb.Services.DownloadWorker");
            logger.LogError(new IOException("The process cannot access the file because token=abc123secret is in use"),
                "Download job for entry {EntryId} failed.", entryId);

            var staff = await StaffClientAsync();
            JsonElement report = default;
            for (var i = 0; i < 50; i++)
            {
                var list = await GetJsonAsync(staff, "/api/admin/reports?kind=JobError");
                if (list.GetProperty("total").GetInt32() > 0)
                {
                    report = list.GetProperty("items")[0];
                    break;
                }
                await Task.Delay(100, ct);
            }
            Assert.Equal(JsonValueKind.Object, report.ValueKind);
            Assert.Equal("DownloadWorker", report.GetProperty("area").GetString());
            Assert.Equal("IOException", report.GetProperty("errorType").GetString());
            Assert.Equal(entryId, report.GetProperty("entryId").GetInt32());
            Assert.Equal(memberId, report.GetProperty("userId").GetInt32());
            Assert.Equal("rep2member", report.GetProperty("username").GetString());
            Assert.Equal(471, report.GetProperty("vodId").GetInt64());
            Assert.DoesNotContain("abc123secret", report.GetProperty("message").GetString());
            var full = await GetJsonAsync(staff, $"/api/admin/reports/{report.GetProperty("id").GetInt64()}");
            Assert.Contains($"Download job for entry {entryId} failed.", full.GetProperty("details").GetString());
            Assert.DoesNotContain("abc123secret", full.GetProperty("details").GetString());
        }
    }
}
