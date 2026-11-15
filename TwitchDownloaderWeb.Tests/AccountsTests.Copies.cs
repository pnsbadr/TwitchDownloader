using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Copies of another member's segment or clip onto your own row (badrfly, 2026-11-15, frontend rework).</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task MembersCopyOthersItemsToTheirOwnRows()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("cp1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries", "EditOwnEntries");
            await AssignAsync(owner, memberId, clipper);

            // The owner's segment: the member can't change it, but can copy it
            var segment = await (await owner.PostAsJsonAsync("/api/vods/420/markers", new { seconds = 10, endSeconds = 25, label = "Fight", color = "red" }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            var segmentId = segment.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Forbidden,
                (await member.PutAsJsonAsync($"/api/markers/{segmentId}", new { seconds = 11, endSeconds = 25, label = "Fight" }, ct)).StatusCode);

            var copied = await member.PostAsJsonAsync($"/api/markers/{segmentId}/copy", new { }, ct);
            Assert.Equal(HttpStatusCode.Created, copied.StatusCode);
            var copy = await copied.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(segmentId, copy.GetProperty("copiedFromMarkerId").GetInt32());
            Assert.Equal(memberId, copy.GetProperty("createdByUserId").GetInt32());
            Assert.Equal("red", copy.GetProperty("color").GetString());
            Assert.True(copy.GetProperty("canEdit").GetBoolean());
            var timeline = await GetJsonAsync(member, "/api/vods/420/timeline");
            var memberVideoRow = RowsOf(timeline, memberId).Single(x => x.GetProperty("kind").GetString() == "Video").GetProperty("id").GetInt32();
            var memberClipRow = RowsOf(timeline, memberId).Single(x => x.GetProperty("kind").GetString() == "Clip").GetProperty("id").GetInt32();
            Assert.Equal(memberVideoRow, copy.GetProperty("rowId").GetInt32());
            // Never onto someone else's row, or a clip row
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync($"/api/markers/{segmentId}/copy", new { rowId = memberClipRow }, ct)).StatusCode);
            var ownerVideoRow = segment.GetProperty("rowId").GetInt32();
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync($"/api/markers/{segmentId}/copy", new { rowId = ownerVideoRow }, ct)).StatusCode);

            // The owner's stored clip: the copy gets its own file, no new download
            var clipId = AddEntry(teamId, ownerId, "Owner's clip", vodId: 420);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var entry = db.Entries.Single(x => x.Id == clipId);
                entry.Status = EntryStatus.Ready;
                entry.FilePath = Path.Combine("library", $"{clipId}.mp4");
                entry.StartTime = TimeSpan.FromSeconds(30);
                entry.EndTime = TimeSpan.FromSeconds(50);
                db.SaveChanges();
                Directory.CreateDirectory(Path.Combine(_dir, "downloads", "library"));
                await File.WriteAllTextAsync(Path.Combine(_dir, "downloads", entry.FilePath), "not really a video", ct);
            }

            Assert.Equal(HttpStatusCode.Forbidden, (await member.PatchAsJsonAsync($"/api/entries/{clipId}", new { title = "Mine" }, ct)).StatusCode);
            var clipCopy = await member.PostAsJsonAsync($"/api/entries/{clipId}/copy", new { }, ct);
            Assert.Equal(HttpStatusCode.Created, clipCopy.StatusCode);
            var clip = await clipCopy.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("Ready", clip.GetProperty("status").GetString());
            Assert.Equal(clipId, clip.GetProperty("copiedFromEntryId").GetInt32());
            Assert.Equal(memberClipRow, clip.GetProperty("rowId").GetInt32());
            Assert.Equal("cp1member", clip.GetProperty("addedBy").GetString());
            var file = await member.GetAsync($"/api/entries/{clip.GetProperty("id").GetInt32()}/file", ct);
            Assert.Equal("not really a video", await file.Content.ReadAsStringAsync(ct));
            // The copy is the member's to edit
            Assert.Equal(HttpStatusCode.OK, (await member.PatchAsJsonAsync($"/api/entries/{clip.GetProperty("id").GetInt32()}", new { title = "Mine" }, ct)).StatusCode);

            // Alt+drag: the clip as a segment at exactly its times
            var asSegment = await member.PostAsJsonAsync($"/api/entries/{clipId}/copy-as-segment", new { rowId = memberVideoRow }, ct);
            Assert.Equal(HttpStatusCode.Created, asSegment.StatusCode);
            var made = await asSegment.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(30, made.GetProperty("seconds").GetDouble());
            Assert.Equal(50, made.GetProperty("endSeconds").GetDouble());
            Assert.Equal("Owner's clip", made.GetProperty("label").GetString());
            Assert.Equal(clipId, made.GetProperty("copiedFromEntryId").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync($"/api/entries/{clipId}/copy-as-segment", new { rowId = memberClipRow }, ct)).StatusCode);

            // Without AddEntries no copies
            var viewer = await CreateRoleAsync(owner, "Viewer", "ViewLibrary");
            await AssignAsync(owner, memberId, viewer);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/markers/{segmentId}/copy", new { }, ct)).StatusCode);
        }
    }
}
