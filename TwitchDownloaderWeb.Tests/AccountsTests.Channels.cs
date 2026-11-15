using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;
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

            // Strict: work already on a VOD of a channel the team doesn't have doesn't open it
            var teamId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("currentTeamId").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                db.Markers.Add(new VodMarker { TeamId = teamId, VodId = 201, Seconds = 1, Label = "", Color = "yellow", CreatedByUserId = 1, CreatedAtUtc = DateTime.UtcNow });
                db.SaveChanges();
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/vods/201/timeline", ct)).StatusCode);

            var clip = await owner.PostAsJsonAsync("/api/entries", new { vodId = "https://clips.twitch.tv/SomeClipSlug-abc123" }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, clip.StatusCode);
            Assert.Equal("clip-links-disabled", await CodeOf(clip));
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.DoesNotContain((await GetJsonAsync(owner, "/api/team")).GetProperty("permissionCatalog").EnumerateArray(),
                x => x.GetProperty("key").GetString() == "ManageChannels");
        }

        [Fact]
        public async Task StaffAssignChannelsAndRemoveThemForNewVodsOrRightAway()
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

            // Removing needs a choice
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.DeleteAsync($"/api/admin/teams/{teamId}/channels/{channelId}", ct)).StatusCode);

            // "Stop new VODs": a VOD the team already worked on stays open, the channel's other VODs close and it leaves the team's list
            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/admin/teams/{teamId}/channels/{channelId}?mode=future", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/vods/301/timeline", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/vods/302/timeline", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/channels/ellsya/vods", ct)).StatusCode);
            Assert.Equal(["arky"], (await GetJsonAsync(owner, "/api/channels")).EnumerateArray().Select(x => x.GetProperty("login").GetString()));
            var ended = (await GetJsonAsync(staff, $"/api/admin/teams/{teamId}/channels")).EnumerateArray().Single(x => x.GetProperty("login").GetString() == "ellsya");
            Assert.NotEqual(JsonValueKind.Null, ended.GetProperty("accessEndedAtUtc").ValueKind);

            // Assigning it again opens everything again
            Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/vods/302/timeline", ct)).StatusCode);

            // "Revoke now": even the VOD the team worked on closes
            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/admin/teams/{teamId}/channels/{channelId}?mode=revoke", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/vods/301/timeline", ct)).StatusCode);
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
