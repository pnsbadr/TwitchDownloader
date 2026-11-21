using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>The team's own statuses and tags for clips and videos (badrfly 2026-11-20, "Redesign the library for large teams", step 4).</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task TeamsShapeTheirStatusesAndMembersSetThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("st1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();

            // A team starts with To do / Editing / Ready / Posted; a new clip is To do by itself
            var first = await GetJsonAsync(owner, "/api/team/statuses");
            var statuses = first.GetProperty("statuses").EnumerateArray().ToList();
            Assert.Equal(new[] { "To do", "Editing", "Ready", "Posted" }, statuses.Select(x => x.GetProperty("name").GetString()!).ToArray());
            int Status(string name) => statuses.First(x => x.GetProperty("name").GetString() == name).GetProperty("id").GetInt32();
            var (todo, editing, ready, posted) = (Status("To do"), Status("Editing"), Status("Ready"), Status("Posted"));
            Assert.True(first.GetProperty("canManage").GetBoolean());

            var clip = AddEntry(teamId, ownerId, "Knocked out", vodId: 720);
            var other = AddEntry(teamId, ownerId, "Rage quit", vodId: 720);
            var entry = await GetJsonAsync(owner, $"/api/entries/{clip}");
            Assert.Equal(todo, entry.GetProperty("teamStatusId").GetInt32());

            // Members who make clips set statuses and tags on anyone's clip
            var maker = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId, maker));
            Assert.Equal(HttpStatusCode.OK, (await member.PutAsJsonAsync($"/api/entries/{clip}/status", new { statusId = posted }, ct)).StatusCode);
            var tagged = await member.PutAsJsonAsync($"/api/entries/{clip}/tags", new { tags = new[] { "#Boxing", "big win", "boxing" } }, ct);
            Assert.Equal(HttpStatusCode.OK, tagged.StatusCode);
            entry = await GetJsonAsync(owner, $"/api/entries/{clip}");
            Assert.Equal(posted, entry.GetProperty("teamStatusId").GetInt32());
            Assert.Equal(new[] { "big-win", "boxing" }, entry.GetProperty("tags").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PutAsJsonAsync($"/api/entries/{clip}/tags", new { tags = new[] { "###" } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsJsonAsync($"/api/entries/{clip}/status", new { statusId = 999999 }, ct)).StatusCode);
            // ...but don't shape the team's statuses
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/team/statuses", new { name = "Scheduled" }, ct)).StatusCode);
            Assert.False((await GetJsonAsync(member, "/api/team/statuses")).GetProperty("canManage").GetBoolean());

            List<int> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
            Assert.Equal([clip], Ids(await GetJsonAsync(owner, $"/api/entries?paged=true&status={posted}")));
            Assert.Equal([clip], Ids(await GetJsonAsync(owner, "/api/entries?paged=true&tag=boxing")));
            var tagMatches = await GetJsonAsync(owner, "/api/vods/matches?tag=boxing");
            Assert.Equal(new[] { 720L }, tagMatches.GetProperty("vodIds").EnumerateArray().Select(x => x.GetInt64()).ToArray());

            // The owner makes Posted hidden from lists: it's left out unless picked
            var hidden = await owner.PutAsJsonAsync($"/api/team/statuses/{posted}", new { hideFromLists = true, color = "teal" }, ct);
            Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
            Assert.Equal([other], Ids(await GetJsonAsync(owner, "/api/entries?paged=true")));
            Assert.Equal([clip], Ids(await GetJsonAsync(owner, $"/api/entries?paged=true&status={posted}")));

            // The owner picks the tabs a status shows on (all at first)
            Assert.Equal(["vods", "videos", "clips"], (await GetJsonAsync(owner, "/api/team/statuses")).GetProperty("statuses")[0].GetProperty("tabs")
                .EnumerateArray().Select(x => x.GetString()!).ToArray());
            var tabbed = await owner.PutAsJsonAsync($"/api/team/statuses/{posted}", new { tabs = new[] { "videos" } }, ct);
            Assert.Equal(HttpStatusCode.OK, tabbed.StatusCode);
            var postedNow = (await tabbed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("statuses").EnumerateArray()
                .First(x => x.GetProperty("id").GetInt32() == posted);
            Assert.Equal(["videos"], postedNow.GetProperty("tabs").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Assert.True(postedNow.GetProperty("hideFromLists").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/team/statuses/{posted}", new { tabs = new[] { "nope" } }, ct)).StatusCode);

            // Owner's own steps: a new one, one trigger per status, order, names unique
            var added = await owner.PostAsJsonAsync("/api/team/statuses", new { name = "Needs vertical", color = "orange", setWhen = "EditMade" }, ct);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var after = (await added.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("statuses").EnumerateArray().ToList();
            var vertical = after.First(x => x.GetProperty("name").GetString() == "Needs vertical").GetProperty("id").GetInt32();
            Assert.Equal("ByHand", after.First(x => x.GetProperty("id").GetInt32() == editing).GetProperty("setWhen").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/team/statuses", new { name = "to DO" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/team/statuses/order", new { ids = new[] { todo, ready } }, ct)).StatusCode);
            var order = await owner.PutAsJsonAsync("/api/team/statuses/order", new { ids = new[] { todo, vertical, editing, ready, posted } }, ct);
            Assert.Equal(HttpStatusCode.OK, order.StatusCode);
            Assert.Equal(vertical, (await order.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("statuses")[1].GetProperty("id").GetInt32());

            // Set by itself only moves forward: Ready (exported) never goes back to Needs vertical (edit made)
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                await ItemStatuses.AdvanceAsync(db, teamId, StatusTrigger.EditMade, [other], null, ct);
                await db.SaveChangesAsync(ct);
                Assert.Equal(vertical, db.Entries.Single(x => x.Id == other).TeamStatusId);
                await ItemStatuses.AdvanceAsync(db, teamId, StatusTrigger.Exported, [other], null, ct);
                await db.SaveChangesAsync(ct);
                await ItemStatuses.AdvanceAsync(db, teamId, StatusTrigger.EditMade, [other], null, ct);
                await db.SaveChangesAsync(ct);
                Assert.Equal(ready, db.Entries.AsNoTracking().Single(x => x.Id == other).TeamStatusId);
            }

            // Deleting a status moves its items to another
            Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/team/statuses/{ready}?moveTo={posted}", ct)).StatusCode);
            Assert.Equal(posted, (await GetJsonAsync(owner, $"/api/entries/{other}")).GetProperty("teamStatusId").GetInt32());

            // Videos too
            var segment = await SegmentIdAsync(owner, 721, 10, 30);
            var video = (await PostJsonAsync(owner, "/api/vods/721/videos", new { title = "Story", markerIds = new[] { segment } }, HttpStatusCode.Created));
            Assert.Equal(todo, video.GetProperty("teamStatusId").GetInt32());
            var videoId = video.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/videos/{videoId}/tags", new { tags = new[] { "collab" } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/videos/{videoId}/status", new { statusId = vertical }, ct)).StatusCode);
            var listed = (await GetJsonAsync(owner, "/api/videos?paged=true&tag=collab")).GetProperty("items")[0];
            Assert.Equal(vertical, listed.GetProperty("teamStatusId").GetInt32());

            // Counts for the chips, and the team log
            var counts = await GetJsonAsync(owner, "/api/team/statuses");
            Assert.Equal(2, counts.GetProperty("statuses").EnumerateArray().First(x => x.GetProperty("id").GetInt32() == posted).GetProperty("clips").GetInt32());
            Assert.Contains(counts.GetProperty("tags").EnumerateArray(), x => x.GetProperty("name").GetString() == "collab" && x.GetProperty("videos").GetInt32() == 1);
            var log = await GetJsonAsync(owner, "/api/team/audit");
            Assert.Contains(log.EnumerateArray(), x => x.GetProperty("action").GetString() == "status.changed");
            Assert.Contains(log.EnumerateArray(), x => x.GetProperty("action").GetString() == "statuses.changed");
        }
    }
}
