using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>
    /// Teammates' vertical edits (projects and 9:16 renders) by role (badrfly 2026-11-17): View sees them, Download also downloads and
    /// exports them, Edit anyone's re-edits or copies them, Delete anyone's deletes them. Without View a member has only their own.
    /// </summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task TeammatesHorizontalEditsAreOpenToTheTeam()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("va2");
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId,
                await CreateRoleAsync(owner, "Editor", "ViewLibrary", "AddEntries", "GenerateFiles")));
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();

            // badrfly 2026-11-17: anyone can open the clip a teammate cut and make their own vertical edit from it
            var content = TwoTracks(Piece("a", "t1", 0, 521, 200, 203.5));
            var horizontal = (await (await owner.PostAsJsonAsync("/api/projects", new { title = "Owner's cut", content }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var vertical = (await (await owner.PostAsJsonAsync("/api/projects", new { title = "Owner's 9:16", content, vertical = true }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            Assert.Equal(["Owner's cut"], (await GetJsonAsync(member, "/api/projects")).EnumerateArray().Select(x => x.GetProperty("title").GetString()));
            var seen = await GetJsonAsync(member, $"/api/projects/{horizontal}");
            Assert.True(seen.GetProperty("canCopy").GetBoolean());
            Assert.True(seen.GetProperty("canDownload").GetBoolean());
            Assert.False(seen.GetProperty("canEdit").GetBoolean()); // changing it in place still needs Edit anyone's
            Assert.False(seen.GetProperty("canDelete").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/projects/{horizontal}", new { title = "Taken" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/projects/{horizontal}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync($"/api/projects/{horizontal}/copy", new { }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await member.PostAsJsonAsync("/api/exports",
                new { items = new[] { new { kind = "project", projectId = horizontal, quality = "480p30" } } }, ct)).StatusCode);

            // Their vertical edits stay behind the role
            Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/projects/{vertical}", ct)).StatusCode);

            // "Edited by" filters and the VOD's work count the horizontal edit
            var work = (await GetJsonAsync(member, "/api/vods/work?vodIds=521")).EnumerateArray().Single();
            Assert.Equal(2, work.GetProperty("edits").GetInt32()); // the owner's horizontal one + the member's copy
            Assert.Empty((await GetJsonAsync(member, $"/api/entries?editedBy={ownerId}")).EnumerateArray()); // no clip parent here
        }

        [Fact]
        public async Task TeammatesVerticalEditsFollowTheRole()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("va1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();

            async Task Role(params string[] permissions) =>
                Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId,
                    await CreateRoleAsync(owner, "R" + Guid.NewGuid().ToString("N")[..8], ["ViewLibrary", "AddEntries", "GenerateFiles", .. permissions])));

            var content = TwoTracks(Piece("a", "t1", 0, 520, 200, 203.5));
            var ownersProject = (await (await owner.PostAsJsonAsync("/api/projects", new { title = "Owner's cut", content, vertical = true }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var clip = AddStoredClip(teamId, memberId, 450, 30, 40, "landscape");
            AddRender(clip, "20261117a", ownerId, DateTime.UtcNow.AddMinutes(-5), "owner's vertical");
            AddRender(clip, "20261117b", memberId, DateTime.UtcNow.AddMinutes(-2), "member's vertical");
            AddRender(clip, "20261117c", null, DateTime.UtcNow.AddMinutes(-9), "old vertical"); // maker unknown: a teammate's
            async Task<List<string?>> RenderIds() =>
                (await GetJsonAsync(member, $"/api/entries/{clip}/shortform")).GetProperty("renders").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
            HttpStatusCode Status(HttpResponseMessage x) => x.StatusCode;
            async Task<HttpResponseMessage> ExportProject() =>
                await member.PostAsJsonAsync("/api/exports", new { items = new[] { new { kind = "project", projectId = ownersProject, horizontal = false, vertical = true, quality = "480p30" } } }, ct);

            // No vertical permission: only their own
            await Role();
            var mine = (await (await member.PostAsJsonAsync("/api/projects", new { title = "Mine" }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct));
            Assert.True(mine.GetProperty("canCopy").GetBoolean());
            Assert.True(mine.GetProperty("canDownload").GetBoolean());
            Assert.Equal(["Mine"], (await GetJsonAsync(member, "/api/projects")).EnumerateArray().Select(x => x.GetProperty("title").GetString()));
            Assert.Equal(HttpStatusCode.NotFound, Status(await member.GetAsync($"/api/projects/{ownersProject}", ct)));
            Assert.Equal(HttpStatusCode.NotFound, Status(await member.PostAsJsonAsync($"/api/projects/{ownersProject}/copy", new { }, ct)));
            Assert.Equal(HttpStatusCode.BadRequest, Status(await ExportProject()));
            Assert.Equal(["20261117b"], await RenderIds());
            Assert.Equal(HttpStatusCode.NotFound, Status(await member.GetAsync($"/api/entries/{clip}/shortform/20261117a/file?inline=true", ct)));
            Assert.Equal(HttpStatusCode.NotFound, Status(await member.GetAsync($"/api/entries/{clip}/shortform/20261117c/file", ct)));
            Assert.Equal("member's vertical", await member.GetStringAsync($"/api/entries/{clip}/shortform/20261117b/file", ct));
            // The owner still sees everyone's
            Assert.Equal(2, (await GetJsonAsync(owner, "/api/projects")).GetArrayLength());
            Assert.Equal(3, (await GetJsonAsync(owner, $"/api/entries/{clip}/shortform")).GetProperty("renders").GetArrayLength());

            // View: listed and playable, nothing more
            await Role("ViewTeamVerticals");
            var seen = await GetJsonAsync(member, $"/api/projects/{ownersProject}");
            Assert.False(seen.GetProperty("canEdit").GetBoolean());
            Assert.False(seen.GetProperty("canCopy").GetBoolean());
            Assert.False(seen.GetProperty("canDownload").GetBoolean());
            Assert.False(seen.GetProperty("canDelete").GetBoolean());
            Assert.Equal(2, (await GetJsonAsync(member, "/api/projects")).GetArrayLength());
            Assert.Equal(HttpStatusCode.Forbidden, Status(await member.PostAsJsonAsync($"/api/projects/{ownersProject}/copy", new { }, ct)));
            Assert.Equal(HttpStatusCode.Forbidden, Status(await member.PutAsJsonAsync($"/api/projects/{ownersProject}", new { title = "Taken" }, ct)));
            Assert.Equal(HttpStatusCode.Forbidden, Status(await member.DeleteAsync($"/api/projects/{ownersProject}", ct)));
            Assert.Equal(HttpStatusCode.BadRequest, Status(await ExportProject()));
            Assert.Equal(["20261117b", "20261117a", "20261117c"], await RenderIds());
            Assert.Equal("owner's vertical", await member.GetStringAsync($"/api/entries/{clip}/shortform/20261117a/file?inline=true", ct));
            Assert.Equal(HttpStatusCode.Forbidden, Status(await member.GetAsync($"/api/entries/{clip}/shortform/20261117a/file", ct)));
            Assert.Equal(HttpStatusCode.Conflict, Status(await member.PostAsJsonAsync("/api/exports",
                new { items = new[] { new { kind = "clip", entryId = clip, horizontal = false, vertical = true, renderId = "20261117a" } } }, ct)));

            // Download: also downloads and exports them (and sees them without View)
            await Role("DownloadTeamVerticals");
            Assert.True((await GetJsonAsync(member, $"/api/projects/{ownersProject}")).GetProperty("canDownload").GetBoolean());
            Assert.Equal("owner's vertical", await member.GetStringAsync($"/api/entries/{clip}/shortform/20261117a/file", ct));
            if (HasFfmpeg())
                Assert.Equal(HttpStatusCode.Accepted, Status(await ExportProject()));
            Assert.Equal(HttpStatusCode.Forbidden, Status(await member.PostAsJsonAsync($"/api/projects/{ownersProject}/copy", new { }, ct)));

            // Edit anyone's: re-edit in place or copy; Delete anyone's: delete
            await Role("ViewTeamVerticals", "EditAnyEntries", "DeleteAnyEntries");
            Assert.Equal(HttpStatusCode.Created, Status(await member.PostAsJsonAsync($"/api/projects/{ownersProject}/copy", new { }, ct)));
            Assert.Equal(HttpStatusCode.OK, Status(await member.PutAsJsonAsync($"/api/projects/{ownersProject}", new { title = "Re-edited" }, ct)));
            Assert.Equal(HttpStatusCode.NoContent, Status(await member.DeleteAsync($"/api/projects/{ownersProject}", ct)));

            // Edit anyone's alone (a clipper role) doesn't reach teammates' vertical edits
            var another = (await (await owner.PostAsJsonAsync("/api/projects", new { title = "Owner's other", vertical = true }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            await Role("EditAnyEntries", "DeleteAnyEntries", "DeleteRenders");
            Assert.Equal(HttpStatusCode.NotFound, Status(await member.PutAsJsonAsync($"/api/projects/{another}", new { title = "Taken" }, ct)));
            Assert.Equal(HttpStatusCode.NotFound, Status(await member.DeleteAsync($"/api/entries/{clip}/shortform/20261117a", ct)));
            Assert.Equal(HttpStatusCode.NoContent, Status(await member.DeleteAsync($"/api/entries/{clip}/shortform/20261117b", ct)));
        }
    }
}
