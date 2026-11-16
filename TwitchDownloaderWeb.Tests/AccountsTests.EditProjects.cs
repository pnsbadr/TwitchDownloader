using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Projects;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Saved edits turned into projects at startup (badrfly, 2026-11-16).</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task SavedEditsBecomeProjectsOnce()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ep1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();

            var segment = await SegmentIdAsync(owner, 480, 10, 30);
            await PostJsonAsync(owner, "/api/edits", new { markerId = segment, title = "Tight", cuts = new[] { new { start = 10, end = 15 }, new { start = 20, end = 22 } } },
                HttpStatusCode.Created);
            var a = await SegmentIdAsync(owner, 480, 100, 110);
            var b = await SegmentIdAsync(owner, 480, 200, 210);
            var videoId = (await PostJsonAsync(owner, "/api/vods/480/videos", new { title = "Pair", markerIds = new[] { a, b } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();
            await PostJsonAsync(owner, "/api/edits", new { videoId, parts = new[] { new { markerId = a, cuts = new[] { new { start = 100, end = 110 } } } } },
                HttpStatusCode.Created);
            var clipId = AddStoredClip(teamId, ownerId, 480, 300, 320, "stored");
            // A clip edit with a split and a two-part vertical layout (file seconds; the file starts at 300)
            await PostJsonAsync(owner, "/api/edits", new
            {
                entryId = clipId, title = "Vertical", cuts = new[] { new { start = 318, end = 320 } },
                layout = new
                {
                    mode = "single", top = new { x = 0, y = 0, width = 0.3, height = 1 },
                    parts = new object[]
                    {
                        new { start = 0, end = 5, mode = "single", top = new { x = 0.1, y = 0, width = 0.3, height = 1 } },
                        new { start = 5, end = 20, mode = "split", top = new { x = 0, y = 0, width = 0.5, height = 0.5 }, bottom = new { x = 0.5, y = 0.5, width = 0.5, height = 0.5 }, splitRatio = 0.4,
                            keys = new[] { new { time = 6, top = new { x = 0, y = 0, width = 0.5, height = 0.5 }, bottom = new { x = 0.5, y = 0.5, width = 0.5, height = 0.5 } } } },
                    },
                    splits = new Dictionary<string, double[]> { ["c"] = [305] },
                },
            }, HttpStatusCode.Created);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                Assert.Equal(3, await SavedEditConversion.RunAsync(db, NullLogger.Instance, ct));
                // Once only
                Assert.Equal(0, await SavedEditConversion.RunAsync(db, NullLogger.Instance, ct));
            }

            var projects = (await GetJsonAsync(owner, "/api/projects")).EnumerateArray().ToList();
            JsonElement Full(string title) =>
                GetJsonAsync(owner, $"/api/projects/{projects.Single(x => x.GetProperty("title").GetString() == title).GetProperty("id").GetInt32()}").Result;
            static (double At, double Start, double End)[] Pieces(JsonElement project) => project.GetProperty("content").GetProperty("pieces").EnumerateArray()
                .Select(x => (x.GetProperty("at").GetDouble(), x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble())).ToArray();

            var seg = Full("Segment · Tight");
            Assert.Equal([(0.0, 15.0, 20.0), (5.0, 22.0, 30.0)], Pieces(seg));
            Assert.Equal(ownerId, seg.GetProperty("userId").GetInt32());
            Assert.Equal("segment", seg.GetProperty("content").GetProperty("pieces")[0].GetProperty("source").GetProperty("kind").GetString());

            // The video's first segment is cut out whole
            Assert.Equal([(0.0, 200.0, 210.0)], Pieces(Full("Pair (edit)")));

            // The clip: split at 305, the end cut; each piece takes the layout part under its middle, keys in VOD seconds
            var clip = Full("Stored clip · Vertical");
            Assert.Equal([(0.0, 300.0, 305.0), (5.0, 305.0, 318.0)], Pieces(clip));
            var layouts = clip.GetProperty("content").GetProperty("pieces").EnumerateArray().Select(x => x.GetProperty("layout")).ToArray();
            Assert.Equal("single", layouts[0].GetProperty("mode").GetString());
            Assert.Equal(0.1, layouts[0].GetProperty("top").GetProperty("x").GetDouble());
            Assert.Equal("split", layouts[1].GetProperty("mode").GetString());
            Assert.Equal(306, layouts[1].GetProperty("keys")[0].GetProperty("time").GetDouble());
        }
    }
}
