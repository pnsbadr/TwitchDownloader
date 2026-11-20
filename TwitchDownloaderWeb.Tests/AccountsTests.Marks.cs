using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>A member's own stars and recently opened items (badrfly 2026-11-20, "Redesign the library for large teams", step 5).</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task MembersStarAndOpenTheirOwnItems()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("mk1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var viewer = await CreateRoleAsync(owner, "Viewer", "ViewLibrary");
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId, viewer));

            var a = AddEntry(teamId, ownerId, "Knocked out", vodId: 730);
            var b = AddEntry(teamId, ownerId, "Rage quit", vodId: 730);
            List<int> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
            List<long> Marks(JsonElement marks, string which, string kind) =>
                marks.GetProperty(which).GetProperty(kind).EnumerateArray().Select(x => x.GetInt64()).ToList();

            // Stars are personal: viewing the library is enough, and teammates don't see them
            Assert.Equal(HttpStatusCode.NoContent, (await member.PutAsync($"/api/my/stars/clip/{a}", null, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await member.PutAsync("/api/my/stars/vod/730", null, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await member.PutAsync($"/api/my/stars/clip/{a}", null, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsync("/api/my/stars/clip/999999", null, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PutAsync($"/api/my/stars/thing/{a}", null, ct)).StatusCode);
            var marks = await GetJsonAsync(member, "/api/my/marks");
            Assert.Equal([a], Marks(marks, "starred", "clips"));
            Assert.Equal([730L], Marks(marks, "starred", "vods"));
            Assert.Empty(Marks(await GetJsonAsync(owner, "/api/my/marks"), "starred", "clips"));
            Assert.Equal([a], Ids(await GetJsonAsync(member, "/api/entries?paged=true&mark=starred")));
            Assert.Empty(Ids(await GetJsonAsync(owner, "/api/entries?paged=true&mark=starred")));
            Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync("/api/entries?mark=liked", ct)).StatusCode);

            // Recent = opened last, newest first
            Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsJsonAsync("/api/my/opened", new { kind = "clip", id = b }, ct)).StatusCode);
            await Task.Delay(20, ct);
            Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsJsonAsync("/api/my/opened", new { kind = "clip", id = a }, ct)).StatusCode);
            Assert.Equal([a, (long)b], Marks(await GetJsonAsync(member, "/api/my/marks"), "recent", "clips"));
            Assert.Equal(new[] { a, b }.OrderDescending().ToList(), Ids(await GetJsonAsync(member, "/api/entries?paged=true&mark=recent")).OrderDescending().ToList());

            // Unstarring keeps it in Recent; a deleted clip drops out of both
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/my/stars/clip/{a}", ct)).StatusCode);
            marks = await GetJsonAsync(member, "/api/my/marks");
            Assert.Empty(Marks(marks, "starred", "clips"));
            Assert.Contains((long)a, Marks(marks, "recent", "clips"));
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/entries/{a}", ct)).StatusCode);
            Assert.Equal([(long)b], Marks(await GetJsonAsync(member, "/api/my/marks"), "recent", "clips"));
        }
    }
}
