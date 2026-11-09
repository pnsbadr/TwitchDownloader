using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task MarkersAreSharedInTheTeamAndOnlyTheirs()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("m1");
            AddEntry(teamId, memberId, "Clip on VOD 1");
            var adder = await CreateRoleAsync(owner, "Adder", "ViewLibrary", "AddEntries");
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId, adder));

            var added = await member.PostAsJsonAsync("/api/vods/1/markers", new { seconds = 3725.456, label = "Funny bit", color = "pink" }, ct);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            var mine = (await added.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var ownerMarker = (await (await owner.PostAsJsonAsync("/api/vods/1/markers", new { seconds = 10, label = "" }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/1/markers", new { seconds = -1 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/1/markers", new { seconds = 5, color = "red; x" }, ct)).StatusCode);

            // The whole team sees both, with who made them and the team's clips on the VOD
            var timeline = await GetJsonAsync(member, "/api/vods/1/timeline");
            var markers = timeline.GetProperty("markers").EnumerateArray().ToList();
            Assert.Equal([10, 3725.46], markers.Select(x => x.GetProperty("seconds").GetDouble()));
            Assert.Equal("m1member", markers[1].GetProperty("createdBy").GetString());
            Assert.Equal([false, true], markers.Select(x => x.GetProperty("canDelete").GetBoolean()));
            Assert.Equal(["Clip on VOD 1"], timeline.GetProperty("clips").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

            // Another team sees none of it (the Main team has its own entry on VOD 1)
            var staff = await StaffClientAsync();
            var registration = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });
            var other = NewClient();
            await AcceptAsync(other, registration, "m1other", newAccount: true, teamName: "Other team");
            var otherTimeline = await GetJsonAsync(other, "/api/vods/1/timeline");
            Assert.Equal(0, otherTimeline.GetProperty("markers").GetArrayLength());
            Assert.Equal(0, otherTimeline.GetProperty("clips").GetArrayLength());
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/markers/{mine}", ct)).StatusCode);

            // Own markers yes, others' no
            Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/markers/{ownerMarker}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/markers/{mine}", ct)).StatusCode);

            // Without AddEntries no new markers
            var viewer = await CreateRoleAsync(owner, "Viewer", "ViewLibrary");
            await AssignAsync(owner, memberId, viewer);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/vods/1/markers", new { seconds = 1 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{ownerMarker}", ct)).StatusCode);
        }
    }
}
