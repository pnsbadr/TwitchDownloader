using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Team channels: the app Owner assigns them, members only see and work on those channels' VODs (badrfly, 2026-11-15).</summary>
    public sealed partial class AccountsTests
    {
        private static async Task<string> CodeOf(HttpResponseMessage response) =>
            (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).TryGetProperty("code", out var code)
                ? code.GetString() ?? ""
                : "";

        [Fact]
        public async Task RegistrationLinkAssignsChannelsAndOnlyTheirVodsOpen()
        {
            var ct = TestContext.Current.CancellationToken;
            var staff = await StaffClientAsync();
            _vods.Vods[201] = new VodDetails(201, "Ellsya stream", "Ellsya", 3600, DateTime.UtcNow.AddDays(-1), null, ChannelLogin: "ellsya");

            // Unknown channels are refused when the link is made
            var bad = await staff.PostAsJsonAsync("/api/admin/links", new { kind = "registration", channels = new[] { "arky", "nobody" } }, ct);
            Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);

            var token = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration", channels = new[] { "https://www.twitch.tv/Arky", "qsmp" } });
            var links = await GetJsonAsync(staff, "/api/admin/links");
            Assert.Equal(["Arky", "Qsmp"], links[0].GetProperty("channels").EnumerateArray().Select(x => x.GetString()));

            // The person opening it sees which channels come with it
            var info = await (await NewClient().PostAsJsonAsync("/api/links/inspect", new { token }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(2, info.GetProperty("channels").GetArrayLength());

            var owner = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(owner, token, "ch1owner", newAccount: true, teamName: "Arky team")).StatusCode);
            var channels = await GetJsonAsync(owner, "/api/channels");
            Assert.Equal(["arky", "qsmp"], channels.EnumerateArray().Select(x => x.GetProperty("login").GetString()));

            // Arky's VODs open; Ellsya's don't, nor can a member add channels or Twitch clip links any more
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/vods/200/timeline", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/vods/200/markers", new { seconds = 5 }, ct)).StatusCode);
            foreach (var response in new[]
                     {
                         await owner.GetAsync("/api/vods/201/timeline", ct),
                         await owner.PostAsJsonAsync("/api/vods/201/markers", new { seconds = 5 }, ct),
                         await owner.GetAsync("/api/vods/201/play.m3u8", ct),
                         await owner.GetAsync("/api/vods/201/rescue", ct),
                         await owner.PostAsJsonAsync("/api/vods/201/segment-downloads", new { markerIds = new[] { 1 } }, ct),
                         await owner.GetAsync("/api/vods/info?vod=201", ct),
                         await owner.PostAsJsonAsync("/api/entries", new { vodId = "201" }, ct),
                         await owner.GetAsync("/api/channels/ellsya/vods", ct),
                     })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("channel-not-assigned", await CodeOf(response));
            }

            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/vods/{FakeVodDetails.MissingFrom}/timeline", ct)).StatusCode);

            var clip = await owner.PostAsJsonAsync("/api/entries", new { vodId = "https://clips.twitch.tv/SomeClipSlug-abc123" }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, clip.StatusCode);
            Assert.Equal("clip-links-disabled", await CodeOf(clip));
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.DoesNotContain((await GetJsonAsync(owner, "/api/team")).GetProperty("permissionCatalog").EnumerateArray(),
                x => x.GetProperty("key").GetString() == "ManageChannels");
        }

        [Fact]
        public async Task StaffAssignAndRemoveChannelsAndWorkAlreadyDoneStaysOpen()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ch2");
            var staff = await StaffClientAsync();
            _vods.Vods[301] = new VodDetails(301, "Ellsya stream", "Ellsya", 3600, DateTime.UtcNow.AddDays(-1), null, ChannelLogin: "ellsya");
            _vods.Vods[302] = new VodDetails(302, "Another Ellsya stream", "Ellsya", 3600, DateTime.UtcNow, null, ChannelLogin: "ellsya");
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/vods/301/timeline", ct)).StatusCode);

            var added = await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "Ellsya" }, ct);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            var channelId = (await added.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "nobody" }, ct)).StatusCode);

            var team = (await GetJsonAsync(staff, "/api/admin/teams")).EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == teamId);
            Assert.Equal(["arky", "ellsya"], team.GetProperty("channels").EnumerateArray().Select(x => x.GetProperty("login").GetString()));

            // Takes effect at once, also for answers the app had cached
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/vods/301/timeline", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/vods/301/markers", new { seconds = 5 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/vods/302/timeline", ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/admin/teams/{teamId}/channels/{channelId}", ct)).StatusCode);
            // A VOD the team already worked on stays open; the channel's other VODs close
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/vods/301/timeline", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/vods/302/timeline", ct)).StatusCode);

            var audit = await GetJsonAsync(staff, $"/api/admin/audit?teamId={teamId}");
            Assert.Contains(audit.EnumerateArray(), x => x.GetProperty("action").GetString() == "channel.added");
            Assert.Contains(audit.EnumerateArray(), x => x.GetProperty("action").GetString() == "channel.removed");
        }

        [Fact]
        public async Task OnlyStaffWithThePermissionAssignChannels()
        {
            var ct = TestContext.Current.CancellationToken;
            var (staffOwner, moderator, moderatorId) = await OwnerWithModeratorAsync("chmod");
            var (_, _, teamId, _) = await TeamWithMemberAsync("ch3");
            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(staffOwner, moderatorId, "ManageTeamLinks", "ViewTeamsAndUsers"));

            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await moderator.PostAsJsonAsync("/api/admin/links", new { kind = "registration", channels = new[] { "arky" } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await moderator.PostAsJsonAsync("/api/admin/links", new { kind = "registration" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await moderator.GetAsync($"/api/admin/teams/{teamId}/channels", ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(staffOwner, moderatorId, "ManageTeamChannels"));
            Assert.Equal(HttpStatusCode.Created, (await moderator.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
        }
    }
}
