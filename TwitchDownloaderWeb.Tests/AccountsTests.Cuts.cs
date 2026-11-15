using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Cut-out parts on segments, clips and videos (badrfly, 2026-11-15, frontend rework).</summary>
    public sealed partial class AccountsTests
    {
        private static double[][] CutsOf(JsonElement item) =>
            item.GetProperty("cuts").EnumerateArray().Select(x => new[] { x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble() }).ToArray();

        [Fact]
        public void CutOutPartsAreClippedMergedAndNeverLeaveSlivers()
        {
            // Clipped to the range, overlaps merged, a gap under 0.5 s between cuts or at an end is cut too
            Assert.Equal([new CutRange(10, 13.8), new CutRange(16, 20)],
                CutOuts.Normalize([new CutRange(16, 30), new CutRange(5, 12), new CutRange(11, 13.8)], 10, 20));
            Assert.Equal([new CutRange(12, 15)], CutOuts.Normalize([new CutRange(12, 13), new CutRange(13.3, 15)], 10, 20));
            Assert.Equal([(10.0, 12.0), (15.0, 20.0)], CutOuts.Kept(10, 20, [new CutRange(12, 15)]));
            Assert.Equal(7, CutOuts.KeptSeconds(10, 20, [new CutRange(12, 15)]));
            Assert.Equal("The cut-out parts leave nothing.", CutOuts.Validate([new CutRange(0, 100)], 10, 20));
            Assert.NotNull(CutOuts.Validate([new CutRange(5, 4)], 10, 20));
            Assert.Null(CutOuts.Validate([new CutRange(12, 15)], 10, 20));
        }

        [Fact]
        public async Task SegmentsKeepTheirCutOutPartsThroughEdits()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("ct1");

            // Only segments, and something must be left
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PostAsJsonAsync("/api/vods/440/markers", new { seconds = 5, cuts = new[] { new { start = 5, end = 6 } } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PostAsJsonAsync("/api/vods/440/markers", new { seconds = 10, endSeconds = 20, cuts = new[] { new { start = 0, end = 30 } } }, ct)).StatusCode);

            var made = await owner.PostAsJsonAsync("/api/vods/440/markers",
                new { seconds = 10, endSeconds = 30, label = "Fight", cuts = new[] { new { start = 12, end = 15 }, new { start = 25, end = 40 } } }, ct);
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
            var segment = await made.Content.ReadFromJsonAsync<JsonElement>(ct);
            var id = segment.GetProperty("id").GetInt32();
            Assert.Equal([[12.0, 15.0], [25.0, 30.0]], CutsOf(segment));
            Assert.Equal(12, segment.GetProperty("keptSeconds").GetDouble());

            // A change without cuts keeps them (clipped to the new range); [] removes them
            var trimmed = await (await owner.PutAsJsonAsync($"/api/markers/{id}", new { seconds = 10, endSeconds = 20, label = "Fight" }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal([[12.0, 15.0]], CutsOf(trimmed));
            Assert.Equal(7, trimmed.GetProperty("keptSeconds").GetDouble());
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PutAsJsonAsync($"/api/markers/{id}", new { seconds = 10, endSeconds = 20, cuts = new[] { new { start = 10, end = 20 } } }, ct)).StatusCode);
            var cleared = await (await owner.PutAsJsonAsync($"/api/markers/{id}", new { seconds = 10, endSeconds = 20, cuts = Array.Empty<object>() }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Empty(CutsOf(cleared));

            // Copies take them along
            Assert.Equal(HttpStatusCode.OK,
                (await owner.PutAsJsonAsync($"/api/markers/{id}", new { seconds = 10, endSeconds = 20, cuts = new[] { new { start = 11, end = 12 } } }, ct)).StatusCode);
            var copy = await (await owner.PostAsJsonAsync($"/api/markers/{id}/copy", new { }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal([[11.0, 12.0]], CutsOf(copy));
            var timeline = await GetJsonAsync(owner, "/api/vods/440/timeline");
            Assert.Equal(9, timeline.GetProperty("markers").EnumerateArray().First(x => x.GetProperty("id").GetInt32() == id).GetProperty("keptSeconds").GetDouble());
        }

        [Fact]
        public async Task ClipLimitCountsWhatsLeftAfterCutOutParts()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ct2");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var entryId = AddEntry(teamId, ownerId, "Long one", vodId: 441);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var entry = db.Entries.Single(x => x.Id == entryId);
                entry.VodLengthSeconds = 3600;
                entry.EndTime = TimeSpan.FromSeconds(600);
                db.SaveChanges();
            }

            // 200 s with 30 s cut out = 170 s: under the 180 s limit, so it's stored
            var cut = await owner.PutAsJsonAsync($"/api/entries/{entryId}/trim",
                new { startTime = "100", endTime = "300", cuts = new[] { new { start = 150, end = 180 } } }, ct);
            Assert.Equal(HttpStatusCode.OK, cut.StatusCode);
            var entryDto = await cut.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(170, entryDto.GetProperty("clipLengthSeconds").GetDouble());
            Assert.Equal([[150.0, 180.0]], CutsOf(entryDto));
            Assert.False(entryDto.GetProperty("isReferenceOnly").GetBoolean());

            // Without the cut it's 200 s: reference-only again
            var uncut = await (await owner.PutAsJsonAsync($"/api/entries/{entryId}/trim", new { startTime = "100", endTime = "300", cuts = Array.Empty<object>() }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("ReferenceOnly", uncut.GetProperty("status").GetString());
            Assert.Equal(200, uncut.GetProperty("clipLengthSeconds").GetDouble());

            // The timeline shows the clip's cuts and what's left
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/entries/{entryId}/trim",
                new { startTime = "100", endTime = "300", cuts = new[] { new { start = 110, end = 120 } } }, ct)).StatusCode);
            var clip = (await GetJsonAsync(owner, "/api/vods/441/timeline")).GetProperty("clips").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == entryId);
            Assert.Equal([[110.0, 120.0]], CutsOf(clip));
            Assert.Equal(190, clip.GetProperty("lengthSeconds").GetDouble());
        }

        [Fact]
        public async Task VideosCutPartsOutOfTheirSegmentsWithoutChangingThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("ct3");
            var a = await SegmentIdAsync(owner, 442, 10, 20);
            var bMade = await owner.PostAsJsonAsync("/api/vods/442/markers", new { seconds = 40, endSeconds = 50, cuts = new[] { new { start = 40, end = 42 } } }, ct);
            var b = (await bMade.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            var created = await owner.PostAsJsonAsync("/api/vods/442/videos",
                new { title = "Tight", markerIds = new[] { a, b }, cuts = new[] { new { markerId = a, cuts = new[] { new { start = 15, end = 17 } } } } }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var video = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
            var videoId = video.GetProperty("id").GetInt32();
            // a: 10 s - 2 s cut in the video; b: 10 s - 2 s of its own
            Assert.Equal(16, video.GetProperty("lengthSeconds").GetDouble());
            var segs = video.GetProperty("segments").EnumerateArray().ToList();
            Assert.Equal([[15.0, 17.0]], segs[0].GetProperty("videoCuts").EnumerateArray()
                .Select(x => new[] { x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble() }).ToArray());
            Assert.Equal([[40.0, 42.0]], CutsOf(segs[1]));
            // The segment itself is untouched
            var markerA = (await GetJsonAsync(owner, "/api/vods/442/timeline")).GetProperty("markers").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == a);
            Assert.Empty(CutsOf(markerA));

            // Only its own segments, and something must be left
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/videos/{videoId}",
                new { cuts = new[] { new { markerId = 999999, cuts = new[] { new { start = 1, end = 2 } } } } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/videos/{videoId}",
                new { cuts = new[] { new { markerId = b, cuts = new[] { new { start = 42, end = 50 } } } } }, ct)).StatusCode);

            // Reordering keeps a segment's cuts in the video; copies keep them too
            var reordered = await (await owner.PutAsJsonAsync($"/api/videos/{videoId}", new { markerIds = new[] { b, a } }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(16, reordered.GetProperty("lengthSeconds").GetDouble());
            var copy = await (await owner.PostAsJsonAsync($"/api/videos/{videoId}/copy", new { }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(16, copy.GetProperty("lengthSeconds").GetDouble());

            // Trimming a segment so a video cut falls outside drops it from the length
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/markers/{a}", new { seconds = 10, endSeconds = 14 }, ct)).StatusCode);
            Assert.Equal(12, (await GetJsonAsync(owner, $"/api/videos/{videoId}")).GetProperty("lengthSeconds").GetDouble());
        }

        [Fact]
        public async Task SegmentFilesSkipTheirCutOutParts()
        {
            if (!HasFfmpeg())
                Assert.Skip("ffmpeg not found; set FFMPEG_PATH.");
            var ct = TestContext.Current.CancellationToken;
            _vods.Vods[443] = new VodDetails(443, "Cut stream", "arky", 600, DateTime.UtcNow.AddDays(-1), null);
            var (owner, _, _, _) = await TeamWithMemberAsync("ct4");
            var made = await owner.PostAsJsonAsync("/api/vods/443/markers",
                new { seconds = 100, endSeconds = 106, label = "Cut", cuts = new[] { new { start = 102, end = 104 } } }, ct);
            var id = (await made.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            var started = await owner.PostAsJsonAsync("/api/vods/443/segment-downloads", new { markerIds = new[] { id } }, ct);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var export = await WaitForSegmentDownloadAsync(owner, (await started.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32());
            Assert.Equal("Ready", export.GetProperty("status").GetString());
            var item = export.GetProperty("items")[0];
            Assert.Equal([[102.0, 104.0]], CutsOf(item));

            // The two kept parts were cut separately (Exact) and joined: 4 s in all
            var parts = _cutter.Cuts.Where(x => x.VodId == 443).OrderBy(x => x.StartTime).ToList();
            Assert.Equal([(100.0, 102.0), (104.0, 106.0)], parts.Select(x => (x.StartTime.TotalSeconds, x.EndTime!.Value.TotalSeconds)));
            var file = Path.Combine(Path.GetTempPath(), $"cut-{Guid.NewGuid():N}.mp4");
            await File.WriteAllBytesAsync(file, await owner.GetByteArrayAsync(item.GetProperty("fileUrl").GetString(), ct), ct);
            try
            {
                Assert.InRange(Duration(file), 3.8, 4.3);
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}
