using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Timeline rows: each member's own lanes on a VOD (badrfly, 2026-11-15, frontend rework).</summary>
    public sealed partial class AccountsTests
    {
        private static JsonElement[] RowsOf(JsonElement timeline, int userId) =>
            timeline.GetProperty("rows").EnumerateArray().Where(x => x.GetProperty("userId").GetInt32() == userId).ToArray();

        [Fact]
        public async Task MembersGetAVideoAndAClipRowTheFirstTimeTheyOpenAVod()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("rw1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();

            // A viewer gets no rows (they can't put anything on them)
            var viewer = await CreateRoleAsync(owner, "Viewer", "ViewLibrary");
            await AssignAsync(owner, memberId, viewer);
            Assert.Empty(RowsOf(await GetJsonAsync(member, "/api/vods/400/timeline"), memberId));

            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, clipper);
            var rows = RowsOf(await GetJsonAsync(member, "/api/vods/400/timeline"), memberId);
            Assert.Equal(["Video", "Clip"], rows.Select(x => x.GetProperty("kind").GetString()));
            Assert.Equal([0, 1], rows.Select(x => x.GetProperty("position").GetInt32()));
            Assert.All(rows, x => Assert.True(x.GetProperty("canEdit").GetBoolean()));
            Assert.Equal("rw1member", rows[0].GetProperty("username").GetString());

            // Loading again doesn't add more; rows don't carry over to other VODs
            Assert.Equal(2, RowsOf(await GetJsonAsync(member, "/api/vods/400/timeline"), memberId).Length);
            Assert.Empty((await GetJsonAsync(member, "/api/vods/401/timeline")).GetProperty("rows").EnumerateArray()
                .Where(x => x.GetProperty("userId").GetInt32() == ownerId));

            // Others see the rows but can't change them; the caller's own rows come first
            var seenByOwner = await GetJsonAsync(owner, "/api/vods/400/timeline");
            Assert.Equal(ownerId, seenByOwner.GetProperty("rows")[0].GetProperty("userId").GetInt32());
            Assert.All(RowsOf(seenByOwner, memberId), x => Assert.False(x.GetProperty("canEdit").GetBoolean()));
            var videoRow = rows[0].GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"/api/rows/{videoRow}", new { name = "Mine now" }, ct)).StatusCode);

            // A deleted row doesn't come back on the next load
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/rows/{videoRow}", ct)).StatusCode);
            Assert.Equal(["Clip"], RowsOf(await GetJsonAsync(member, "/api/vods/400/timeline"), memberId).Select(x => x.GetProperty("kind").GetString()));
        }

        [Fact]
        public async Task SegmentsAndClipsSitOnTheirCreatorsRows()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("rw2");
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries", "EditOwnEntries", "DeleteOwnEntries");
            await AssignAsync(owner, memberId, clipper);

            // A marker added before the VOD was opened makes the member's first video row
            var first = await (await member.PostAsJsonAsync("/api/vods/410/markers", new { seconds = 5, endSeconds = 9, label = "A" }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            var timeline = await GetJsonAsync(member, "/api/vods/410/timeline");
            var mine = RowsOf(timeline, memberId);
            var videoRow = mine.Single(x => x.GetProperty("kind").GetString() == "Video").GetProperty("id").GetInt32();
            var clipRow = mine.Single(x => x.GetProperty("kind").GetString() == "Clip").GetProperty("id").GetInt32();
            Assert.Equal(videoRow, first.GetProperty("rowId").GetInt32());

            // A second video row; segments can be put on it or moved there, never on a clip row or someone else's row
            var added = await member.PostAsJsonAsync("/api/vods/410/rows", new { kind = "video", name = "Funny" }, ct);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            var secondRow = (await added.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/410/rows", new { kind = "audio" }, ct)).StatusCode);

            var onSecond = await member.PostAsJsonAsync("/api/vods/410/markers", new { seconds = 20, endSeconds = 30, rowId = secondRow }, ct);
            Assert.Equal(HttpStatusCode.Created, onSecond.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/410/markers", new { seconds = 1, rowId = clipRow }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/411/markers", new { seconds = 1, rowId = videoRow }, ct)).StatusCode);

            var ownerTimeline = await GetJsonAsync(owner, "/api/vods/410/timeline");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var ownerVideoRow = RowsOf(ownerTimeline, ownerId).Single(x => x.GetProperty("kind").GetString() == "Video").GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/410/markers", new { seconds = 1, rowId = ownerVideoRow }, ct)).StatusCode);

            var markerId = first.GetProperty("id").GetInt32();
            var moved = await member.PutAsJsonAsync($"/api/markers/{markerId}", new { seconds = 5, endSeconds = 9, label = "A", color = "yellow", rowId = secondRow }, ct);
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            Assert.Equal(secondRow, (await moved.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("rowId").GetInt32());
            // The owner (EditAnyEntries) may change the member's segment, but it stays on one of the member's rows
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PutAsJsonAsync($"/api/markers/{markerId}", new { seconds = 5, endSeconds = 9, label = "A", rowId = ownerVideoRow }, ct)).StatusCode);

            // Clips go on the adder's clip row; they can move to another clip row of theirs
            var entryId = AddEntry(teamId, memberId, "Clip on 410", vodId: 410);
            var entry = await GetJsonAsync(member, $"/api/entries/{entryId}");
            Assert.Equal(clipRow, entry.GetProperty("rowId").GetInt32());
            var timelineClip = (await GetJsonAsync(owner, "/api/vods/410/timeline")).GetProperty("clips").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == entryId);
            Assert.Equal("rw2member", timelineClip.GetProperty("createdBy").GetString());
            Assert.Equal(clipRow, timelineClip.GetProperty("rowId").GetInt32());

            var secondClipRow = (await (await member.PostAsJsonAsync("/api/vods/410/rows", new { kind = "clip" }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct))
                .GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PutAsJsonAsync($"/api/entries/{entryId}/row", new { rowId = videoRow }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await member.PutAsJsonAsync($"/api/entries/{entryId}/row", new { rowId = secondClipRow }, ct)).StatusCode);

            // A clip row with clips isn't deleted; a video row goes with its segments
            var refused = await member.DeleteAsync($"/api/rows/{secondClipRow}", ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("row-has-clips", await CodeOf(refused));
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/rows/{secondRow}", ct)).StatusCode);
            Assert.DoesNotContain((await GetJsonAsync(member, "/api/vods/410/timeline")).GetProperty("markers").EnumerateArray(),
                x => x.GetProperty("id").GetInt32() == markerId);

            // Reordering own rows
            var reordered = await member.PutAsJsonAsync($"/api/rows/{secondClipRow}", new { position = 0, name = "Best" }, ct);
            Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
            var order = RowsOf(await GetJsonAsync(member, "/api/vods/410/timeline"), memberId).ToList();
            Assert.Equal(secondClipRow, order[0].GetProperty("id").GetInt32());
            Assert.Equal("Best", order[0].GetProperty("name").GetString());
            Assert.Equal([0, 1, 2], order.Select(x => x.GetProperty("position").GetInt32()));

            // DeleteAnyEntries may delete someone else's row
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/rows/{videoRow}", ct)).StatusCode);
        }
    }
}
