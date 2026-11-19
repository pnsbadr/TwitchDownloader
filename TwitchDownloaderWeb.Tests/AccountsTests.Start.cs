using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>The start screen: team cards with their channel and last activity, links that open the app (badrfly 2026-11-19).</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task StartScreenListsTeamsWithChannelAndLastActivity()
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().GetAsync("/api/auth/teams", ct)).StatusCode);
            var app = await GetJsonAsync(NewClient(), "/api/auth/app");
            Assert.Equal("Clipco", app.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, app.GetProperty("links").GetProperty("x").ValueKind);

            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("st1");
            var before = await GetJsonAsync(member, "/api/auth/teams");
            var card = before.EnumerateArray().Single();
            Assert.Equal(teamId, card.GetProperty("id").GetInt32());
            Assert.True(card.GetProperty("isCurrent").GetBoolean());
            Assert.False(card.GetProperty("hasAccess").GetBoolean());
            Assert.False(card.GetProperty("canOpenTeamPage").GetBoolean());
            Assert.Equal("Arky", card.GetProperty("channel").GetProperty("displayName").GetString());
            Assert.Equal(JsonValueKind.Null, card.GetProperty("myLastActiveUtc").ValueKind);

            // A change to the team's work counts for the member and the team; the owner's own stays empty
            var role = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId, role));
            Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/vods/200/markers", new { seconds = 5 }, ct)).StatusCode);

            card = (await GetJsonAsync(member, "/api/auth/teams")).EnumerateArray().Single();
            Assert.True(card.GetProperty("hasAccess").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, card.GetProperty("myLastActiveUtc").ValueKind);
            var ownerCard = (await GetJsonAsync(owner, "/api/auth/teams")).EnumerateArray().Single();
            Assert.True(ownerCard.GetProperty("canOpenTeamPage").GetBoolean());
            Assert.Equal(card.GetProperty("teamLastActiveUtc").GetDateTime(), ownerCard.GetProperty("teamLastActiveUtc").GetDateTime());
            Assert.Equal(JsonValueKind.Null, ownerCard.GetProperty("myLastActiveUtc").ValueKind);

            // The channel's picture and last stream, only for the team's members
            var channel = await GetJsonAsync(member, $"/api/auth/teams/{teamId}/channel");
            Assert.Equal("https://example.invalid/arky.png", channel.GetProperty("profileImageUrl").GetString());
            Assert.False(channel.GetProperty("live").GetBoolean());
            var (otherOwner, _, _, _) = await TeamWithMemberAsync("st2");
            Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.GetAsync($"/api/auth/teams/{teamId}/channel", ct)).StatusCode);

            // Team users' links open the app's start screen; staff links still open join.html
            var invite = await owner.PostAsJsonAsync("/api/team/invites", new { }, ct);
            Assert.Contains("/#join=", (await invite.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString());
            var (staffOwner, _, _) = await OwnerWithModeratorAsync("st3");
            var staffInvite = await staffOwner.PostAsJsonAsync("/api/admin/staff/invites", new { }, ct);
            Assert.Contains("/join.html#", (await staffInvite.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString());
        }
    }
}
