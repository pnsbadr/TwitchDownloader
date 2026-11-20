using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Synced rows on Player 1 and videos across rows and VODs (badrfly 2026-11-20).</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task SyncedRowsShowAnotherVodLinedUpAndVideosCrossThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("sr1");
            var staff = await StaffClientAsync();
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "bonnie" }, ct)).StatusCode);
            var arkyStart = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);
            _vods.Vods[720] = new VodDetails(720, "Arky collab", "Arky", 4 * 3600, arkyStart, null);
            _vods.Vods[821] = new VodDetails(821, "Bonnie collab", "Bonnie", 3 * 3600, arkyStart.AddMinutes(30), null, ChannelLogin: "bonnie");

            // Not the VOD itself
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/vods/720/rows", new { kind = "video", syncedVodId = 720 }, ct)).StatusCode);

            // A row of Bonnie's VOD shown on Arky's: Bonnie started 30 min later, so her second = Arky's - 1800
            var made = await owner.PostAsJsonAsync("/api/vods/720/rows", new { kind = "video", syncedVodId = 821 }, ct);
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
            var row = await made.Content.ReadFromJsonAsync<JsonElement>(ct);
            var rowId = row.GetProperty("id").GetInt32();
            Assert.Equal(821, row.GetProperty("vodId").GetInt64());
            Assert.Equal(720, row.GetProperty("shownOnVodId").GetInt64());
            Assert.Equal(-1800, row.GetProperty("shift").GetDouble());
            Assert.Equal("Bonnie", row.GetProperty("name").GetString());

            // The pair's line-up moves it
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/vods/720/sync/821", new { at = 3600, otherAt = 1802 }, ct)).StatusCode);

            // Its items are Bonnie's VOD's
            var theirs = await SegmentIdAsync(owner, 821, 1810, 1820, rowId);
            var mine = await SegmentIdAsync(owner, 720, 100, 110);
            var timeline = await GetJsonAsync(owner, "/api/vods/720/timeline");
            var shown = timeline.GetProperty("rows").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == rowId);
            Assert.Equal(-1798, shown.GetProperty("shift").GetDouble());
            Assert.Contains(timeline.GetProperty("markers").EnumerateArray(), x => x.GetProperty("id").GetInt32() == theirs && x.GetProperty("vodId").GetInt64() == 821);
            // ... and show on Bonnie's VOD as a row of it (no shift there)
            var own = await GetJsonAsync(owner, "/api/vods/821/timeline");
            var there = own.GetProperty("rows").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == rowId);
            Assert.Equal(JsonValueKind.Null, there.GetProperty("shift").ValueKind);
            Assert.Contains(own.GetProperty("markers").EnumerateArray(), x => x.GetProperty("id").GetInt32() == theirs);

            // A video of both VODs' segments
            var video = await owner.PostAsJsonAsync("/api/vods/720/videos", new { title = "Both sides", markerIds = new[] { mine, theirs } }, ct);
            Assert.Equal(HttpStatusCode.Created, video.StatusCode);
            var made2 = await video.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal([720L, 821L], made2.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("vodId").GetInt64()));
            Assert.Equal(20, made2.GetProperty("lengthSeconds").GetDouble());
            Assert.Single((await GetJsonAsync(owner, "/api/vods/720/timeline")).GetProperty("videos").EnumerateArray());
            Assert.Single((await GetJsonAsync(owner, "/api/vods/821/timeline")).GetProperty("videos").EnumerateArray());

            // Deleting the synced row takes its segment out of the video; the video stays with the other
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/rows/{rowId}", ct)).StatusCode);
            var left = await GetJsonAsync(owner, $"/api/videos/{made2.GetProperty("id").GetInt32()}");
            Assert.Equal([mine], left.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("markerId").GetInt32()));
        }
    }
}
