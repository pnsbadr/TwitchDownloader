using System.Net;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>"The VOD is the project" (badrfly, 2026-11-17): edits belong to a clip or video, library filters, work per VOD.</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task EditsBelongToTheirClipOrVideoAndLibrariesFilterByVodAndMember()
        {
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("vw1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, clipper);

            var clipA = AddStoredClip(teamId, ownerId, 490, 10, 30, "a");
            var clipB = AddStoredClip(teamId, ownerId, 491, 10, 30, "b");
            var s1 = await SegmentIdAsync(owner, 490, 100, 110);
            var s2 = await SegmentIdAsync(owner, 490, 200, 210);
            await SegmentIdAsync(owner, 490, 300, null); // a moment
            var videoId = (await PostJsonAsync(owner, "/api/vods/490/videos", new { title = "Pair", markerIds = new[] { s1, s2 } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();

            // The member's edit of clip A: its VOD and clip; the piece must be from VOD 490
            var edit = await PostJsonAsync(member, "/api/projects", new { title = "Vert 1", entryId = clipA, content = TwoTracks(Piece("a", "t1", 0, 490, 10, 30)) },
                HttpStatusCode.Created);
            Assert.Equal(490, edit.GetProperty("vodId").GetInt64());
            Assert.Equal(clipA, edit.GetProperty("entryId").GetInt32());
            await PostJsonAsync(member, "/api/projects", new { entryId = clipA, content = TwoTracks(Piece("a", "t1", 0, 491, 10, 30)) }, HttpStatusCode.BadRequest);
            var videoEdit = await PostJsonAsync(owner, "/api/projects", new { videoId }, HttpStatusCode.Created);
            Assert.Equal(490, videoEdit.GetProperty("vodId").GetInt64());
            Assert.Equal(videoId, videoEdit.GetProperty("videoId").GetInt32());
            await PostJsonAsync(owner, "/api/projects", new { entryId = 99999 }, HttpStatusCode.BadRequest);

            // A copy is another edit of the same clip
            var copy = await PostJsonAsync(owner, $"/api/projects/{edit.GetProperty("id").GetInt32()}/copy", new { }, HttpStatusCode.Created);
            Assert.Equal(clipA, copy.GetProperty("entryId").GetInt32());
            Assert.Equal(2, (await GetJsonAsync(owner, $"/api/projects?entryId={clipA}")).GetArrayLength());

            // Clip filters: VOD, made by, edited by
            static int[] Ids(JsonElement list) => list.EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).Order().ToArray();
            Assert.Equal([clipA], Ids(await GetJsonAsync(owner, "/api/entries?vodId=490")));
            Assert.Equal([clipA, clipB], Ids(await GetJsonAsync(owner, $"/api/entries?madeBy={ownerId}")));
            Assert.Empty(Ids(await GetJsonAsync(owner, $"/api/entries?madeBy={memberId}")));
            Assert.Equal([clipA], Ids(await GetJsonAsync(owner, $"/api/entries?editedBy={memberId}")));

            // Video filters
            Assert.Equal([videoId], Ids(await GetJsonAsync(owner, $"/api/videos?editedBy={ownerId}")));
            Assert.Empty(Ids(await GetJsonAsync(owner, $"/api/videos?editedBy={memberId}")));
            // A member who can't see teammates' vertical edits isn't told who made them
            Assert.Empty(Ids(await GetJsonAsync(member, $"/api/videos?editedBy={ownerId}")));
            Assert.Equal([clipA], Ids(await GetJsonAsync(member, $"/api/entries?editedBy={memberId}")));

            // Work per VOD
            var work = await GetJsonAsync(owner, "/api/vods/work");
            var vod490 = work.EnumerateArray().Single(x => x.GetProperty("vodId").GetInt64() == 490);
            Assert.Equal(1, vod490.GetProperty("clips").GetInt32());
            Assert.Equal(1, vod490.GetProperty("videos").GetInt32());
            Assert.Equal(2, vod490.GetProperty("segments").GetInt32());
            Assert.Equal(1, vod490.GetProperty("moments").GetInt32());
            Assert.Equal(3, vod490.GetProperty("edits").GetInt32());
            Assert.Single((await GetJsonAsync(owner, "/api/vods/work?vodIds=491,492")).EnumerateArray());

            // Deleting the clip keeps its edits, on the VOD
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/entries/{clipA}", TestContext.Current.CancellationToken)).StatusCode);
            var kept = await GetJsonAsync(owner, $"/api/projects/{edit.GetProperty("id").GetInt32()}");
            Assert.Equal(JsonValueKind.Null, kept.GetProperty("entryId").ValueKind);
            Assert.Equal(490, kept.GetProperty("vodId").GetInt64());
        }
    }
}
