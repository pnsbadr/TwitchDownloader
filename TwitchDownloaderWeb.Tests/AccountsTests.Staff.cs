using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>App moderators, view as support and the staff tools for users and teams.</summary>
    public sealed partial class AccountsTests
    {
        private const string ModeratorPassword = "moderator-password-1";

        /// <summary>The Owner invites a moderator, who signs up through the link. Returns the moderator's staff client and ID.</summary>
        private async Task<(HttpClient Owner, HttpClient Moderator, int ModeratorId)> OwnerWithModeratorAsync(string username)
        {
            var ct = TestContext.Current.CancellationToken;
            var owner = await StaffClientAsync();
            var invite = await MakeLinkAsync(owner, "/api/admin/staff/invites", new { note = "for " + username });
            var moderator = NewClient();
            var info = await (await moderator.PostAsJsonAsync("/api/links/inspect", new { token = invite }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("StaffInvite", info.GetProperty("kind").GetString());
            var accepted = await AcceptAsync(moderator, invite, username, newAccount: true, password: ModeratorPassword);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var me = await GetJsonAsync(moderator, "/api/admin/me");
            return (owner, moderator, me.GetProperty("id").GetInt32());
        }

        private static async Task<HttpStatusCode> SetStaffPermissionsAsync(HttpClient owner, int staffId, params string[] permissions) =>
            (await owner.PutAsJsonAsync($"/api/admin/staff/{staffId}/permissions", new { permissions }, TestContext.Current.CancellationToken)).StatusCode;

        [Fact]
        public async Task ModeratorsOnlyDoWhatTheOwnerAllows()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, moderator, moderatorId) = await OwnerWithModeratorAsync("mod1");

            // A moderator starts with nothing, like a new team member
            var me = await GetJsonAsync(moderator, "/api/admin/me");
            Assert.False(me.GetProperty("isOwner").GetBoolean());
            Assert.Equal(0, me.GetProperty("permissions").GetArrayLength());
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.GetAsync("/api/admin/teams", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.GetAsync("/api/admin/staff", ct)).StatusCode);

            // A staff account is not a team login
            Assert.Equal(HttpStatusCode.Unauthorized, (await moderator.GetAsync("/api/entries", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await NewClient().PostAsJsonAsync("/api/auth/login", new { username = "mod1", password = ModeratorPassword }, ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(owner, moderatorId, "ViewTeamsAndUsers"));
            Assert.Equal(HttpStatusCode.OK, (await moderator.GetAsync("/api/admin/teams", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await moderator.GetAsync("/api/admin/users", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsJsonAsync("/api/admin/links", new { kind = "registration" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsJsonAsync("/api/admin/support", new { teamId = 1 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.GetAsync("/api/admin/audit", ct)).StatusCode);

            // Only the Owner manages staff, and nobody changes the Owner
            Assert.Equal(HttpStatusCode.Forbidden, await SetStaffPermissionsAsync(moderator, moderatorId, "ViewTeamsAndUsers", "ManageTeamLinks"));
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsJsonAsync("/api/admin/staff/invites", new { }, ct)).StatusCode);
            var ownerId = (await GetJsonAsync(owner, "/api/admin/me")).GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Forbidden, await SetStaffPermissionsAsync(owner, ownerId, "ViewTeamsAndUsers"));
            Assert.Equal(HttpStatusCode.BadRequest, await SetStaffPermissionsAsync(owner, moderatorId, "MakeCoffee"));

            var staffList = await GetJsonAsync(owner, "/api/admin/staff");
            Assert.Equal(["ViewTeamsAndUsers"], staffList.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == moderatorId)
                .GetProperty("permissions").EnumerateArray().Select(x => x.GetString()));

            // Disabling signs the moderator out at once
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/admin/staff/{moderatorId}/disabled", new { disabled = true }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await moderator.GetAsync("/api/admin/me", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await NewClient().PostAsJsonAsync("/api/admin/login", new { username = "mod1", password = ModeratorPassword }, ct)).StatusCode);
        }

        [Fact]
        public async Task StaffPasswordResetsAndChanges()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, moderator, moderatorId) = await OwnerWithModeratorAsync("mod2");

            var reset = await MakeLinkAsync(owner, $"/api/admin/staff/{moderatorId}/password-reset", new { });
            var info = await (await NewClient().PostAsJsonAsync("/api/links/inspect", new { token = reset }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("mod2", info.GetProperty("forUsername").GetString());
            Assert.Equal(HttpStatusCode.OK, (await NewClient().PostAsJsonAsync("/api/links/accept", new { token = reset, password = "fresh-staff-password" }, ct)).StatusCode);

            Assert.Equal(HttpStatusCode.Unauthorized, (await moderator.GetAsync("/api/admin/me", ct)).StatusCode);
            var again = NewClient();
            Assert.Equal(HttpStatusCode.NoContent,
                (await again.PostAsJsonAsync("/api/admin/login", new { username = "mod2", password = "fresh-staff-password" }, ct)).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest,
                (await again.PostAsJsonAsync("/api/admin/password", new { currentPassword = "wrong-wrong-wrong", newPassword = "newer-staff-password" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,
                (await again.PostAsJsonAsync("/api/admin/password", new { currentPassword = "fresh-staff-password", newPassword = "newer-staff-password" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/admin/me", ct)).StatusCode);

            // The Owner's own account can't be reset through a link
            var ownerId = (await GetJsonAsync(owner, "/api/admin/me")).GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/admin/staff/{ownerId}/password-reset", new { }, ct)).StatusCode);
        }

        [Fact]
        public async Task SupportViewIsReadOnlyAndAudited()
        {
            var ct = TestContext.Current.CancellationToken;
            var (teamOwner, _, teamId, memberId) = await TeamWithMemberAsync("s1");
            var entryId = AddEntry(teamId, memberId, "Team clip");
            var (owner, moderator, moderatorId) = await OwnerWithModeratorAsync("mod3");

            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsJsonAsync("/api/admin/support", new { teamId }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(owner, moderatorId, "ViewAsSupport"));
            Assert.Equal(HttpStatusCode.NoContent, (await moderator.PostAsJsonAsync("/api/admin/support", new { teamId }, ct)).StatusCode);

            // Sees that team's library (and only it) and its Team page, as the staff account
            var me = await GetJsonAsync(moderator, "/api/auth/me");
            Assert.Equal("s1 team", me.GetProperty("support").GetProperty("teamName").GetString());
            Assert.Equal(["ViewLibrary", "ViewActivity"], me.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()));
            var entries = await GetJsonAsync(moderator, "/api/entries");
            Assert.Equal([entryId], entries.EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
            Assert.Equal(HttpStatusCode.NotFound, (await moderator.GetAsync($"/api/entries/{_mainTeamEntryId}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await moderator.GetAsync("/api/team", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await moderator.GetAsync("/api/team/activity", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.GetAsync("/api/team/invites", ct)).StatusCode);

            // Nothing can be changed
            var add = await moderator.PostAsJsonAsync("/api/entries", new { vodId = "123" }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
            Assert.Equal("read-only", (await add.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PatchAsJsonAsync($"/api/entries/{entryId}", new { title = "x" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.DeleteAsync($"/api/entries/{entryId}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsJsonAsync("/api/team/invites", new { }, ct)).StatusCode);

            // Audited for staff, but not in the team's own log
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var started = await db.AuditEvents.SingleAsync(x => x.Action == AuditActions.SupportViewStarted, ct);
                Assert.Equal(moderatorId, started.StaffId);
                Assert.Equal(teamId, started.TeamId);
                Assert.Null(started.UserId);
            }
            var teamLog = await GetJsonAsync(teamOwner, "/api/team/audit");
            Assert.DoesNotContain(teamLog.EnumerateArray(), x => x.GetProperty("action").GetString()!.StartsWith("support."));
            var appLog = await GetJsonAsync(owner, $"/api/admin/audit?staffId={moderatorId}");
            Assert.Contains(appLog.EnumerateArray(), x => x.GetProperty("action").GetString() == AuditActions.SupportViewStarted);

            // Taking the permission away ends the visit on the next request
            Assert.Equal(HttpStatusCode.NoContent, await SetStaffPermissionsAsync(owner, moderatorId));
            var ended = await moderator.GetAsync("/api/entries", ct);
            Assert.Equal(HttpStatusCode.Forbidden, ended.StatusCode);
            Assert.Equal("support-ended", (await ended.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.NoContent, (await moderator.DeleteAsync("/api/admin/support", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await moderator.GetAsync("/api/entries", ct)).StatusCode);
        }

        [Fact]
        public async Task StaffSeeUsersAcrossTeamsAndCanResetOrDisableThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var (_, member, teamA, memberId) = await TeamWithMemberAsync("u1");
            var (otherOwner, _, teamB, _) = await TeamWithMemberAsync("u2");
            var invite = await MakeLinkAsync(otherOwner, "/api/team/invites", new { });
            Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync("/api/links/accept", new { token = invite, newAccount = false }, ct)).StatusCode);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                db.Entries.Add(new LibraryEntry { TeamId = teamA, Title = "A", VodId = 1, Status = EntryStatus.Ready, AddedAtUtc = DateTime.UtcNow,
                    AddedByUserId = memberId.ToString(), FileSizeBytes = 1000 });
                db.Entries.Add(new LibraryEntry { TeamId = teamB, Title = "B", VodId = 1, Status = EntryStatus.Ready, AddedAtUtc = DateTime.UtcNow,
                    AddedByUserId = memberId.ToString(), FileSizeBytes = 500 });
                db.Audit(AuditActions.EntryAdded, null, "A", userId: memberId.ToString(), teamId: teamA);
                await db.SaveChangesAsync(ct);
            }

            var staff = await StaffClientAsync();
            var users = await GetJsonAsync(staff, "/api/admin/users?search=u1mem");
            var user = users.EnumerateArray().Single();
            Assert.Equal(memberId, user.GetProperty("id").GetInt32());
            Assert.Equal(["u1 team", "u2 team"], user.GetProperty("teams").EnumerateArray().Select(x => x.GetProperty("teamName").GetString()));
            Assert.Equal(1500, user.GetProperty("diskBytes").GetInt64());
            Assert.Equal(2, user.GetProperty("entriesAdded").GetInt32());
            Assert.Equal(1, user.GetProperty("recentActions").GetInt32());

            var detail = await GetJsonAsync(staff, $"/api/admin/users/{memberId}?days=30");
            var inA = detail.GetProperty("activity").EnumerateArray().Single(x => x.GetProperty("teamId").GetInt32() == teamA);
            Assert.Equal(1, inA.GetProperty("counts").GetProperty("added").GetInt32());
            Assert.Equal(1000, inA.GetProperty("diskBytes").GetInt64());

            var team = await GetJsonAsync(staff, $"/api/admin/teams/{teamA}");
            Assert.Equal(1000, team.GetProperty("team").GetProperty("diskBytes").GetInt64());
            var row = team.GetProperty("members").EnumerateArray().Single(x => x.GetProperty("userId").GetInt32() == memberId);
            Assert.Equal(1, row.GetProperty("otherTeams").GetInt32());

            // Staff can reset someone who is in several teams (a team can't)
            var reset = await MakeLinkAsync(staff, $"/api/admin/users/{memberId}/password-reset", new { });
            Assert.Equal(HttpStatusCode.OK, (await NewClient().PostAsJsonAsync("/api/links/accept", new { token = reset, password = "staff-set-password" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/auth/me", ct)).StatusCode);

            // Disabling the account signs it out and blocks logging in; enabling lets them back
            var device = NewClient();
            Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync("/api/auth/login", new { username = "u1member", password = "staff-set-password" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await staff.PutAsJsonAsync($"/api/admin/users/{memberId}/disabled", new { disabled = true }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/auth/me", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await NewClient().PostAsJsonAsync("/api/auth/login", new { username = "u1member", password = "staff-set-password" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await staff.PutAsJsonAsync($"/api/admin/users/{memberId}/disabled", new { disabled = false }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync("/api/auth/login", new { username = "u1member", password = "staff-set-password" }, ct)).StatusCode);

            // A disabled team drops out of the member's teams
            Assert.Equal(HttpStatusCode.NoContent, (await staff.PutAsJsonAsync($"/api/admin/teams/{teamA}/disabled", new { disabled = true }, ct)).StatusCode);
            var me = await GetJsonAsync(device, "/api/auth/me");
            Assert.Equal([teamB], me.GetProperty("teams").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        }
    }
}
