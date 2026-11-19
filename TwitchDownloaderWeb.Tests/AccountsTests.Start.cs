using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;

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

        [Fact]
        public async Task OwnerSetsAppNameAndStartScreenLinks()
        {
            var ct = TestContext.Current.CancellationToken;
            var (staffOwner, moderator, _) = await OwnerWithModeratorAsync("as1");
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PutAsJsonAsync("/api/admin/app", new { name = "Nope" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.GetAsync("/api/admin/app", ct)).StatusCode);

            var bad = await staffOwner.PutAsJsonAsync("/api/admin/app", new { name = "Clips", x = "javascript:alert(1)", discord = "" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            // No scheme gets https://; blank hides the icon
            var saved = await staffOwner.PutAsJsonAsync("/api/admin/app", new { name = " Arky Clips ", x = "x.com/arky", discord = "" }, ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var app = await GetJsonAsync(NewClient(), "/api/auth/app");
            Assert.Equal("Arky Clips", app.GetProperty("name").GetString());
            Assert.Equal("https://x.com/arky", app.GetProperty("links").GetProperty("x").GetString());
            Assert.Equal(JsonValueKind.Null, app.GetProperty("links").GetProperty("discord").ValueKind);

            // Changed again; a blank name goes back to the default
            Assert.Equal(HttpStatusCode.OK, (await staffOwner.PutAsJsonAsync("/api/admin/app",
                new { name = "", x = "", discord = "https://discord.gg/abc" }, ct)).StatusCode);
            app = await GetJsonAsync(staffOwner, "/api/admin/app");
            Assert.Equal("Clipco", app.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, app.GetProperty("links").GetProperty("x").ValueKind);
            Assert.Equal("https://discord.gg/abc", app.GetProperty("links").GetProperty("discord").GetString());

        }

        [Fact]
        public async Task LibraryFiltersClipsAndVideosByTeamChannel()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("cf1");
            var staff = await StaffClientAsync();
            _vods.Vods[631] = new VodDetails(631, "Ellsya stream", "Ellsya", 3600, DateTime.UtcNow.AddDays(-1), null, ChannelLogin: "ellsya");
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);

            // The library starts on the channel the team is known by; each channel has its picture and live state
            var channels = (await GetJsonAsync(owner, "/api/channels")).EnumerateArray().ToList();
            Assert.Equal(["arky"], channels.Where(x => x.GetProperty("isPrimary").GetBoolean()).Select(x => x.GetProperty("login").GetString()));
            var cards = (await GetJsonAsync(owner, "/api/channels/cards")).EnumerateArray().ToList();
            Assert.Equal(["arky", "ellsya"], cards.Select(x => x.GetProperty("login").GetString()).Order());
            Assert.Equal("https://example.invalid/ellsya.png", cards.Single(x => x.GetProperty("login").GetString() == "ellsya").GetProperty("profileImageUrl").GetString());

            // Clips and videos keep the name Twitch gave the VOD's owner: display name or login, any case
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                foreach (var (title, channel) in new[] { ("Arky clip", "Arky"), ("Arky login clip", "arky"), ("Ellsya clip", "ELLSYA"), ("Other clip", "Someone") })
                    db.Entries.Add(new LibraryEntry { TeamId = teamId, Title = title, VodId = 1, ChannelName = channel, Status = EntryStatus.ReferenceOnly, AddedAtUtc = DateTime.UtcNow });
                db.SaveChanges();
            }
            async Task<string[]> Titles(string url) =>
                (await GetJsonAsync(owner, url)).EnumerateArray().Select(x => x.GetProperty("title").GetString() ?? "").Order().ToArray();
            Assert.Equal(["Arky clip", "Arky login clip"], await Titles("/api/entries?channels=arky"));
            Assert.Equal(["Arky clip", "Arky login clip", "Ellsya clip"], await Titles("/api/entries?channels=arky,ellsya"));
            Assert.Equal(["Arky clip", "Arky login clip", "Ellsya clip", "Other clip"], await Titles("/api/entries"));
            Assert.Empty(await Titles("/api/entries?channels=nobody"));

            foreach (var (vod, title) in new[] { (630, "Arky video"), (631, "Ellsya video") })
            {
                var segment = await (await owner.PostAsJsonAsync($"/api/vods/{vod}/markers", new { seconds = 5, endSeconds = 20 }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
                Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/vods/{vod}/videos",
                    new { title, markerIds = new[] { segment.GetProperty("id").GetInt32() } }, ct)).StatusCode);
            }
            Assert.Equal(["Ellsya video"], await Titles("/api/videos?channels=ellsya"));
            Assert.Equal(["Arky video", "Ellsya video"], await Titles("/api/videos?channels=ellsya,arky"));
            Assert.Equal(["Arky video", "Ellsya video"], await Titles("/api/videos"));
        }
    }
}
