using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Logins, one-time links and team scoping, through the real HTTP pipeline (TestServer).</summary>
    public sealed class AccountsTests : IDisposable
    {
        private const string StaffPassword = "staff-password-1";
        private const string Password = "user-password-1";

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-web-accounts-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> _factory;
        private readonly int _mainTeamEntryId;

        public AccountsTests()
        {
            Directory.CreateDirectory(_dir);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Database:Path", Path.Combine(_dir, "test.db"));
                builder.UseSetting("Downloads:OutputFolder", Path.Combine(_dir, "downloads"));
                builder.UseSetting("Auth:LoginAttemptsPerMinute", "100000");
                builder.UseSetting("AudioRescue:Enabled", "false");
            });

            // Starts the app (runs the migrations), then adds the staff Owner and an old entry in the migrated "Main team"
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.StaffAccounts.Add(new StaffAccount
            {
                Username = "boss", NormalizedUsername = "boss", PasswordHash = AccountRules.HashPassword(StaffPassword),
                SecurityStamp = AccountRules.NewSecurityStamp(), IsOwner = true, CreatedAtUtc = DateTime.UtcNow,
            });
            var entry = new LibraryEntry { TeamId = 1, Title = "Old clip", VodId = 1, Status = EntryStatus.ReferenceOnly, AddedAtUtc = DateTime.UtcNow };
            db.Entries.Add(entry);
            db.SaveChanges();
            _mainTeamEntryId = entry.Id;
        }

        public void Dispose()
        {
            _factory.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        private async Task<HttpClient> StaffClientAsync()
        {
            var client = NewClient();
            var login = await client.PostAsJsonAsync("/api/admin/login", new { username = "boss", password = StaffPassword }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
            return client;
        }

        private static async Task<string> MakeLinkAsync(HttpClient client, string url, object body)
        {
            var response = await client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var linkUrl = json.GetProperty("url").GetString()!;
            Assert.Contains("/join.html#", linkUrl);
            return linkUrl[(linkUrl.IndexOf('#') + 1)..];
        }

        private static Task<HttpResponseMessage> AcceptAsync(HttpClient client, string token, string username, bool newAccount, string? teamName = null, string password = Password) =>
            client.PostAsJsonAsync("/api/links/accept", new { token, newAccount, username, password, teamName }, TestContext.Current.CancellationToken);

        private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task LibraryNeedsALogin()
        {
            var client = NewClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/entries", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/entries/{_mainTeamEntryId}/file", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/channels", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/teams", TestContext.Current.CancellationToken)).StatusCode);
        }

        [Fact]
        public async Task OwnerSetupIsOnlyAllowedLocally()
        {
            // TestServer requests have no remote address, so they don't count as coming from the machine itself
            var client = NewClient();
            var response = await client.PostAsJsonAsync("/api/admin/setup", new { username = "intruder", password = "long-enough-pw" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task RegistrationLinkMakesANewTeamWithItsOwnLibrary()
        {
            var staff = await StaffClientAsync();
            var token = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });

            var owner = NewClient();
            var accepted = await AcceptAsync(owner, token, "creator1", newAccount: true, teamName: "Creator team");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            var me = await GetJsonAsync(owner, "/api/auth/me");
            Assert.Equal("Creator team", me.GetProperty("teams")[0].GetProperty("name").GetString());
            Assert.True(me.GetProperty("teams")[0].GetProperty("isOwner").GetBoolean());

            // The new team's library is empty and the Main team's entry is invisible to it
            Assert.Equal(0, (await GetJsonAsync(owner, "/api/entries")).GetArrayLength());
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/entries/{_mainTeamEntryId}", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/entries/{_mainTeamEntryId}", TestContext.Current.CancellationToken)).StatusCode);

            // One time only
            var again = await AcceptAsync(NewClient(), token, "creator2", newAccount: true, teamName: "Second");
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        [Fact]
        public async Task OwnerLinkClaimsTheMainTeamAndAnAccountCanBeInSeveralTeams()
        {
            var staff = await StaffClientAsync();
            var registration = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });
            var user = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(user, registration, "badrfly", newAccount: true, teamName: "Side team")).StatusCode);

            // Same account (logged in) takes over the Main team
            var ownership = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "ownership", teamId = 1 });
            var accepted = await user.PostAsJsonAsync("/api/links/accept", new { token = ownership, newAccount = false }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            var me = await GetJsonAsync(user, "/api/auth/me");
            Assert.Equal(2, me.GetProperty("teams").GetArrayLength());
            Assert.Equal(1, me.GetProperty("currentTeamId").GetInt32());

            var entries = await GetJsonAsync(user, "/api/entries");
            Assert.Equal("Old clip", entries[0].GetProperty("title").GetString());

            // Switching team switches the library
            var sideTeam = me.GetProperty("teams").EnumerateArray().First(x => x.GetProperty("name").GetString() == "Side team").GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsJsonAsync("/api/auth/team", new { teamId = sideTeam }, TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(0, (await GetJsonAsync(user, "/api/entries")).GetArrayLength());

            // Can't switch to a team you aren't in
            Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsJsonAsync("/api/auth/team", new { teamId = 999 }, TestContext.Current.CancellationToken)).StatusCode);
        }

        [Fact]
        public async Task InvitedMembersStartWithNoAccess()
        {
            var staff = await StaffClientAsync();
            var registration = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });
            var owner = NewClient();
            await AcceptAsync(owner, registration, "teamowner", newAccount: true, teamName: "Team A");

            var invite = await MakeLinkAsync(owner, "/api/team/invites", new { note = "Sam" });
            var member = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(member, invite, "sam", newAccount: true)).StatusCode);

            var me = await GetJsonAsync(member, "/api/auth/me");
            Assert.False(me.GetProperty("teams")[0].GetProperty("isOwner").GetBoolean());

            foreach (var url in new[] { "/api/entries", "/api/channels", "/api/team", "/api/team/invites" })
            {
                var response = await member.GetAsync(url, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Contains("no-access", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }

            // The owner sees them and can remove them
            var team = await GetJsonAsync(owner, "/api/team");
            var sam = team.GetProperty("members").EnumerateArray().First(x => x.GetProperty("username").GetString() == "sam").GetProperty("userId").GetInt32();
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/team/members/{sam}", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/entries", TestContext.Current.CancellationToken)).StatusCode);
        }

        [Fact]
        public async Task RevokedLinksDontWork()
        {
            var staff = await StaffClientAsync();
            var create = await staff.PostAsJsonAsync("/api/admin/links", new { kind = "registration" }, TestContext.Current.CancellationToken);
            var json = await create.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var url = json.GetProperty("url").GetString()!;
            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/admin/links/{json.GetProperty("id").GetInt32()}", TestContext.Current.CancellationToken)).StatusCode);

            var response = await AcceptAsync(NewClient(), url[(url.IndexOf('#') + 1)..], "late", newAccount: true, teamName: "Late team");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task TeamLoginsAndStaffLoginsAreSeparate()
        {
            var staff = await StaffClientAsync();
            // A staff session is not a team session
            Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/entries", TestContext.Current.CancellationToken)).StatusCode);

            var registration = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });
            var user = NewClient();
            await AcceptAsync(user, registration, "editor", newAccount: true, teamName: "Team B");
            // ...and a team session is not a staff session
            Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/admin/teams", TestContext.Current.CancellationToken)).StatusCode);
            // Staff usernames don't log in as users
            var wrong = await NewClient().PostAsJsonAsync("/api/auth/login", new { username = "boss", password = StaffPassword }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        [Fact]
        public async Task WrongPasswordsLockTheAccount()
        {
            var staff = await StaffClientAsync();
            var registration = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });
            await AcceptAsync(NewClient(), registration, "target", newAccount: true, teamName: "Team C");

            var client = NewClient();
            for (var i = 0; i < AccountRules.MaxFailedLogins; i++)
            {
                var attempt = await client.PostAsJsonAsync("/api/auth/login", new { username = "target", password = "wrong-password" }, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
            }

            var locked = await client.PostAsJsonAsync("/api/auth/login", new { username = "target", password = Password }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

            // Nothing token- or password-like reaches the audit log
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var details = await db.AuditEvents.Select(x => x.Details ?? "").ToListAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(details, x => x.Contains("wrong-password") || x.Contains(Password) || x.Contains(registration));
        }

        // ---------- roles and permissions ----------

        /// <summary>A new team with its owner, and one invited member who has no role yet.</summary>
        private async Task<(HttpClient Owner, HttpClient Member, int TeamId, int MemberId)> TeamWithMemberAsync(string prefix)
        {
            var staff = await StaffClientAsync();
            var registration = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });
            var owner = NewClient();
            await AcceptAsync(owner, registration, prefix + "owner", newAccount: true, teamName: prefix + " team");
            var invite = await MakeLinkAsync(owner, "/api/team/invites", new { });
            var member = NewClient();
            await AcceptAsync(member, invite, prefix + "member", newAccount: true);

            var me = await GetJsonAsync(member, "/api/auth/me");
            return (owner, member, me.GetProperty("currentTeamId").GetInt32(), me.GetProperty("id").GetInt32());
        }

        private static async Task<int> CreateRoleAsync(HttpClient client, string name, params string[] permissions)
        {
            var response = await client.PostAsJsonAsync("/api/team/roles", new { name, permissions }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt32();
        }

        private static async Task<HttpStatusCode> AssignAsync(HttpClient client, int userId, int? roleId) =>
            (await client.PutAsJsonAsync($"/api/team/members/{userId}/role", new { roleId }, TestContext.Current.CancellationToken)).StatusCode;

        private int AddEntry(int teamId, int? addedBy, string title)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var entry = new LibraryEntry { TeamId = teamId, Title = title, VodId = 1, Status = EntryStatus.ReferenceOnly, AddedAtUtc = DateTime.UtcNow,
                AddedByUserId = addedBy?.ToString() };
            db.Entries.Add(entry);
            db.SaveChanges();
            return entry.Id;
        }

        [Fact]
        public async Task RolesDecideWhatMembersCanDo()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("r1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var mine = AddEntry(teamId, memberId, "Mine");
            var theirs = AddEntry(teamId, ownerId, "Theirs");

            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "EditOwnEntries", "DeleteOwnEntries");
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId, clipper));

            var me = await GetJsonAsync(member, "/api/auth/me");
            Assert.Equal(["ViewLibrary", "EditOwnEntries", "DeleteOwnEntries"], me.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToArray());

            Assert.Equal(2, (await GetJsonAsync(member, "/api/entries")).GetArrayLength());
            Assert.Equal(HttpStatusCode.OK, (await member.PatchAsJsonAsync($"/api/entries/{mine}", new { title = "Renamed" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PatchAsJsonAsync($"/api/entries/{theirs}", new { title = "Nope" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/entries/{theirs}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/entries/{mine}", ct)).StatusCode);

            // Not in the role at all
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/entries", new { vodId = "123" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/channels", new { login = "arky" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/team", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/entries/{theirs}/generate", new { }, ct)).StatusCode);
            // Owner-only dev endpoint, even with every permission
            var all = await CreateRoleAsync(owner, "Everything", TeamPermissions.Catalog.Select(x => x.Info.Key).ToArray());
            await AssignAsync(owner, memberId, all);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/vods/download", new { vodId = "123" }, ct)).StatusCode);

            // Taking the role away takes the access away
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId, null));
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/entries", ct)).StatusCode);
        }

        [Fact]
        public async Task MembersCantGrantMoreThanTheyHave()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, mod, teamId, modId) = await TeamWithMemberAsync("r2");
            var invite = await MakeLinkAsync(owner, "/api/team/invites", new { });
            var clipperClient = NewClient();
            await AcceptAsync(clipperClient, invite, "r2clipper", newAccount: true);
            var clipperId = (await GetJsonAsync(clipperClient, "/api/auth/me")).GetProperty("id").GetInt32();
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();

            var moderator = await CreateRoleAsync(owner, "Moderator", "ViewLibrary", "AddEntries", "AssignRoles", "ManageRoles", "RemoveMembers");
            var admin = await CreateRoleAsync(owner, "Admin", "ViewLibrary", "DeleteAnyEntries", "AssignRoles");
            await AssignAsync(owner, modId, moderator);

            // Can make and give a role within their own permissions
            var viewer = await CreateRoleAsync(mod, "Viewer", "ViewLibrary");
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(mod, clipperId, viewer));

            // ...but not one with permissions they lack
            var tooMuch = await mod.PostAsJsonAsync("/api/team/roles", new { name = "Deleter", permissions = new[] { "DeleteAnyEntries" } }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, await AssignAsync(mod, clipperId, admin));
            Assert.Equal(HttpStatusCode.Forbidden, (await mod.PutAsJsonAsync($"/api/team/roles/{admin}", new { name = "Admin", permissions = new[] { "ViewLibrary" } }, ct)).StatusCode);

            // Never their own role or the owner
            Assert.Equal(HttpStatusCode.Forbidden, await AssignAsync(mod, modId, viewer));
            Assert.Equal(HttpStatusCode.Forbidden, (await mod.PutAsJsonAsync($"/api/team/roles/{moderator}",
                new { name = "Moderator", permissions = new[] { "ViewLibrary", "AddEntries", "AssignRoles", "ManageRoles", "RemoveMembers" } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, await AssignAsync(mod, ownerId, viewer));
            Assert.Equal(HttpStatusCode.Forbidden, (await mod.DeleteAsync($"/api/team/members/{ownerId}", ct)).StatusCode);

            // Someone whose role is beyond theirs can't be touched either
            await AssignAsync(owner, clipperId, admin);
            Assert.Equal(HttpStatusCode.Forbidden, (await mod.DeleteAsync($"/api/team/members/{clipperId}", ct)).StatusCode);
            await AssignAsync(owner, clipperId, viewer);
            Assert.Equal(HttpStatusCode.NoContent, (await mod.DeleteAsync($"/api/team/members/{clipperId}", ct)).StatusCode);

            // Deleting a role leaves its members with no access
            await AssignAsync(owner, modId, admin);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/team/roles/{admin}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await mod.GetAsync("/api/entries", ct)).StatusCode);
        }

        [Fact]
        public async Task ActivityShowsWhatEachMemberDid()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("r3");
            var role = await CreateRoleAsync(owner, "Editor", "ViewLibrary", "EditOwnEntries");
            await AssignAsync(owner, memberId, role);
            var entry = AddEntry(teamId, memberId, "Clip one");
            await member.PatchAsJsonAsync($"/api/entries/{entry}", new { title = "Clip one v2" }, ct);
            await member.PatchAsJsonAsync($"/api/entries/{entry}", new { title = "Clip one v3" }, ct);

            var activity = await GetJsonAsync(owner, "/api/team/activity?days=7");
            var row = activity.GetProperty("members").EnumerateArray().First(x => x.GetProperty("userId").GetInt32() == memberId);
            Assert.Equal(2, row.GetProperty("counts").GetProperty("edited").GetInt32());
            Assert.Equal(1, row.GetProperty("entriesAdded").GetInt32());
            Assert.Equal("Editor", row.GetProperty("roleName").GetString());

            var log = await GetJsonAsync(owner, $"/api/team/audit?userId={memberId}");
            Assert.All(log.EnumerateArray(), x => Assert.Equal("r3member", x.GetProperty("username").GetString()));
            Assert.Contains(log.EnumerateArray(), x => x.GetProperty("action").GetString() == AuditActions.EntryRenamed
                                                     && x.GetProperty("entryTitle").GetString() == "Clip one v3");

            // The member can't see it
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/team/activity", ct)).StatusCode);
        }

        [Fact]
        public async Task ResetLinksOnlyForMembersInNoOtherTeam()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("r4");

            var reset = await MakeLinkAsync(owner, $"/api/team/members/{memberId}/password-reset", new { });
            var info = await (await NewClient().PostAsJsonAsync("/api/links/inspect", new { token = reset }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("r4member", info.GetProperty("forUsername").GetString());

            var accepted = await NewClient().PostAsJsonAsync("/api/links/accept", new { token = reset, password = "brand-new-password" }, ct);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            // The old session is signed out; the new password works, the old one doesn't
            Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/auth/me", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await NewClient().PostAsJsonAsync("/api/auth/login", new { username = "r4member", password = "brand-new-password" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().PostAsJsonAsync("/api/auth/login", new { username = "r4member", password = Password }, ct)).StatusCode);

            // Someone who also works for another team can only be reset by app staff
            var (other, _, _, _) = await TeamWithMemberAsync("r5");
            var invite = await MakeLinkAsync(other, "/api/team/invites", new { });
            var again = NewClient();
            await again.PostAsJsonAsync("/api/auth/login", new { username = "r4member", password = "brand-new-password" }, ct);
            Assert.Equal(HttpStatusCode.OK, (await again.PostAsJsonAsync("/api/links/accept", new { token = invite, newAccount = false }, ct)).StatusCode);
            var blocked = await owner.PostAsJsonAsync($"/api/team/members/{memberId}/password-reset", new { }, ct);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        }

        [Fact]
        public async Task ChangingThePasswordSignsOutOtherSessions()
        {
            var ct = TestContext.Current.CancellationToken;
            var (_, member, _, _) = await TeamWithMemberAsync("r6");
            var otherDevice = NewClient();
            await otherDevice.PostAsJsonAsync("/api/auth/login", new { username = "r6member", password = Password }, ct);

            var wrong = await member.PostAsJsonAsync("/api/auth/password", new { currentPassword = "nope-nope-nope", newPassword = "another-password" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            var changed = await member.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = "another-password" }, ct);
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/auth/me", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/api/auth/me", ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await member.PutAsJsonAsync("/api/auth/settings", new { timeZone = "pacific" }, ct)).StatusCode);
            Assert.Equal("pacific", (await GetJsonAsync(member, "/api/auth/me")).GetProperty("timeZone").GetString());
        }
    }
}
