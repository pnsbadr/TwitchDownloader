using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Interfaces;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Downloading picked segments: one file each, or joined for teams the app Owner turned it on for. Never library entries.</summary>
    public sealed partial class AccountsTests
    {
        /// <summary>Every VOD has 1080p60 and 720p30.</summary>
        private sealed class FakeQualities : IVodQualitySource
        {
            private const string Master = """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60",AUTOSELECT=NO,DEFAULT=NO
                #EXT-X-STREAM-INF:BANDWIDTH=6000000,CODECS="avc1.64002A,mp4a.40.2",RESOLUTION=1920x1080,VIDEO="chunked",FRAME-RATE=60.000
                https://example.test/chunked/index-dvr.m3u8
                #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p30",NAME="720p",AUTOSELECT=YES,DEFAULT=YES
                #EXT-X-STREAM-INF:BANDWIDTH=2000000,CODECS="avc1.4D401F,mp4a.40.2",RESOLUTION=1280x720,VIDEO="720p30",FRAME-RATE=30.000
                https://example.test/720p30/index-dvr.m3u8
                """;

            public Task<IVideoQualities<M3U8.Stream>?> GetQualitiesAsync(long vodId) =>
                Task.FromResult<IVideoQualities<M3U8.Stream>?>(VideoQualities.FromM3U8(M3U8.Parse(Master)));
        }

        /// <summary>Makes a small real mp4 (test pattern + tone, encoded like an Exact cut) as long as the cut. A cut starting at 666 s fails.</summary>
        private sealed class FakeCutter : IVodCutter
        {
            public static readonly string Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } p ? p : "ffmpeg";

            public readonly System.Collections.Concurrent.ConcurrentQueue<LibraryEntry> Cuts = new();

            public async Task<AudioRestoreResult?> CutAsync(LibraryDbContext db, LibraryEntry cut, string outputPath, ILogger logger, Action<string> onRestoreFailed,
                CancellationToken cancellationToken)
            {
                Cuts.Enqueue(cut);
                if (cut.StartTime == TimeSpan.FromSeconds(666))
                    throw new InvalidOperationException("Twitch said no");

                var length = (cut.EndTime!.Value - cut.StartTime).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await FfmpegRunner.RunAsync(Ffmpeg,
                [
                    "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                    "-t", length, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outputPath,
                ], Path.GetDirectoryName(outputPath)!, logger, cancellationToken);
                return null;
            }
        }

        private static bool HasFfmpeg()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(FakeCutter.Ffmpeg, "-version") { RedirectStandardOutput = true, UseShellExecute = false });
                process!.WaitForExit();
                return process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static async Task<int> AddSegmentAsync(HttpClient client, long vodId, double start, double? end, string label)
        {
            var response = await client.PostAsJsonAsync($"/api/vods/{vodId}/markers", new { seconds = start, endSeconds = end, label }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt32();
        }

        private static async Task<JsonElement> WaitForSegmentDownloadAsync(HttpClient client, int id)
        {
            for (var i = 0; i < 300; i++)
            {
                var export = await GetJsonAsync(client, $"/api/segment-downloads/{id}");
                if (export.GetProperty("status").GetString() is "Ready" or "Failed")
                    return export;
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException($"Segment download {id} didn't finish.");
        }

        private static double Duration(string file)
        {
            using var process = Process.Start(new ProcessStartInfo("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file])
                { RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return double.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }

        [Fact]
        public async Task PickedSegmentsDownloadAsOneFileEachAndNeverBecomeEntries()
        {
            if (!HasFfmpeg())
                Assert.Skip("ffmpeg not found; set FFMPEG_PATH.");
            var ct = TestContext.Current.CancellationToken;
            _vods.Vods[50] = new VodDetails(50, "Big stream", "arky", 600, DateTime.UtcNow.AddDays(-1), null);
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("sg1");
            var entryId = AddEntry(teamId, memberId, "🟢 I GOT MAIL? 🟢 !newsong");
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var entry = db.Entries.Single(x => x.Id == entryId);
                entry.VodId = 50;
                entry.Quality = "720p30";
                db.SaveChanges();
            }

            var b = await AddSegmentAsync(owner, 50, 30, 31.5, "Bit / two");
            var a = await AddSegmentAsync(owner, 50, 10, 12, "Intro");
            var moment = await AddSegmentAsync(owner, 50, 40, null, "Moment");

            // Only segments, at least one, on this VOD, and joining needs the Owner's switch
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/vods/50/segment-downloads", new { markerIds = Array.Empty<int>() }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/vods/50/segment-downloads", new { markerIds = new[] { a, moment } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/vods/51/segment-downloads", new { markerIds = new[] { a } }, ct)).StatusCode);
            var combined = await owner.PostAsJsonAsync("/api/vods/50/segment-downloads", new { markerIds = new[] { a }, combined = true }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, combined.StatusCode);
            Assert.Equal("combine-not-enabled", (await combined.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
            Assert.False((await GetJsonAsync(owner, "/api/auth/me")).GetProperty("combineSegmentDownloads").GetBoolean());

            // Making files needs GenerateFiles; viewing is enough to download them
            var viewer = await CreateRoleAsync(owner, "Viewer", "ViewLibrary");
            await AssignAsync(owner, memberId, viewer);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/vods/50/segment-downloads", new { markerIds = new[] { a } }, ct)).StatusCode);

            var entriesBefore = (await GetJsonAsync(owner, "/api/entries")).GetArrayLength();
            var created = await owner.PostAsJsonAsync("/api/vods/50/segment-downloads", new { markerIds = new[] { b, a }, entryId }, ct);
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            var export = await WaitForSegmentDownloadAsync(member, id);
            Assert.Equal("Ready", export.GetProperty("status").GetString());
            Assert.Equal("720p30", export.GetProperty("quality").GetString());
            Assert.Equal(JsonValueKind.Null, export.GetProperty("combinedFileUrl").ValueKind);
            var items = export.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["Intro", "Bit / two"], items.Select(x => x.GetProperty("label").GetString()));
            Assert.All(items, x => Assert.Equal("Ready", x.GetProperty("status").GetString()));

            // Cut like a library entry: Exact, the card's quality, the segment's own range
            var cuts = _cutter.Cuts.Where(x => x.VodId == 50).OrderBy(x => x.StartTime).ToList();
            Assert.All(cuts, x => Assert.Equal(VideoTrimMode.Exact, x.TrimMode));
            Assert.Equal([10.0, 30.0], cuts.Select(x => x.StartTime.TotalSeconds));
            Assert.Equal([12.0, 31.5], cuts.Select(x => x.EndTime!.Value.TotalSeconds));
            Assert.All(cuts, x => Assert.Equal("720p30", x.Quality));

            // No new library cards
            Assert.Equal(entriesBefore, (await GetJsonAsync(owner, "/api/entries")).GetArrayLength());

            var file = await member.GetAsync(items[1].GetProperty("fileUrl").GetString(), ct);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("video/mp4", file.Content.Headers.ContentType!.MediaType);
            Assert.Contains("02 Bit", file.Content.Headers.ContentDisposition!.FileNameStar ?? file.Content.Headers.ContentDisposition.FileName);

            var zip = await member.GetAsync(export.GetProperty("zipUrl").GetString(), ct);
            Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
            // The title's emoji reach the file name (filename*), and the header stays ASCII
            Assert.Equal("🟢 I GOT MAIL？ 🟢 !newsong - segments.zip", zip.Content.Headers.ContentDisposition!.FileNameStar);
            using (var archive = new ZipArchive(new MemoryStream(await zip.Content.ReadAsByteArrayAsync(ct))))
            {
                Assert.Equal(2, archive.Entries.Count);
                Assert.Equal("01 Intro.mp4", archive.Entries[0].FullName);
                Assert.StartsWith("02 Bit", archive.Entries[1].FullName);
            }

            // Listed for the VOD; another team sees none of it
            Assert.Equal(1, (await GetJsonAsync(owner, "/api/segment-downloads?vodIds=50")).GetArrayLength());
            var staff = await StaffClientAsync();
            var other = NewClient();
            await AcceptAsync(other, await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration", channel = "arky", teamName = "Other" }), "sg1other", newAccount: true, teamName: "Other");
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/segment-downloads/{id}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/segment-downloads/{id}/zip", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync("/api/vods/50/segment-downloads", new { markerIds = new[] { a } }, ct)).StatusCode);

            // Counted in the team's disk use; deleting removes the files
            var folder = Path.Combine(_dir, "downloads", "segments", id.ToString());
            Assert.True(Directory.Exists(folder));
            Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/segment-downloads/{id}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/segment-downloads/{id}", ct)).StatusCode);
            Assert.False(Directory.Exists(folder));
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/segment-downloads/{id}", ct)).StatusCode);
        }

        [Fact]
        public async Task OnlyTheAppOwnerTurnsOnJoinedSegmentDownloads()
        {
            if (!HasFfmpeg())
                Assert.Skip("ffmpeg not found; set FFMPEG_PATH.");
            var ct = TestContext.Current.CancellationToken;
            _vods.Vods[60] = new VodDetails(60, "Stream", "arky", 900, DateTime.UtcNow.AddDays(-1), null);
            var (owner, _, teamId, _) = await TeamWithMemberAsync("sg2");
            var first = await AddSegmentAsync(owner, 60, 100, 102, "One");
            var second = await AddSegmentAsync(owner, 60, 200, 201.5, "Two");

            // A moderator, even one who may see and disable teams, can't; the Owner can
            var (staffOwner, moderator, moderatorId) = await OwnerWithModeratorAsync("sgmod");
            await SetStaffPermissionsAsync(staffOwner, moderatorId, "ViewTeamsAndUsers", "DisableTeams");
            Assert.Equal(HttpStatusCode.Forbidden,
                (await moderator.PutAsJsonAsync($"/api/admin/teams/{teamId}/features", new { combineSegmentDownloads = true }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,
                (await staffOwner.PutAsJsonAsync($"/api/admin/teams/{teamId}/features", new { combineSegmentDownloads = true }, ct)).StatusCode);
            Assert.True((await GetJsonAsync(owner, "/api/auth/me")).GetProperty("combineSegmentDownloads").GetBoolean());
            var teams = await GetJsonAsync(staffOwner, "/api/admin/teams");
            Assert.True(teams.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == teamId).GetProperty("combineSegmentDownloads").GetBoolean());

            var created = await owner.PostAsJsonAsync("/api/vods/60/segment-downloads", new { markerIds = new[] { second, first }, combined = true }, ct);
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var export = await WaitForSegmentDownloadAsync(owner, (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32());
            Assert.Equal("Ready", export.GetProperty("status").GetString());
            Assert.Equal("Stream", export.GetProperty("title").GetString());

            // One file, both segments back to back in time order
            var joined = await owner.GetAsync(export.GetProperty("combinedFileUrl").GetString(), ct);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
            var path = Path.Combine(_dir, "joined.mp4");
            await File.WriteAllBytesAsync(path, await joined.Content.ReadAsByteArrayAsync(ct), ct);
            Assert.InRange(Duration(path), 3.3, 3.7);
            // The separate files are there too
            Assert.All(export.GetProperty("items").EnumerateArray(), x => Assert.Equal("Ready", x.GetProperty("status").GetString()));

            // A segment that fails: the others are kept, nothing is joined, and it says why
            var bad = await AddSegmentAsync(owner, 60, 666, 667, "Bad");
            var partial = await owner.PostAsJsonAsync("/api/vods/60/segment-downloads", new { markerIds = new[] { first, bad }, combined = true }, ct);
            var partialExport = await WaitForSegmentDownloadAsync(owner, (await partial.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32());
            Assert.Equal("Ready", partialExport.GetProperty("status").GetString());
            Assert.Contains("Not joined", partialExport.GetProperty("error").GetString());
            Assert.Equal(JsonValueKind.Null, partialExport.GetProperty("combinedFileUrl").ValueKind);
            Assert.Equal(["Ready", "Failed"], partialExport.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("status").GetString()));

            // Turning it off again stops new joined downloads
            await staffOwner.PutAsJsonAsync($"/api/admin/teams/{teamId}/features", new { combineSegmentDownloads = false }, ct);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await owner.PostAsJsonAsync("/api/vods/60/segment-downloads", new { markerIds = new[] { first }, combined = true }, ct)).StatusCode);
        }

        [Fact]
        public async Task SegmentsNotStreamedYetCantBeDownloaded()
        {
            var ct = TestContext.Current.CancellationToken;
            _vods.Vods[70] = new VodDetails(70, "Live now", "arky", 300, DateTime.UtcNow, null, IsLive: true);
            var (owner, _, _, _) = await TeamWithMemberAsync("sg3");
            var future = await AddSegmentAsync(owner, 70, 290, 320, "Later");
            var response = await owner.PostAsJsonAsync("/api/vods/70/segment-downloads", new { markerIds = new[] { future } }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("streamed", await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PostAsJsonAsync("/api/vods/70/segment-downloads", new { markerIds = new[] { future }, quality = "4k" }, ct)).StatusCode);
        }
    }
}
