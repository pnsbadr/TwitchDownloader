using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Exports (the Export tab's list) and vertical picks (badrfly, 2026-11-15, frontend rework).</summary>
    public sealed partial class AccountsTests
    {
        private static async Task<JsonElement> WaitForExportAsync(HttpClient client, int id)
        {
            for (var i = 0; i < 600; i++)
            {
                var export = await GetJsonAsync(client, $"/api/exports/{id}");
                if (export.GetProperty("status").GetString() is "Ready" or "Failed")
                    return export;
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException($"Export {id} didn't finish.");
        }

        /// <summary>A finished vertical render of the entry, as the shortform service leaves it on disk.</summary>
        private void AddRender(int entryId, string renderId, int? userId, DateTime createdAtUtc, string content)
        {
            var folder = Path.Combine(_dir, "downloads", LibraryStorage.ShortformFolderName, entryId.ToString());
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"{renderId}.mp4"), content);
            File.WriteAllText(Path.Combine(folder, $"{renderId}.json"), JsonSerializer.Serialize(new
            {
                createdAtUtc,
                layout = new { mode = "single", top = new { x = 0, y = 0, width = 0.3, height = 1 } },
                createdByUserId = userId,
            }));
        }

        /// <summary>A Ready stored clip of the member with a small file on disk.</summary>
        private int AddStoredClip(int teamId, int userId, long vodId, double start, double end, string content)
        {
            var id = AddEntry(teamId, userId, "Stored clip", vodId: vodId);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var entry = db.Entries.Single(x => x.Id == id);
            entry.Status = EntryStatus.Ready;
            entry.StartTime = TimeSpan.FromSeconds(start);
            entry.EndTime = TimeSpan.FromSeconds(end);
            entry.VodLengthSeconds = 600;
            entry.FilePath = LibraryStorage.StoredRelativePath(id);
            db.SaveChanges();
            Directory.CreateDirectory(Path.Combine(_dir, "downloads", LibraryStorage.StoredFolderName));
            File.WriteAllText(Path.Combine(_dir, "downloads", entry.FilePath), content);
            return id;
        }

        [Fact]
        public async Task ExportsCheckEachItemAndPickVerticalRendersByRole()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("ex1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var maker = await CreateRoleAsync(owner, "Maker", "ViewLibrary", "AddEntries", "GenerateFiles");
            await AssignAsync(owner, memberId, maker);

            var moment = await SegmentIdAsync(member, 450, 5, null);
            var segment = await SegmentIdAsync(member, 450, 10, 20);
            var video = (await (await member.PostAsJsonAsync("/api/vods/450/videos", new { title = "V", markerIds = new[] { segment } }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var clip = AddStoredClip(teamId, memberId, 450, 30, 40, "landscape");

            async Task<HttpResponseMessage> Export(HttpClient client, params object[] items) =>
                await client.PostAsJsonAsync("/api/exports", new { items }, ct);

            foreach (var bad in new object[]
                     {
                         new { kind = "reel" },
                         new { kind = "segment", markerId = moment },
                         new { kind = "vod", vodId = 450, start = 10, end = 10.2 },
                         new { kind = "vod", vodId = 450, start = 10, end = 99999 },
                         new { kind = "clip", entryId = clip, horizontal = false, vertical = false },
                         new { kind = "segment", markerId = segment, quality = "4k" },
                     })
                Assert.Equal(HttpStatusCode.BadRequest, (await Export(member, bad)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/exports", new { items = Array.Empty<object>() }, ct)).StatusCode);

            // Render as one needs the team's setting
            var joined = await Export(member, new { kind = "video", videoId = video, joined = true });
            Assert.Equal(HttpStatusCode.Forbidden, joined.StatusCode);
            Assert.Equal("combine-not-enabled", await CodeOf(joined));

            // Vertical: no render of their own yet
            AddRender(clip, "20261115a", ownerId, DateTime.UtcNow.AddMinutes(-5), "owner's vertical");
            var none = await Export(member, new { kind = "clip", entryId = clip, horizontal = false, vertical = true });
            Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
            Assert.Equal("no-vertical", await CodeOf(none));
            // Someone else's needs "Download team vertical entries"
            Assert.Equal(HttpStatusCode.Conflict, (await Export(member, new { kind = "clip", entryId = clip, vertical = true, horizontal = false, renderId = "20261115a" })).StatusCode);
            // Their own newest is used by default
            AddRender(clip, "20261115b", memberId, DateTime.UtcNow.AddMinutes(-3), "member's old vertical");
            AddRender(clip, "20261115c", memberId, DateTime.UtcNow.AddMinutes(-1), "member's vertical");
            var renders = (await GetJsonAsync(member, $"/api/entries/{clip}/shortform")).GetProperty("renders").EnumerateArray().ToList();
            Assert.Equal("ex1member", renders.Single(x => x.GetProperty("id").GetString() == "20261115c").GetProperty("createdBy").GetString());
            var mine = await Export(member, new { kind = "clip", entryId = clip, horizontal = true, vertical = true, fileName = "Best / bit" });
            Assert.Equal(HttpStatusCode.Accepted, mine.StatusCode);
            var batch = await mine.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(["Clip", "ClipVertical"], batch.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("kind").GetString()));
            Assert.Equal("20261115c", batch.GetProperty("items")[1].GetProperty("renderId").GetString());

            // With the permission the team's renders can be picked
            var picker = await CreateRoleAsync(owner, "Picker", "ViewLibrary", "AddEntries", "GenerateFiles", "DownloadTeamVerticals");
            await AssignAsync(owner, memberId, picker);
            var theirs = await Export(member, new { kind = "clip", entryId = clip, horizontal = false, vertical = true, renderId = "20261115a" });
            Assert.Equal(HttpStatusCode.Accepted, theirs.StatusCode);

            // The copied files: no cut was made for a stored clip at its own quality, nor for a render
            var done = await WaitForExportAsync(member, batch.GetProperty("id").GetInt32());
            Assert.Equal("Ready", done.GetProperty("status").GetString());
            Assert.Equal("landscape", await member.GetStringAsync(done.GetProperty("items")[0].GetProperty("fileUrl").GetString(), ct));
            var vertical = await member.GetAsync(done.GetProperty("items")[1].GetProperty("fileUrl").GetString(), ct);
            Assert.Equal("member's vertical", await vertical.Content.ReadAsStringAsync(ct));
            Assert.Contains("(vertical).mp4", vertical.Content.Headers.ContentDisposition!.ToString());
            Assert.DoesNotContain(_cutter.Cuts, x => x.VodId == 450);
            var other = await WaitForExportAsync(member, (await theirs.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32());
            Assert.Equal("owner's vertical", await member.GetStringAsync(other.GetProperty("items")[0].GetProperty("fileUrl").GetString(), ct));

            // Listed for the member who made them; only they (or DeleteAnyEntries) delete them
            Assert.Equal(2, (await GetJsonAsync(member, "/api/exports")).GetArrayLength());
            Assert.Empty((await GetJsonAsync(owner, "/api/exports")).EnumerateArray());
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/exports/{other.GetProperty("id").GetInt32()}", ct)).StatusCode);
            Assert.Single((await GetJsonAsync(member, "/api/exports")).EnumerateArray());
        }

        [Fact]
        public async Task ExportsCutVodRangesSegmentsAndVideosIntoFiles()
        {
            if (!HasFfmpeg())
                Assert.Skip("ffmpeg not found; set FFMPEG_PATH.");
            var ct = TestContext.Current.CancellationToken;
            _vods.Vods[451] = new VodDetails(451, "Long stream", "arky", 1000, DateTime.UtcNow.AddDays(-1), null);
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ex2");
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                db.Teams.Single(x => x.Id == teamId).CombineSegmentDownloads = true;
                db.SaveChanges();
            }

            var a = await SegmentIdAsync(owner, 451, 10, 13);
            var bMade = await owner.PostAsJsonAsync("/api/vods/451/markers",
                new { seconds = 20, endSeconds = 26, label = "B", cuts = new[] { new { start = 22, end = 24 } } }, ct);
            var b = (await bMade.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var video = (await (await owner.PostAsJsonAsync("/api/vods/451/videos", new { title = "Two bits", markerIds = new[] { b, a } }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            var started = await owner.PostAsJsonAsync("/api/exports", new
            {
                items = new object[]
                {
                    new { kind = "vod", vodId = 451, start = 100, end = 102.5, fileName = "One-off", quality = "720p" },
                    new { kind = "segment", markerId = b },
                    new { kind = "video", videoId = video },
                    new { kind = "video", videoId = video, joined = true, fileName = "Joined" },
                    new { kind = "vod", vodId = 451, start = 666, end = 668 }, // the fake cutter fails this one
                },
            }, ct);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var first = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("720p30", first.GetProperty("items")[0].GetProperty("quality").GetString());
            Assert.Equal(4, first.GetProperty("items")[1].GetProperty("lengthSeconds").GetDouble());
            Assert.Equal(7, first.GetProperty("items")[2].GetProperty("lengthSeconds").GetDouble());

            // A VOD range is a one-off: nothing new in the library or on the timeline
            var export = await WaitForExportAsync(owner, first.GetProperty("id").GetInt32());
            Assert.Equal("Ready", export.GetProperty("status").GetString());
            Assert.Equal("1 of 5 items failed.", export.GetProperty("error").GetString());
            Assert.Equal(5, export.GetProperty("itemsDone").GetInt32());
            var items = export.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal("Failed", items[4].GetProperty("status").GetString());
            Assert.Empty((await GetJsonAsync(owner, "/api/vods/451/timeline")).GetProperty("clips").EnumerateArray());

            async Task<string> Save(JsonElement item, string extension)
            {
                var path = Path.Combine(_dir, $"{Guid.NewGuid():N}{extension}");
                await File.WriteAllBytesAsync(path, await owner.GetByteArrayAsync(item.GetProperty("fileUrl").GetString(), ct), ct);
                return path;
            }

            Assert.InRange(Duration(await Save(items[0], ".mp4")), 2.3, 2.8);
            Assert.InRange(Duration(await Save(items[1], ".mp4")), 3.8, 4.3);
            Assert.InRange(Duration(await Save(items[3], ".mp4")), 6.7, 7.4);
            Assert.Equal(3, items[3].GetProperty("partsTotal").GetInt32()); // two segments, then joining

            // Separate files: a .zip with one file per segment, in play order
            using (var zip = ZipFile.OpenRead(await Save(items[2], ".zip")))
                Assert.Equal(["01 B.mp4", "02 Segment 2.mp4"], zip.Entries.Select(x => x.FullName));

            // The cutter saw each kept part once per item that needed it (b's two parts, then a)
            var cuts = _cutter.Cuts.Where(x => x.VodId == 451).Select(x => (x.StartTime.TotalSeconds, x.EndTime!.Value.TotalSeconds)).ToList();
            Assert.Equal(3, cuts.Count(x => x == (20, 22)));
            Assert.All(_cutter.Cuts.Where(x => x.VodId == 451 && x.StartTime.TotalSeconds == 100), x => Assert.Equal("720p30", x.Quality));
        }
    }
}
