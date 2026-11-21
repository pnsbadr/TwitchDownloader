using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Playback;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>The library's Details view and calendar (badrfly 2026-11-21): sort by column, several members, day counts, who edited.</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task ClipListsSortByColumnFilterBySeveralMembersAndCountDays()
        {
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("dt1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, clipper);

            // dt1owner: "banana" (20 s, Nov 3) and "apple" (40 s, Nov 3); dt1member: "cherry" (10 s, Nov 2) and "date" (30 s, Nov 1)
            var banana = AddEntry(teamId, ownerId, "banana", vodId: 700);
            var apple = AddEntry(teamId, ownerId, "Apple", vodId: 701);
            var cherry = AddEntry(teamId, memberId, "cherry", vodId: 702);
            var date = AddEntry(teamId, memberId, "date", vodId: 703);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                void Set(int id, double seconds, DateTime added)
                {
                    var entry = db.Entries.Single(x => x.Id == id);
                    entry.ClipLengthSeconds = seconds;
                    entry.AddedAtUtc = added;
                    entry.StreamDateUtc = added.AddDays(-1);
                }
                Set(banana, 20, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc));
                Set(apple, 40, new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
                Set(cherry, 10, new DateTime(2026, 11, 2, 23, 30, 0, DateTimeKind.Utc));
                Set(date, 30, new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
                db.SaveChanges();
            }

            async Task<List<int>> AllPages(string url)
            {
                var seen = new List<int>();
                string? cursor = null;
                for (var pages = 0; pages < 10; pages++)
                {
                    var page = await GetJsonAsync(owner, url + (cursor is null ? "" : $"&cursor={cursor}"));
                    seen.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
                    cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
                    if (cursor is null)
                        break;
                }
                return seen;
            }

            // Name (case doesn't matter), length and maker, a page at a time
            Assert.Equal([apple, banana, cherry, date], await AllPages("/api/entries?paged=true&limit=3&sort=title&order=asc"));
            Assert.Equal([date, cherry, banana, apple], await AllPages("/api/entries?paged=true&limit=3&sort=title"));
            Assert.Equal([apple, date, banana, cherry], await AllPages("/api/entries?paged=true&limit=2&sort=length"));
            // dt1member before dt1owner; each maker newest first
            Assert.Equal([cherry, date, apple, banana], await AllPages("/api/entries?paged=true&limit=3&sort=madeBy&order=asc"));
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/entries?paged=true&sort=nope", TestContext.Current.CancellationToken)).StatusCode);

            // Several members at once
            Assert.Equal(4, (await GetJsonAsync(owner, $"/api/entries?paged=true&madeBy={ownerId},{memberId}")).GetProperty("total").GetInt32());
            Assert.Equal(2, (await GetJsonAsync(owner, $"/api/entries?paged=true&madeBy={memberId}")).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/entries?madeBy=a,b", TestContext.Current.CancellationToken)).StatusCode);

            // Who edited: the member's edit of banana shows on its line, and filters
            await PostJsonAsync(member, "/api/projects", new { entryId = banana, content = TwoTracks(Piece("a", "t1", 0, 700, 10, 20)) }, HttpStatusCode.Created);
            var page = await GetJsonAsync(owner, $"/api/entries?paged=true&editedBy={memberId},{ownerId}");
            var line = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal(banana, line.GetProperty("id").GetInt32());
            Assert.Equal(["dt1member"], line.GetProperty("editedBy").EnumerateArray().Select(x => x.GetString()!).ToArray());

            // Days in the viewer's time zone, without the day picked (Los Angeles: cherry's 23:30 UTC is still Nov 2, banana's 10:00 is Nov 3 02:00)
            var counted = await GetJsonAsync(owner, "/api/entries?paged=true&limit=1&days=America/Los_Angeles&from=2026-11-03T08:00:00Z&to=2026-11-04T08:00:00Z");
            Assert.Equal(2, counted.GetProperty("total").GetInt32());
            var days = counted.GetProperty("days").EnumerateArray().Select(x => (x.GetProperty("day").GetString(), x.GetProperty("count").GetInt32())).ToArray();
            Assert.Equal([("2026-11-03", 2), ("2026-11-02", 1), ("2026-11-01", 1)], days);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/entries?paged=true&days=Not/AZone", TestContext.Current.CancellationToken)).StatusCode);

            // Videos: by name, by several makers, with day counts
            var s1 = await SegmentIdAsync(owner, 700, 100, 110);
            var s2 = await SegmentIdAsync(member, 700, 200, 230);
            var zebra = (await PostJsonAsync(owner, "/api/vods/700/videos", new { title = "zebra", markerIds = new[] { s1 } }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
            var ant = (await PostJsonAsync(member, "/api/vods/700/videos", new { title = "Ant", markerIds = new[] { s2 } }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
            Assert.Equal([ant, zebra], await AllPages("/api/videos?paged=true&limit=1&sort=title&order=asc"));
            Assert.Equal([ant, zebra], await AllPages("/api/videos?paged=true&limit=1&sort=length"));
            Assert.Equal(2, (await GetJsonAsync(owner, $"/api/videos?paged=true&userId={ownerId},{memberId}")).GetProperty("total").GetInt32());
            var videoDays = await GetJsonAsync(owner, "/api/videos?paged=true&days=UTC");
            Assert.Equal(2, videoDays.GetProperty("days").EnumerateArray().Sum(x => x.GetProperty("count").GetInt32()));
        }

        [Theory]
        [InlineData("https://d1m7jfoe9zdc1j.cloudfront.net/a1b2c3d4e5f6a7b8c9d0_arky_41234567890_1700000000/chunked/highlight-2000000000.m3u8", 1700000000L)]
        [InlineData("https://d2nvs31859zcd8.cloudfront.net/a1b2c3d4e5f6a7b8c9d0_some_login_41234567890_1700000123/720p60/index-dvr.m3u8", 1700000123L)]
        [InlineData("https://example.com/plain/folder/index.m3u8", null)]
        [InlineData("not a url", null)]
        public void HighlightsKnowTheirBroadcastFromThePlaylistFolder(string url, long? seconds)
        {
            var start = HighlightSources.StartFromPlaylistUrl(url);
            Assert.Equal(seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime, start);
        }
    }
}
