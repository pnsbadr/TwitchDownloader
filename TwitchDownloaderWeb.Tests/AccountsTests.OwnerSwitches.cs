using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Audio saving per channel and stored clips per team are the app Owner's decision, not the team's (badrfly, 2026-11-18).</summary>
    public sealed partial class AccountsTests
    {
        private void TurnOnStoredClips(int teamId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.Teams.Single(x => x.Id == teamId).StoreClips = true;
            db.SaveChanges();
        }

        [Fact]
        public async Task OnlyTheOwnerTurnsOnAudioSavingPerChannel()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("os1");
            var staff = await StaffClientAsync();
            var channel = (await GetJsonAsync(owner, "/api/channels"))[0];
            Assert.False(channel.GetProperty("saveAudio").GetBoolean());
            Assert.False(channel.GetProperty("watchMutedAudio").GetBoolean());

            // The team owner has SaveAudio, but the app hasn't allowed it on this channel; the team can't turn it on itself
            var refused = await owner.PostAsync("/api/vods/200/rescue", null, ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("audio-not-allowed", await CodeOf(refused));
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/team/settings", new { autoSaveAudio = true }, ct)).StatusCode);
            Assert.DoesNotContain((await GetJsonAsync(owner, "/api/team")).GetProperty("permissionCatalog").EnumerateArray(),
                x => x.GetProperty("key").GetString() == "ManageAudioSettings");

            // A moderator can't either, even one who manages channels
            var (_, moderator, moderatorId) = await OwnerWithModeratorAsync("osmod");
            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(staff, moderatorId, "ManageTeamChannels"));
            var channelId = channel.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PutAsJsonAsync($"/api/admin/teams/{teamId}/channels/{channelId}/audio",
                new { saveAudio = true }, ct)).StatusCode);

            // The Owner does, per channel
            var set = await staff.PutAsJsonAsync($"/api/admin/teams/{teamId}/channels/{channelId}/audio", new { saveAudio = true, autoSaveAudio = true }, ct);
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            var dto = await set.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.True(dto.GetProperty("saveAudio").GetBoolean());
            Assert.True(dto.GetProperty("autoSaveAudio").GetBoolean());
            channel = (await GetJsonAsync(owner, "/api/channels"))[0];
            Assert.True(channel.GetProperty("saveAudio").GetBoolean());
            Assert.True(channel.GetProperty("watchMutedAudio").GetBoolean());
            var settings = await GetJsonAsync(owner, "/api/team/settings");
            Assert.True(settings.GetProperty("channels")[0].GetProperty("autoSaveAudio").GetBoolean());
            Assert.False(settings.GetProperty("storeClips").GetBoolean());

            var team = (await GetJsonAsync(staff, "/api/admin/teams")).EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == teamId);
            Assert.True(team.GetProperty("channels")[0].GetProperty("autoSaveAudio").GetBoolean());
            Assert.Contains((await GetJsonAsync(staff, $"/api/admin/audit?teamId={teamId}")).EnumerateArray(),
                x => x.GetProperty("action").GetString() == AuditActions.ChannelAudioChanged);
        }

        [Fact]
        public async Task ShortClipsAreReferencesUntilTheOwnerStoresThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("os2");
            var staff = await StaffClientAsync();
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            Assert.Equal(180, (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("maxStoredClipSeconds").GetInt32());
            var entryId = AddEntry(teamId, ownerId, "Short one", vodId: 442);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var entry = db.Entries.Single(x => x.Id == entryId);
                entry.VodLengthSeconds = 3600;
                entry.EndTime = TimeSpan.FromSeconds(60);
                db.SaveChanges();
            }

            // A 30 s clip is a reference by default: nothing is downloaded
            var reference = await (await owner.PutAsJsonAsync($"/api/entries/{entryId}/trim", new { startTime = "10", endTime = "40" }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("ReferenceOnly", reference.GetProperty("status").GetString());

            // Only the app Owner turns stored clips on; then the next cut is stored
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PutAsJsonAsync($"/api/admin/teams/{teamId}/features", new { storeClips = true }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await staff.PutAsJsonAsync($"/api/admin/teams/{teamId}/features", new { storeClips = true }, ct)).StatusCode);
            Assert.True((await GetJsonAsync(staff, "/api/admin/teams")).EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == teamId)
                .GetProperty("storeClips").GetBoolean());
            var stored = await (await owner.PutAsJsonAsync($"/api/entries/{entryId}/trim", new { startTime = "10", endTime = "45" }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.False(stored.GetProperty("isReferenceOnly").GetBoolean());
        }
    }
}
