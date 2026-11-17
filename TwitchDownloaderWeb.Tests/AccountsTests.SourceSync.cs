using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Projects;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Player 1 changes flow into Player 2 edits (badrfly 2026-11-17, ProjectSourceSync).</summary>
    public sealed partial class AccountsTests
    {
        private static object SyncPiece(string id, double at, long vod, double start, double end, string kind, int sourceId) =>
            new { id, trackId = "t1", at, vodId = vod, start, end, source = new { kind, id = sourceId } };

        private static async Task<(double At, double Start, double End)[]> ProjectPiecesAsync(HttpClient client, int projectId) =>
            (await GetJsonAsync(client, $"/api/projects/{projectId}")).GetProperty("content").GetProperty("pieces").EnumerateArray()
                .Select(x => (x.GetProperty("at").GetDouble(), x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble()))
                .OrderBy(x => x.Item1).ToArray();

        private static async Task<HttpResponseMessage> PutMarkerAsync(HttpClient client, int id, double start, double end, object[] cuts)
        {
            var response = await client.PutAsJsonAsync($"/api/markers/{id}", new { seconds = start, endSeconds = end, cuts }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response;
        }

        [Fact]
        public async Task EditsFollowTrimsAndCutsMadeOnPlayer1()
        {
            var (owner, _, _, _) = await TeamWithMemberAsync("ss1");
            var segment = await SegmentIdAsync(owner, 481, 10, 70);
            var other = await SegmentIdAsync(owner, 481, 300, 320);

            // Two pieces of the segment with a gap the member left, then a piece of something else
            var project = (await PostJsonAsync(owner, "/api/projects", new
            {
                title = "Edit",
                content = new
                {
                    tracks = new[] { new { id = "t1" } },
                    pieces = new[]
                    {
                        SyncPiece("a", 0, 481, 10, 30, "segment", segment),
                        SyncPiece("b", 25, 481, 40, 70, "segment", segment),
                        SyncPiece("c", 60, 481, 300, 310, "segment", other),
                    },
                },
            }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
            (double, double, double)[] start = [(0, 10, 30), (25, 40, 70), (60, 300, 310)];
            Assert.Equal(start, await ProjectPiecesAsync(owner, project));

            // Trimmed shorter on Player 1: the end goes from the piece, and what comes after slides left; the page hears about it
            var response = await PutMarkerAsync(owner, segment, 10, 60, []);
            Assert.True(response.Headers.TryGetValues(ProjectSourceSync.HeaderName, out var header));
            var change = JsonDocument.Parse(header.Single()).RootElement[0];
            Assert.Equal(project, change.GetProperty("projects")[0].GetProperty("id").GetInt32());
            Assert.Equal(60, change.GetProperty("rules")[0].GetProperty("removed")[0].GetProperty("start").GetDouble());
            Assert.Equal([(0, 10, 30), (25, 40, 60), (50, 300, 310)], await ProjectPiecesAsync(owner, project));

            // A part cut out: the piece is split around it, the rest slides left
            await PutMarkerAsync(owner, segment, 10, 60, [new { start = 20, end = 25 }]);
            Assert.Equal([(0, 10, 20), (10, 25, 30), (20, 40, 60), (45, 300, 310)], await ProjectPiecesAsync(owner, project));

            // Undone on Player 1: the two halves are one piece again
            await PutMarkerAsync(owner, segment, 10, 60, []);
            Assert.Equal([(0, 10, 30), (25, 40, 60), (50, 300, 310)], await ProjectPiecesAsync(owner, project));

            // The trim undone: the piece that ended there grows back
            await PutMarkerAsync(owner, segment, 10, 70, []);
            Assert.Equal(start, await ProjectPiecesAsync(owner, project));

            // Cutting out what the gap left out changes nothing in the edit, and taking it back doesn't fill the gap
            var none = await PutMarkerAsync(owner, segment, 10, 70, [new { start = 30, end = 40 }]);
            // (the page still hears the rule: a loaded original follows it too)
            Assert.Empty(JsonDocument.Parse(none.Headers.GetValues(ProjectSourceSync.HeaderName).Single()).RootElement[0].GetProperty("projects").EnumerateArray());
            await PutMarkerAsync(owner, segment, 10, 70, []);
            Assert.Equal(start, await ProjectPiecesAsync(owner, project));

            // Moved somewhere else entirely: the edit is left alone
            await PutMarkerAsync(owner, segment, 500, 560, []);
            Assert.Equal(start, await ProjectPiecesAsync(owner, project));
        }

        [Fact]
        public async Task ClipAndVideoEditsFollowToo()
        {
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ss2");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var clipId = AddStoredClip(teamId, ownerId, 482, 100, 160, "stored");
            var a = await SegmentIdAsync(owner, 482, 10, 20);
            var b = await SegmentIdAsync(owner, 482, 40, 50);
            var videoId = (await PostJsonAsync(owner, "/api/vods/482/videos", new { title = "Pair", markerIds = new[] { a, b } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();

            var clipEdit = (await PostJsonAsync(owner, "/api/projects", new
            {
                entryId = clipId,
                content = new { tracks = new[] { new { id = "t1" } }, pieces = new[] { SyncPiece("a", 0, 482, 100, 160, "clip", clipId) } },
            }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
            var videoEdit = (await PostJsonAsync(owner, "/api/projects", new
            {
                videoId,
                content = new
                {
                    tracks = new[] { new { id = "t1" } },
                    pieces = new[] { SyncPiece("a", 0, 482, 10, 20, "video", videoId), SyncPiece("b", 10, 482, 40, 50, "video", videoId) },
                },
            }, HttpStatusCode.Created)).GetProperty("id").GetInt32();

            // The clip's start trimmed on Player 1
            var trimmed = await owner.PutAsJsonAsync($"/api/entries/{clipId}/trim", new { startTime = "110", endTime = "160" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, trimmed.StatusCode);
            Assert.Equal([(0.0, 110.0, 160.0)], await ProjectPiecesAsync(owner, clipEdit));

            // The video's second segment made shorter: only its piece changes
            await PutMarkerAsync(owner, b, 40, 45, []);
            Assert.Equal([(0.0, 10.0, 20.0), (10.0, 40.0, 45.0)], await ProjectPiecesAsync(owner, videoEdit));
        }

        [Fact]
        public void SyncRulesLeaveOtherSourcesAndRowsAlone()
        {
            var content = new ProjectContent(
                [new ProjectTrack("t1", null, false, false, false), new ProjectTrack("t2", null, false, false, false)],
                [
                    new ProjectPiece("a", "t1", 0, 1, 0, 10, new ProjectSource("clip", 5)),
                    new ProjectPiece("b", "t1", 10, 1, 50, 60, new ProjectSource("clip", 6)),
                    new ProjectPiece("c", "t2", 10, 1, 0, 10, new ProjectSource("clip", 5)),
                ]);
            var rule = ProjectSourceSync.RuleFor("clip", 5, 1, [new(0, 10)], [new(4, 10)])!;
            var result = ProjectSourceSync.Apply(content, rule)!;
            var pieces = result.Pieces!.ToDictionary(x => x.Id);
            Assert.Equal((0.0, 4.0, 10.0), (pieces["a"].At, pieces["a"].Start, pieces["a"].End));
            Assert.Equal(6, pieces["b"].At); // same row: slides left
            Assert.Equal((10.0, 4.0), (pieces["c"].At, pieces["c"].Start)); // its own row: only itself changes
            Assert.Null(ProjectSourceSync.Apply(content, ProjectSourceSync.RuleFor("clip", 7, 1, [new(0, 10)], [new(4, 10)])!));
        }
    }
}
