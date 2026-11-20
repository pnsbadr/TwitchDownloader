using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>The library pages through long lists (badrfly 2026-11-20, "Redesign the library for large teams").</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task ClipsAndVideosPageWithACursor()
        {
            var (owner, _, teamId, _) = await TeamWithMemberAsync("pg1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();

            // 7 clips; three share one time, so the cursor has to go by ID too
            var ids = new List<int>();
            for (var i = 0; i < 7; i++)
                ids.Add(AddEntry(teamId, ownerId, $"Clip {i}", vodId: 600 + i));
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
                foreach (var (entry, i) in db.Entries.Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToList().Select((e, i) => (e, i)))
                {
                    entry.AddedAtUtc = i < 3 ? start : start.AddHours(i);
                    entry.StreamDateUtc = start.AddDays(-i);
                }
                db.SaveChanges();
            }

            async Task<List<int>> AllPages(string url)
            {
                var seen = new List<int>();
                string? cursor = null;
                var pages = 0;
                do
                {
                    var page = await GetJsonAsync(owner, url + (cursor is null ? "" : $"&cursor={cursor}"));
                    Assert.Equal(7, page.GetProperty("total").GetInt32());
                    var items = page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
                    Assert.True(items.Count <= 3);
                    seen.AddRange(items);
                    cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
                    pages++;
                } while (cursor is not null && pages < 10);
                return seen;
            }

            // Newest added first; the three with the same time by ID, highest first
            Assert.Equal([ids[6], ids[5], ids[4], ids[3], ids[2], ids[1], ids[0]], await AllPages("/api/entries?paged=true&limit=3"));
            Assert.Equal([ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], ids[6]], await AllPages("/api/entries?paged=true&limit=3&order=asc"));
            Assert.Equal([ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], ids[6]], await AllPages("/api/entries?paged=true&limit=3&sort=streamDate"));
            // Without paged the answer is the plain list, as before
            Assert.Equal(JsonValueKind.Array, (await GetJsonAsync(owner, "/api/entries?limit=3")).ValueKind);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/entries?paged=true&cursor=nope", TestContext.Current.CancellationToken)).StatusCode);

            // Videos: newest first, searchable by title
            var segments = new List<int>();
            for (var i = 0; i < 4; i++)
                segments.Add(await SegmentIdAsync(owner, 610, 100 + i * 20, 110 + i * 20));
            var videos = new List<int>();
            foreach (var (segment, i) in segments.Select((s, i) => (s, i)))
                videos.Add((await PostJsonAsync(owner, "/api/vods/610/videos", new { title = i == 2 ? "Boxing story" : $"Video {i}", markerIds = new[] { segment } },
                    HttpStatusCode.Created)).GetProperty("id").GetInt32());
            var first = await GetJsonAsync(owner, "/api/videos?paged=true&limit=3");
            Assert.Equal(4, first.GetProperty("total").GetInt32());
            Assert.Equal([videos[3], videos[2], videos[1]], first.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray());
            var second = await GetJsonAsync(owner, $"/api/videos?paged=true&limit=3&cursor={first.GetProperty("nextCursor").GetString()}");
            Assert.Equal([videos[0]], second.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray());
            Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
            var found = await GetJsonAsync(owner, "/api/videos?paged=true&search=boxing");
            Assert.Equal([videos[2]], found.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray());
        }

        [Fact]
        public async Task SearchBoxFindsLabelsLengthsAndDates()
        {
            var (owner, _, teamId, _) = await TeamWithMemberAsync("sb1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var shortClip = AddEntry(teamId, ownerId, "Quick laugh", vodId: 700);
            var longClip = AddEntry(teamId, ownerId, "Long boxing round", vodId: 701);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                db.Entries.Single(x => x.Id == shortClip).ClipLengthSeconds = 20;
                db.Entries.Single(x => x.Id == longClip).ClipLengthSeconds = 95;
                db.SaveChanges();
            }
            List<int> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();

            // Length: ">1m" and "<30s"
            Assert.Equal([longClip], Ids(await GetJsonAsync(owner, "/api/entries?paged=true&minSeconds=60")));
            Assert.Equal([shortClip], Ids(await GetJsonAsync(owner, "/api/entries?paged=true&maxSeconds=30")));

            // A video is found by a segment's label, not only its title
            var labeled = await SegmentIdAsync(owner, 702, 10, 30);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/markers/{labeled}", new { seconds = 10, endSeconds = 30, label = "Instigator walks in" }, TestContext.Current.CancellationToken)).StatusCode);
            var moment = await SegmentIdAsync(owner, 703, 50, null);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/markers/{moment}", new { seconds = 50, label = "instigator again" }, TestContext.Current.CancellationToken)).StatusCode);
            var video = (await PostJsonAsync(owner, "/api/vods/702/videos", new { title = "Story", markerIds = new[] { labeled } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();
            Assert.Equal([video], Ids(await GetJsonAsync(owner, "/api/videos?paged=true&search=instigator")));

            // Dates on videos (when they were made)
            var later = DateTime.UtcNow.AddDays(1).ToString("O");
            Assert.Empty(Ids(await GetJsonAsync(owner, $"/api/videos?paged=true&from={Uri.EscapeDataString(later)}")));
            Assert.Equal([video], Ids(await GetJsonAsync(owner, $"/api/videos?paged=true&to={Uri.EscapeDataString(later)}")));
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/videos?from=2026-11-01", TestContext.Current.CancellationToken)).StatusCode);

            // What matches across the team's work, and on which VODs
            var matches = await GetJsonAsync(owner, "/api/vods/matches?q=instigator");
            Assert.Equal(1, matches.GetProperty("segments").GetInt32());
            Assert.Equal(1, matches.GetProperty("moments").GetInt32());
            Assert.Equal(0, matches.GetProperty("clips").GetInt32());
            Assert.Equal([702L, 703L], matches.GetProperty("vodIds").EnumerateArray().Select(x => x.GetInt64()).Order().ToArray());
            var boxing = await GetJsonAsync(owner, "/api/vods/matches?q=boxing");
            Assert.Equal(1, boxing.GetProperty("clips").GetInt32());
            Assert.Equal([701L], boxing.GetProperty("vodIds").EnumerateArray().Select(x => x.GetInt64()).ToArray());
        }
    }
}
