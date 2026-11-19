using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

            // A new team gets exactly one real channel (badrfly 2026-11-19): unknown, none or two are refused when the link is made
            Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync("/api/admin/links", new { kind = "registration", channel = "nobody" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/admin/links", new { kind = "registration" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/admin/links", new { kind = "registration", channel = " " }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await staff.PostAsJsonAsync("/api/admin/links", new { kind = "registration", channels = new[] { "arky", "qsmp" } }, ct)).StatusCode);
            // Owner links never change a team's channels
            Assert.Equal(HttpStatusCode.BadRequest,
                (await staff.PostAsJsonAsync("/api/admin/links", new { kind = "ownership", teamId = 1, channel = "qsmp" }, ct)).StatusCode);

            var token = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration", channel = "https://www.twitch.tv/Arky" });
            var links = await GetJsonAsync(staff, "/api/admin/links");
            Assert.Equal(["Arky"], links[0].GetProperty("channels").EnumerateArray().Select(x => x.GetString()));

            // The person opening it sees the channel and the suggested name, after the channel (staff don't pick it)
            var info = await (await NewClient().PostAsJsonAsync("/api/links/inspect", new { token }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(["Arky"], info.GetProperty("channels").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("Arky's Team", info.GetProperty("teamName").GetString());
            Assert.Equal(14, info.GetProperty("teamRenameDays").GetInt32());

            var owner = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(owner, token, "ch1owner", newAccount: true)).StatusCode);
            var me = await GetJsonAsync(owner, "/api/auth/me");
            Assert.Equal("Arky's Team", me.GetProperty("teams")[0].GetProperty("name").GetString());
            Assert.Equal(["arky"], (await GetJsonAsync(owner, "/api/channels")).EnumerateArray().Select(x => x.GetProperty("login").GetString()));

            // More channels only from the app Owner, on the Teams tab
            var teamId = me.GetProperty("currentTeamId").GetInt32();
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "qsmp" }, ct)).StatusCode);
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
        public async Task RevokeHidesTheTeamsWorkUntilTheChannelIsAssignedAgain()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ch4");
            var staff = await StaffClientAsync();
            _vods.Vods[401] = new VodDetails(401, "Ellsya stream", "Ellsya", 3600, DateTime.UtcNow.AddDays(-1), null, ChannelLogin: "ellsya");
            var added = await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct);
            var channelId = (await added.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            // Work on an Ellsya VOD (a segment in a video, a clip), on a deleted Ellsya VOD, an old Ellsya Twitch clip, and on an Arky VOD
            var segment = await (await owner.PostAsJsonAsync("/api/vods/401/markers", new { seconds = 5, endSeconds = 20, label = "bit" }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            var segmentId = segment.GetProperty("id").GetInt32();
            var video = await owner.PostAsJsonAsync("/api/vods/401/videos", new { title = "Ellsya video", markerIds = new[] { segmentId } }, ct);
            Assert.Equal(HttpStatusCode.Created, video.StatusCode);
            var videoId = (await video.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var ellsyaClip = AddEntry(teamId, null, "Ellsya clip", 401);
            var goneVodClip = AddEntry(teamId, null, "Clip of a deleted VOD", FakeVodDetails.MissingFrom + 1);
            var twitchClip = AddEntry(teamId, null, "Old Twitch clip", 0);
            var arkyClip = AddEntry(teamId, null, "Arky clip", 400);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                foreach (var entry in db.Entries.Where(x => x.Id == goneVodClip || x.Id == twitchClip))
                {
                    entry.ChannelName = "Ellsya";
                    if (entry.Id == twitchClip)
                        entry.Kind = EntryKind.Clip;
                }
                db.SaveChanges();
            }

            async Task<List<string>> Titles() =>
                (await GetJsonAsync(owner, "/api/entries")).EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToList();
            Assert.Equal(4, (await Titles()).Count(x => x != "Old clip"));

            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/admin/teams/{teamId}/channels/{channelId}?mode=revoke", ct)).StatusCode);

            // Everything from Ellsya is gone for the team; Arky's work stays
            Assert.Equal(["Arky clip"], await Titles());
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/entries/{ellsyaClip}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/entries/{ellsyaClip}/file", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/entries/{ellsyaClip}/generate", null, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/videos/{videoId}", ct)).StatusCode);
            Assert.Empty((await GetJsonAsync(owner, "/api/videos")).EnumerateArray());
            Assert.Empty((await GetJsonAsync(owner, "/api/markers?vodIds=401")).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/markers/{segmentId}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/vods/401/timeline", ct)).StatusCode);

            var team = (await GetJsonAsync(staff, "/api/admin/teams")).EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == teamId);
            var revoked = Assert.Single(team.GetProperty("revokedChannels").EnumerateArray());
            Assert.Equal("ellsya", revoked.GetProperty("login").GetString());
            Assert.Equal(2, revoked.GetProperty("hiddenVods").GetInt32()); // 401 and the deleted VOD

            // Nothing was deleted: assigning the channel again brings it all back
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.Equal(4, (await Titles()).Count(x => x != "Old clip"));
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/videos/{videoId}", ct)).StatusCode);
            Assert.Single((await GetJsonAsync(owner, "/api/markers?vodIds=401")).EnumerateArray());
            team = (await GetJsonAsync(staff, "/api/admin/teams")).EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == teamId);
            Assert.Empty(team.GetProperty("revokedChannels").EnumerateArray());
        }

        [Fact]
        public async Task TeamsPickTheirOwnNameOnceEvery14Days()
        {
            var ct = TestContext.Current.CancellationToken;
            var (staff, moderator, moderatorId) = await OwnerWithModeratorAsync("nm1");

            // Changing the suggested name when opening the link is the team's rename (badrfly 2026-11-19)
            var token = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration", channel = "arky" });
            var owner = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(owner, token, "nm1owner", newAccount: true, teamName: "  Arky Clips ")).StatusCode);
            var team = await GetJsonAsync(owner, "/api/team");
            Assert.Equal("Arky Clips", team.GetProperty("name").GetString());
            Assert.True(team.GetProperty("canRename").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, team.GetProperty("nextRenameAtUtc").ValueKind);
            var teamId = team.GetProperty("id").GetInt32();

            var tooSoon = await owner.PutAsJsonAsync("/api/team/name", new { name = "Arky Shorts" }, ct);
            Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
            Assert.Equal("rename-too-soon", await CodeOf(tooSoon));
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/team/name", new { name = " " }, ct)).StatusCode);

            // The app Owner renames any time, without touching the team's wait; staff see "Channel (team name)"
            var renamed = await staff.PutAsJsonAsync($"/api/admin/teams/{teamId}/name", new { name = "Arky Editors" }, ct);
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            var dto = await renamed.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("Arky Editors", dto.GetProperty("name").GetString());
            Assert.Equal("Arky (Arky Editors)", dto.GetProperty("label").GetString());
            Assert.Equal("Arky (Arky Editors)", (await GetJsonAsync(staff, "/api/admin/teams")).EnumerateArray()
                .Single(x => x.GetProperty("id").GetInt32() == teamId).GetProperty("label").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/team/name", new { name = "Arky Shorts" }, ct)).StatusCode);

            // 14 days later the team may pick again
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var row = await db.Teams.FirstAsync(x => x.Id == teamId, ct);
                row.NameChangedAtUtc = DateTime.UtcNow.AddDays(-14).AddMinutes(-1);
                await db.SaveChangesAsync(ct);
            }
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/team/name", new { name = "Arky Shorts" }, ct)).StatusCode);
            Assert.Equal("Arky Shorts", (await GetJsonAsync(owner, "/api/team")).GetProperty("name").GetString());

            // Keeping the suggested name doesn't start the wait; only the team owner renames, never a moderator
            var token2 = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration", channel = "arky" });
            var owner2 = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(owner2, token2, "nm2owner", newAccount: true, teamName: "Arky's Team")).StatusCode);
            Assert.Equal(JsonValueKind.Null, (await GetJsonAsync(owner2, "/api/team")).GetProperty("nextRenameAtUtc").ValueKind);
            Assert.Equal(HttpStatusCode.OK, (await owner2.PutAsJsonAsync("/api/team/name", new { name = "Second Arky team" }, ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(staff, moderatorId, "ManageTeamChannels", "ViewTeamsAndUsers"));
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PutAsJsonAsync($"/api/admin/teams/{teamId}/name", new { name = "X" }, ct)).StatusCode);
        }

        [Fact]
        public async Task OnlyStaffWithThePermissionAssignChannels()
        {
            var ct = TestContext.Current.CancellationToken;
            var (staffOwner, moderator, moderatorId) = await OwnerWithModeratorAsync("chmod");
            var (_, _, teamId, _) = await TeamWithMemberAsync("ch3");
            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(staffOwner, moderatorId, "ManageTeamLinks", "ViewTeamsAndUsers"));

            // Making team links includes a new team's one channel; giving a team more is the Owner's alone (badrfly 2026-11-19)
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await moderator.PostAsJsonAsync("/api/admin/links", new { kind = "registration", channel = "arky" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await moderator.GetAsync($"/api/admin/teams/{teamId}/channels", ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(staffOwner, moderatorId, "ManageTeamChannels"));
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await staffOwner.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);

            // Taking a channel away stays with the permission
            var ellsya = (await GetJsonAsync(staffOwner, $"/api/admin/teams/{teamId}/channels")).EnumerateArray().Single(x => x.GetProperty("login").GetString() == "ellsya");
            var removed = await moderator.DeleteAsync($"/api/admin/teams/{teamId}/channels/{ellsya.GetProperty("id").GetInt32()}?mode=future", ct);
            Assert.True(removed.IsSuccessStatusCode, removed.StatusCode.ToString());
        }
    }
}
