using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Saved edits in Player 2: reference-only versions of an item, applied to Player 1 as cut-out parts and undone (badrfly, 2026-11-16).</summary>
    public sealed partial class AccountsTests
    {
        private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body, HttpStatusCode expected)
        {
            var response = await client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);
            Assert.Equal(expected, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        }

        private static async Task<JsonElement> MarkerOnTimelineAsync(HttpClient client, long vodId, int markerId) =>
            (await GetJsonAsync(client, $"/api/vods/{vodId}/timeline")).GetProperty("markers").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == markerId);

        [Fact]
        public async Task EditsAreVersionsThatOnlyChangeTheItemWhenApplied()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("ed1");
            var segment = await SegmentIdAsync(owner, 460, 10, 30);

            // Two edits of one segment: trimmed ends count as cuts
            var shortEdit = await PostJsonAsync(owner, "/api/edits",
                new { markerId = segment, title = "Short", cuts = new[] { new { start = 10, end = 12 }, new { start = 25, end = 30 } } }, HttpStatusCode.Created);
            var shortId = shortEdit.GetProperty("id").GetInt32();
            Assert.Equal("segment", shortEdit.GetProperty("kind").GetString());
            Assert.Equal([[10.0, 12.0], [25.0, 30.0]], CutsOf(shortEdit));
            Assert.Equal(13, shortEdit.GetProperty("keptSeconds").GetDouble());
            Assert.Equal(10, shortEdit.GetProperty("sourceStart").GetDouble());
            Assert.Equal(30, shortEdit.GetProperty("sourceEnd").GetDouble());
            Assert.True(shortEdit.GetProperty("canEdit").GetBoolean());
            var tinyId = (await PostJsonAsync(owner, "/api/edits", new { markerId = segment, title = "Tiny", cuts = new[] { new { start = 10, end = 20 } },
                layout = new { mode = "single" } }, HttpStatusCode.Created)).GetProperty("id").GetInt32();

            // Nothing on Player 1 changes while editing
            Assert.Empty(CutsOf(await MarkerOnTimelineAsync(owner, 460, segment)));
            var list = await GetJsonAsync(owner, $"/api/edits?markerId={segment}");
            Assert.Equal([tinyId, shortId], list.GetProperty("edits").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray());
            Assert.Equal("single", list.GetProperty("edits")[0].GetProperty("layout").GetProperty("mode").GetString());
            Assert.Empty(list.GetProperty("applies").EnumerateArray());
            Assert.True(list.GetProperty("canApply").GetBoolean());
            Assert.False(list.GetProperty("applyMakesCopy").GetBoolean());
            Assert.False(list.GetProperty("canRevert").GetBoolean());

            // Saving changes; bad ones are refused
            var saved = await (await owner.PutAsJsonAsync($"/api/edits/{shortId}", new { cuts = new[] { new { start = 10, end = 15 } } }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(15, saved.GetProperty("keptSeconds").GetDouble());
            Assert.Equal("Short", saved.GetProperty("title").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/edits/{shortId}", new { cuts = new[] { new { start = 0, end = 100 } } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/edits/{shortId}", new { parts = new[] { new { markerId = segment } } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/edits", new { markerId = await SegmentIdAsync(owner, 460, 50, null) }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/edits", new { markerId = segment, entryId = 1 }, ct)).StatusCode);

            // Applying one sets the segment's cut-out parts; the other edit stays as it was
            var applied = await PostJsonAsync(owner, $"/api/edits/{tinyId}/apply", new { }, HttpStatusCode.OK);
            Assert.False(applied.GetProperty("copied").GetBoolean());
            Assert.Equal(segment, applied.GetProperty("item").GetProperty("id").GetInt32());
            Assert.Equal([[10.0, 20.0]], CutsOf(applied.GetProperty("item")));
            Assert.Equal(10, (await MarkerOnTimelineAsync(owner, 460, segment)).GetProperty("keptSeconds").GetDouble());
            Assert.Equal(15, (await GetJsonAsync(owner, $"/api/edits/{shortId}")).GetProperty("keptSeconds").GetDouble());

            // Undo the last apply, revert to the original
            await PostJsonAsync(owner, $"/api/edits/{shortId}/apply", new { }, HttpStatusCode.OK);
            Assert.Equal([[10.0, 15.0]], CutsOf(await MarkerOnTimelineAsync(owner, 460, segment)));
            var history = await GetJsonAsync(owner, $"/api/edits?markerId={segment}");
            Assert.Equal(["Short", "Tiny"], history.GetProperty("applies").EnumerateArray().Select(x => x.GetProperty("editTitle").GetString()!).ToArray());
            Assert.True(history.GetProperty("canRevert").GetBoolean());
            var undone = await PostJsonAsync(owner, $"/api/markers/{segment}/revert", new { to = "previous" }, HttpStatusCode.OK);
            Assert.Equal([[10.0, 20.0]], CutsOf(undone));
            await PostJsonAsync(owner, $"/api/edits/{shortId}/apply", new { }, HttpStatusCode.OK);
            Assert.Empty(CutsOf(await PostJsonAsync(owner, $"/api/markers/{segment}/revert", new { to = "original" }, HttpStatusCode.OK)));
            var nothing = await PostJsonAsync(owner, $"/api/markers/{segment}/revert", new { to = "previous" }, HttpStatusCode.Conflict);
            Assert.Equal("nothing-to-undo", nothing.GetProperty("code").GetString());

            // Made longer, a trimmed end stays trimmed; made shorter, the edit is cut down to fit
            var endTrim = (await PostJsonAsync(owner, "/api/edits", new { markerId = segment, cuts = new[] { new { start = 25, end = 30 } } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/markers/{segment}", new { seconds = 10, endSeconds = 40 }, ct)).StatusCode);
            var longer = await GetJsonAsync(owner, $"/api/edits/{endTrim}");
            Assert.Equal([[25.0, 40.0]], CutsOf(longer));
            Assert.Equal(15, longer.GetProperty("keptSeconds").GetDouble());
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/markers/{segment}", new { seconds = 10, endSeconds = 20 }, ct)).StatusCode);
            Assert.Equal(10, (await GetJsonAsync(owner, $"/api/edits/{endTrim}")).GetProperty("keptSeconds").GetDouble());
            // "Tiny" now cuts all of it, so it can't be applied
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/edits/{tinyId}/apply", new { }, ct)).StatusCode);

            // Deleting the segment deletes its edits
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{segment}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/edits/{shortId}", ct)).StatusCode);
        }

        [Fact]
        public async Task TeammatesSeeEditsCopyThemAndApplyToACopy()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("ed2");
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries", "EditOwnEntries");
            await AssignAsync(owner, memberId, clipper);

            var segment = await SegmentIdAsync(owner, 461, 10, 30);
            var editId = (await PostJsonAsync(owner, "/api/edits", new { markerId = segment, title = "Short", cuts = new[] { new { start = 20, end = 30 } } },
                HttpStatusCode.Created)).GetProperty("id").GetInt32();

            // View only
            var seen = await GetJsonAsync(member, $"/api/edits?markerId={segment}");
            var ownerEdit = seen.GetProperty("edits").EnumerateArray().Single();
            Assert.False(ownerEdit.GetProperty("canEdit").GetBoolean());
            Assert.False(ownerEdit.GetProperty("canDelete").GetBoolean());
            Assert.Equal("ed2owner", ownerEdit.GetProperty("createdBy").GetString());
            Assert.True(seen.GetProperty("applyMakesCopy").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/edits/{editId}", new { title = "Mine" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/edits/{editId}", ct)).StatusCode);

            // Copy to work on their own
            var copy = await PostJsonAsync(member, $"/api/edits/{editId}/copy", new { }, HttpStatusCode.Created);
            Assert.Equal("Copy of Short", copy.GetProperty("title").GetString());
            Assert.Equal(memberId, copy.GetProperty("createdByUserId").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await member.PutAsJsonAsync($"/api/edits/{copy.GetProperty("id").GetInt32()}",
                new { cuts = new[] { new { start = 10, end = 12 } } }, ct)).StatusCode);

            // Applying to the owner's segment copies the segment onto the member's row and applies there
            var applied = await PostJsonAsync(member, $"/api/edits/{editId}/apply", new { }, HttpStatusCode.OK);
            Assert.True(applied.GetProperty("copied").GetBoolean());
            var item = applied.GetProperty("item");
            Assert.NotEqual(segment, item.GetProperty("id").GetInt32());
            Assert.Equal(segment, item.GetProperty("copiedFromMarkerId").GetInt32());
            Assert.Equal(memberId, item.GetProperty("createdByUserId").GetInt32());
            Assert.Equal([[20.0, 30.0]], CutsOf(item));
            Assert.Equal(memberId, applied.GetProperty("edit").GetProperty("createdByUserId").GetInt32());
            Assert.Equal(item.GetProperty("id").GetInt32(), applied.GetProperty("edit").GetProperty("markerId").GetInt32());
            Assert.Empty(CutsOf(await MarkerOnTimelineAsync(owner, 461, segment)));
            // The copy's apply can be undone by its owner; the original can't be changed by the member
            Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync($"/api/markers/{item.GetProperty("id").GetInt32()}/revert", new { }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/markers/{segment}/revert", new { }, ct)).StatusCode);
        }

        [Fact]
        public async Task VideoEditsCutTheirSegmentsAndApplyAsTheVideosCuts()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("ed3");
            var a = await SegmentIdAsync(owner, 462, 10, 20);
            var b = (await PostJsonAsync(owner, "/api/vods/462/markers", new { seconds = 40, endSeconds = 50, cuts = new[] { new { start = 40, end = 42 } } },
                HttpStatusCode.Created)).GetProperty("id").GetInt32();
            var video = await PostJsonAsync(owner, "/api/vods/462/videos", new { title = "Both", markerIds = new[] { a, b } }, HttpStatusCode.Created);
            var videoId = video.GetProperty("id").GetInt32();

            // a taken out whole, b trimmed: 8 s of b (its own cut stays) minus 5 s
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/edits", new { videoId,
                parts = new[] { new { markerId = a, cuts = new[] { new { start = 10, end = 20 } } }, new { markerId = b, cuts = new[] { new { start = 40, end = 50 } } } } }, ct)).StatusCode);
            var edit = await PostJsonAsync(owner, "/api/edits", new { videoId,
                parts = new[] { new { markerId = a, cuts = new[] { new { start = 10, end = 20 } } }, new { markerId = b, cuts = new[] { new { start = 45, end = 50 } } } } },
                HttpStatusCode.Created);
            Assert.Equal("video", edit.GetProperty("kind").GetString());
            Assert.Equal(3, edit.GetProperty("keptSeconds").GetDouble());
            Assert.Equal([a, b], edit.GetProperty("sourceParts").EnumerateArray().Select(x => x.GetProperty("markerId").GetInt32()).ToArray());
            Assert.Equal(JsonValueKind.Null, edit.GetProperty("sourceStart").ValueKind);
            Assert.Equal(2, edit.GetProperty("parts").GetArrayLength());

            var applied = await PostJsonAsync(owner, $"/api/edits/{edit.GetProperty("id").GetInt32()}/apply", new { }, HttpStatusCode.OK);
            Assert.Equal(3, applied.GetProperty("item").GetProperty("lengthSeconds").GetDouble());
            // The segments themselves don't change
            Assert.Empty(CutsOf(await MarkerOnTimelineAsync(owner, 462, a)));
            // An export of the video leaves out the segment that's cut out whole
            var export = await PostJsonAsync(owner, "/api/exports", new { items = new[] { new { kind = "video", videoId } } }, HttpStatusCode.Accepted);
            Assert.Equal(3, export.GetProperty("items")[0].GetProperty("lengthSeconds").GetDouble());

            var reverted = await PostJsonAsync(owner, $"/api/videos/{videoId}/revert", new { to = "original" }, HttpStatusCode.OK);
            Assert.Equal(18, reverted.GetProperty("lengthSeconds").GetDouble());
        }

        [Fact]
        public async Task StoredClipsKeepTheirFileWhenOnlyCutOutPartsChange()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ed4");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var clipId = AddEntry(teamId, ownerId, "Stored", vodId: 463);
            string filePath;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var entry = db.Entries.Single(x => x.Id == clipId);
                entry.Status = EntryStatus.Ready;
                entry.FilePath = filePath = Path.Combine("library", $"{clipId}.mp4");
                entry.StartTime = TimeSpan.FromSeconds(30);
                entry.EndTime = TimeSpan.FromSeconds(50);
                entry.VodLengthSeconds = 3600;
                entry.ClipLengthSeconds = 20;
                db.SaveChanges();
                Directory.CreateDirectory(Path.Combine(_dir, "downloads", "library"));
                await File.WriteAllTextAsync(Path.Combine(_dir, "downloads", filePath), "whole clip", ct);
            }

            var editId = (await PostJsonAsync(owner, "/api/edits", new { entryId = clipId, cuts = new[] { new { start = 30, end = 35 } } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();
            var applied = (await PostJsonAsync(owner, $"/api/edits/{editId}/apply", new { }, HttpStatusCode.OK)).GetProperty("item");
            Assert.Equal("Ready", applied.GetProperty("status").GetString());
            Assert.Equal(15, applied.GetProperty("clipLengthSeconds").GetDouble());
            Assert.Equal([[30.0, 35.0]], CutsOf(applied));
            Assert.False(applied.GetProperty("fileHasCuts").GetBoolean());
            Assert.Equal("whole clip", await (await owner.GetAsync($"/api/entries/{clipId}/file?preview=true", ct)).Content.ReadAsStringAsync(ct));

            // Changing cuts by hand keeps the file too
            var trimmed = await (await owner.PutAsJsonAsync($"/api/entries/{clipId}/trim",
                new { startTime = "30", endTime = "50", cuts = new[] { new { start = 40, end = 45 } } }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("Ready", trimmed.GetProperty("status").GetString());
            Assert.True(File.Exists(Path.Combine(_dir, "downloads", filePath)));

            // A file made the old way (without its cut-out parts) is made again
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var entry = db.Entries.Single(x => x.Id == clipId);
                entry.FileCuts = entry.Cuts;
                db.SaveChanges();
            }
            Assert.True((await GetJsonAsync(owner, $"/api/entries/{clipId}")).GetProperty("fileHasCuts").GetBoolean());
            await PostJsonAsync(owner, $"/api/entries/{clipId}/revert", new { to = "original" }, HttpStatusCode.OK);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var audits = db.AuditEvents.Where(x => x.EntryId == clipId && (x.Action == AuditActions.EditApplied || x.Action == AuditActions.EditReverted))
                    .OrderBy(x => x.Id).Select(x => x.Details!).ToList();
                Assert.Contains("same file", audits[0]);
                Assert.DoesNotContain("same file", audits[1]);
                Assert.Null(db.Entries.Single(x => x.Id == clipId).FileCuts);
            }

            // Twitch clips have no edits
            var twitchClip = AddEntry(teamId, ownerId, "Twitch", vodId: 463);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                db.Entries.Single(x => x.Id == twitchClip).Kind = EntryKind.Clip;
                db.SaveChanges();
            }
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/edits", new { entryId = twitchClip }, ct)).StatusCode);
        }
    }
}
