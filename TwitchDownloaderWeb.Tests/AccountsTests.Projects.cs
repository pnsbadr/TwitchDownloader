using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Projects: the multi-track Player 2 timeline (badrfly, 2026-11-16).</summary>
    public sealed partial class AccountsTests
    {
        private static object Piece(string id, string track, double at, long vod, double start, double end, object? layout = null) =>
            new { id, trackId = track, at, vodId = vod, start, end, source = new { kind = "clip", id = 1 }, layout };

        private static object TwoTracks(params object[] pieces) => new
        {
            tracks = new object[] { new { id = "t1", name = "Top" }, new { id = "t2", muted = true } },
            pieces,
        };

        [Fact]
        public async Task ProjectsSaveTracksAndPiecesFromSeveralVods()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("pj1");
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, clipper);

            // A new project has one empty row
            var created = await member.PostAsJsonAsync("/api/projects", new { title = "Best of the week" }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var project = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
            var id = project.GetProperty("id").GetInt32();
            Assert.Equal(1, project.GetProperty("revision").GetInt32());
            Assert.Single(project.GetProperty("content").GetProperty("tracks").EnumerateArray());
            Assert.Equal("pj1member", project.GetProperty("createdBy").GetString());

            // Pieces of two VODs on two rows, with a moving layout
            var layout = new
            {
                mode = "split", splitRatio = 0.4,
                top = new { x = 0.7, y = 0.05, width = 0.25, height = 0.3 }, bottom = new { x = 0.2, y = 0.1, width = 0.5, height = 0.8 },
                keys = new[]
                {
                    new { time = 105.0, top = new { x = 0.7, y = 0.05, width = 0.25, height = 0.3 }, bottom = new { x = 0.2, y = 0.1, width = 0.5, height = 0.8 } },
                    new { time = 108.0, top = new { x = 0.6, y = 0.05, width = 0.25, height = 0.3 }, bottom = new { x = 0.1, y = 0.1, width = 0.5, height = 0.8 } },
                },
            };
            var content = TwoTracks(Piece("a", "t1", 0, 500, 100, 110, layout), Piece("b", "t1", 12, 501, 40, 45.5), Piece("c", "t2", 2, 500, 200, 230));
            var saved = await member.PutAsJsonAsync($"/api/projects/{id}", new { content, revision = 1 }, ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var after = await saved.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(2, after.GetProperty("revision").GetInt32());
            Assert.Equal(32, after.GetProperty("lengthSeconds").GetDouble()); // piece c ends at 2 + 30
            Assert.Equal(3, after.GetProperty("pieceCount").GetInt32());
            Assert.Equal([500L, 501L], after.GetProperty("vodIds").EnumerateArray().Select(x => x.GetInt64()));
            var keys = after.GetProperty("content").GetProperty("pieces")[0].GetProperty("layout").GetProperty("keys");
            Assert.Equal(108, keys[1].GetProperty("time").GetDouble());
            Assert.True(after.GetProperty("content").GetProperty("tracks")[1].GetProperty("muted").GetBoolean());

            // An older copy of the page is told instead of overwriting
            var stale = await member.PutAsJsonAsync($"/api/projects/{id}", new { content = TwoTracks(), revision = 1 }, ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("project-changed", await CodeOf(stale));

            // Listed without content, filterable by VOD
            var list = await GetJsonAsync(owner, "/api/projects");
            Assert.Single(list.EnumerateArray());
            Assert.Equal(JsonValueKind.Null, list[0].GetProperty("content").ValueKind);
            Assert.Single((await GetJsonAsync(owner, "/api/projects?vodId=501")).EnumerateArray());
            Assert.Empty((await GetJsonAsync(owner, "/api/projects?vodId=502")).EnumerateArray());

            // Bad content is refused
            foreach (var bad in new object[]
                     {
                         new { tracks = Array.Empty<object>(), pieces = Array.Empty<object>() },
                         TwoTracks(Piece("a", "t1", 0, 500, 100, 110), Piece("b", "t1", 5, 500, 0, 10)), // overlap on one row
                         TwoTracks(Piece("a", "t9", 0, 500, 100, 110)), // no such row
                         TwoTracks(Piece("a", "t1", 0, 500, 100, 100.2)), // too short
                         TwoTracks(Piece("a", "t1", 0, 500, 100, 110), Piece("a", "t2", 0, 500, 100, 110)), // same ID
                         TwoTracks(Piece("a b", "t1", 0, 500, 100, 110)),
                         TwoTracks(Piece("a", "t1", 3 * 3600, 500, 100, 110)), // past 3 hours
                         TwoTracks(Piece("a", "t1", 0, 500, 100, 110, new { mode = "split", top = new { x = 0, y = 0, width = 0.5, height = 0.5 } })),
                     })
                Assert.Equal(HttpStatusCode.BadRequest, (await member.PutAsJsonAsync($"/api/projects/{id}", new { content = bad }, ct)).StatusCode);

            // Saves aren't audited, adding is
            var audit = await GetJsonAsync(owner, "/api/team/audit");
            Assert.Single(audit.EnumerateArray(), x => x.GetProperty("action").GetString() == "project.added");

            // Others: view only without EditAnyEntries; they make their own copy
            var memberSees = await GetJsonAsync(member, $"/api/projects/{id}");
            Assert.True(memberSees.GetProperty("canEdit").GetBoolean());
            var ownersProject = (await (await owner.PostAsJsonAsync("/api/projects", new { title = "Owner's" }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct))
                .GetProperty("id").GetInt32();
            Assert.False((await GetJsonAsync(member, $"/api/projects/{ownersProject}")).GetProperty("canEdit").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/projects/{ownersProject}", new { title = "Mine" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/projects/{ownersProject}", ct)).StatusCode);
            var copy = await owner.PostAsJsonAsync($"/api/projects/{id}/copy", new { }, ct);
            Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
            var copied = await copy.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("Copy of Best of the week", copied.GetProperty("title").GetString());
            Assert.Equal(id, copied.GetProperty("copiedFromProjectId").GetInt32());
            Assert.Equal(3, copied.GetProperty("content").GetProperty("pieces").GetArrayLength());

            // The team owner can change and delete anyone's
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/projects/{id}", new { title = "Renamed" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/projects/{id}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/projects/{id}", ct)).StatusCode);
        }

        [Fact]
        public async Task ProjectsOnlyTakeTheTeamsVodsAndHideWithARevokedChannel()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("pj2");
            var staff = await StaffClientAsync();
            _vods.Vods[510] = new VodDetails(510, "Ellsya stream", "Ellsya", 3600, DateTime.UtcNow.AddDays(-1), null, ChannelLogin: "ellsya");

            // Not the team's channel: refused
            var refused = await owner.PostAsJsonAsync("/api/projects", new { content = TwoTracks(Piece("a", "t1", 0, 510, 0, 10)) }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("channel-not-assigned", await CodeOf(refused));

            var added = await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct);
            var channelId = (await added.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var created = await owner.PostAsJsonAsync("/api/projects",
                new { content = TwoTracks(Piece("a", "t1", 0, 510, 0, 10), Piece("b", "t2", 0, 511, 0, 10)) }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var arkyOnly = (await (await owner.PostAsJsonAsync("/api/projects", new { content = TwoTracks(Piece("a", "t1", 0, 512, 0, 10)) }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            // Revoked: the project using an Ellsya VOD is hidden whole, the Arky one stays
            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/admin/teams/{teamId}/channels/{channelId}?mode=revoke", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/projects/{id}", ct)).StatusCode);
            Assert.Equal([arkyOnly], (await GetJsonAsync(owner, "/api/projects")).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));

            // Assigned again: back
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/projects/{id}", ct)).StatusCode);
        }

        [Fact]
        public void ProjectContentReadsBackWhatItWrites()
        {
            var content = new ProjectContent(
                [new ProjectTrack("t1", "Main", false, false, false)],
                [new ProjectPiece("p1", "t1", 1.5, 42, 10, 20, new ProjectSource("segment", 7), "Hype", "pink",
                    new ProjectLayout("single", new Services.Shortform.CropBox(0.1, 0.2, 0.3, 0.4), null, null))]);
            Assert.Null(content.Validate());
            var back = ProjectContent.Read(content.Write());
            Assert.Equal("Hype", back.Pieces![0].Label);
            Assert.Equal(0.3, back.Pieces[0].Layout!.Top!.Width);
            Assert.Equal(11.5, back.LengthSeconds);
            Assert.DoesNotContain("\"bottom\"", content.Write()); // nulls left out
            Assert.Single(ProjectContent.Read("not json").Tracks!);
        }
    }
}
