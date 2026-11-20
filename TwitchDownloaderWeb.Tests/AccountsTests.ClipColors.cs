using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Clips have a color like segments (badrfly 2026-11-20): null = violet.</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task ClipsKeepTheirColor()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("cc1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries", "EditOwnEntries");
            await AssignAsync(owner, memberId, clipper);
            var clipId = AddEntry(teamId, ownerId, "Owner's clip", vodId: 420);

            var entry = await GetJsonAsync(owner, $"/api/entries/{clipId}");
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("color").ValueKind);

            // Color only: the title stays
            Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/entries/{clipId}", new { color = "red" }, ct)).StatusCode);
            entry = await GetJsonAsync(owner, $"/api/entries/{clipId}");
            Assert.Equal("red", entry.GetProperty("color").GetString());
            Assert.Equal("Owner's clip", entry.GetProperty("title").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/entries/{clipId}", new { color = "plaid" }, ct)).StatusCode);

            var timeline = await GetJsonAsync(owner, "/api/vods/420/timeline");
            var clip = timeline.GetProperty("clips").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == clipId);
            Assert.Equal("red", clip.GetProperty("color").GetString());

            // Copies keep it; violet is the default
            var copied = await (await member.PostAsJsonAsync($"/api/entries/{clipId}/copy", new { }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("red", copied.GetProperty("color").GetString());
            var copyId = copied.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.OK, (await member.PatchAsJsonAsync($"/api/entries/{copyId}", new { title = "Mine", color = "violet" }, ct)).StatusCode);
            entry = await GetJsonAsync(member, $"/api/entries/{copyId}");
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("color").ValueKind);
            Assert.Equal("Mine", entry.GetProperty("title").GetString());
        }
    }
}
