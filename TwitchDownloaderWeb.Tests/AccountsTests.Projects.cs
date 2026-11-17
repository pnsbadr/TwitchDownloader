using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        public async Task ProjectsSaveTracksAndPiecesOfTheirVod()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("pj1");
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, clipper);

            // A new project has one empty row (a vertical edit: only those keep boxes)
            var created = await member.PostAsJsonAsync("/api/projects", new { title = "Best of the week", vertical = true }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var project = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
            var id = project.GetProperty("id").GetInt32();
            Assert.Equal(1, project.GetProperty("revision").GetInt32());
            Assert.Single(project.GetProperty("content").GetProperty("tracks").EnumerateArray());
            Assert.Equal("pj1member", project.GetProperty("createdBy").GetString());
            Assert.True(project.GetProperty("vertical").GetBoolean());

            // Pieces on two rows, with a moving layout
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
            var content = TwoTracks(Piece("a", "t1", 0, 500, 100, 110, layout), Piece("b", "t1", 12, 500, 40, 45.5), Piece("c", "t2", 2, 500, 200, 230));
            var saved = await member.PutAsJsonAsync($"/api/projects/{id}", new { content, revision = 1 }, ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var after = await saved.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(2, after.GetProperty("revision").GetInt32());
            Assert.Equal(32, after.GetProperty("lengthSeconds").GetDouble()); // piece c ends at 2 + 30
            Assert.Equal(3, after.GetProperty("pieceCount").GetInt32());
            Assert.Equal([500L], after.GetProperty("vodIds").EnumerateArray().Select(x => x.GetInt64()));
            Assert.Equal(500, after.GetProperty("vodId").GetInt64()); // took its first piece's VOD

            // Everything in an edit comes from its VOD (2026-11-17)
            var otherVod = await member.PutAsJsonAsync($"/api/projects/{id}",
                new { content = TwoTracks(Piece("a", "t1", 0, 500, 100, 110), Piece("z", "t2", 0, 501, 0, 10)) }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, otherVod.StatusCode);
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
            Assert.Single((await GetJsonAsync(owner, "/api/projects?vodId=500")).EnumerateArray());
            Assert.Single((await GetJsonAsync(owner, "/api/projects?vodIds=500,501")).EnumerateArray());
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

            // Others' projects are vertical edits: without a role that shows them they aren't there (see AccountsTests.VerticalAccess)
            var memberSees = await GetJsonAsync(member, $"/api/projects/{id}");
            Assert.True(memberSees.GetProperty("canEdit").GetBoolean());
            var ownersProject = (await (await owner.PostAsJsonAsync("/api/projects", new { title = "Owner's" }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct))
                .GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/projects/{ownersProject}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsJsonAsync($"/api/projects/{ownersProject}", new { title = "Mine" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await member.DeleteAsync($"/api/projects/{ownersProject}", ct)).StatusCode);
            var copy = await owner.PostAsJsonAsync($"/api/projects/{id}/copy", new { }, ct);
            Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
            var copied = await copy.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.True(copied.GetProperty("vertical").GetBoolean()); // a copy keeps the kind
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
                new { content = TwoTracks(Piece("a", "t1", 0, 510, 0, 10), Piece("b", "t2", 0, 510, 20, 30)) }, ct);
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

        [Fact]
        public async Task ProjectsExportAsOneFileLandscapeAndVertical()
        {
            if (!HasFfmpeg())
                Assert.Skip("ffmpeg not found; set FFMPEG_PATH to run the project export test.");
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("pj3");

            // A 1 s gap, then a piece of VOD 520 under a piece of VOD 521 on the top row (a project from before 2026-11-17: those could mix VODs)
            var content = TwoTracks(Piece("top", "t1", 2, 521, 30, 31.5), Piece("under", "t2", 1, 520, 200, 203.5));
            var mixed = TwoTracks(Piece("top", "t1", 2, 520, 30, 31.5), Piece("under", "t2", 1, 520, 200, 203.5));
            var oldId = (await (await owner.PostAsJsonAsync("/api/projects", new { title = "Mix", content = mixed }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct))
                .GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var row = await db.Projects.IgnoreQueryFilters().Include(x => x.Vods).SingleAsync(x => x.Id == oldId, ct);
                row.Content = ProjectContent.Read(System.Text.Json.JsonSerializer.Serialize(content)).Write();
                row.Vods.Add(new ProjectVod { VodId = 521 });
                await db.SaveChangesAsync(ct);
            }
            var id = oldId;
            // It keeps both VODs when saved again
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/projects/{id}", new { content }, ct)).StatusCode);

            // The edit's kind decides the shape, whatever the checkboxes say: a horizontal edit exports landscape...
            var response = await owner.PostAsJsonAsync("/api/exports",
                new { items = new[] { new { kind = "project", projectId = id, horizontal = true, vertical = true, quality = "480p30" } } }, ct);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var batch = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(["Project"], batch.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("kind").GetString()));
            Assert.Equal(4.5, batch.GetProperty("items")[0].GetProperty("lengthSeconds").GetDouble());
            var done = await WaitForExportAsync(owner, batch.GetProperty("id").GetInt32());
            Assert.Equal("Ready", done.GetProperty("status").GetString());
            var landscape = done.GetProperty("items")[0];
            Assert.Equal(4, landscape.GetProperty("partsTotal").GetInt32()); // gap, under, top, under again

            // ... and a vertical one 9:16
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var row = await db.Projects.IgnoreQueryFilters().SingleAsync(x => x.Id == id, ct);
                row.Vertical = true;
                await db.SaveChangesAsync(ct);
            }
            response = await owner.PostAsJsonAsync("/api/exports",
                new { items = new[] { new { kind = "project", projectId = id, horizontal = true, vertical = false, quality = "480p30" } } }, ct);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            batch = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(["ProjectVertical"], batch.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("kind").GetString()));
            done = await WaitForExportAsync(owner, batch.GetProperty("id").GetInt32());
            Assert.Equal("Ready", done.GetProperty("status").GetString());
            var items = new[] { landscape, done.GetProperty("items")[0] };
            Assert.Equal(id, items[1].GetProperty("projectId").GetInt32());

            // The VOD ranges cut: the lower piece around the top one
            Assert.Contains(_cutter.Cuts, x => x.VodId == 520 && x.StartTime.TotalSeconds == 200 && x.EndTime!.Value.TotalSeconds == 201);
            Assert.Contains(_cutter.Cuts, x => x.VodId == 521 && x.StartTime.TotalSeconds == 30 && x.EndTime!.Value.TotalSeconds == 31.5);
            Assert.Contains(_cutter.Cuts, x => x.VodId == 520 && x.StartTime.TotalSeconds == 202.5 && x.EndTime!.Value.TotalSeconds == 203.5);

            foreach (var (item, size) in new[] { (items[0], "854x480"), (items[1], "1080x1920") })
            {
                var file = await owner.GetAsync(item.GetProperty("fileUrl").GetString(), ct);
                Assert.Equal(HttpStatusCode.OK, file.StatusCode);
                var path = Path.Combine(_dir, $"project-{size}.mp4");
                await File.WriteAllBytesAsync(path, await file.Content.ReadAsByteArrayAsync(ct), ct);
                var (streams, seconds) = await ProbeAsync(path);
                Assert.Contains(size, streams);
                Assert.Contains("Audio", streams);
                Assert.InRange(seconds, 4.3, 4.8);
            }
        }

        private static bool HasLayout(JsonElement project) =>
            project.GetProperty("content").GetProperty("pieces")[0].TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object;

        [Fact]
        public async Task HorizontalEditsKeepNoBoxesAndVerticalOnesAreSeparate()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("pjv");
            var layout = new { mode = "single", top = new { x = 0.2, y = 0, width = 0.316, height = 1 } };

            // A horizontal edit (the default) drops boxes when it's saved
            var horizontal = await (await owner.PostAsJsonAsync("/api/projects",
                new { title = "Clip · Edit 1", content = TwoTracks(Piece("a", "t1", 0, 530, 100, 110, layout)) }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.False(horizontal.GetProperty("vertical").GetBoolean());
            Assert.False(HasLayout(horizontal));
            var id = horizontal.GetProperty("id").GetInt32();
            var saved = await (await owner.PutAsJsonAsync($"/api/projects/{id}",
                new { content = TwoTracks(Piece("a", "t1", 0, 530, 100, 108, layout)) }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.False(HasLayout(saved));

            // A vertical edit of the same VOD keeps them; the kind can't be changed by a save
            var vertical = await (await owner.PostAsJsonAsync("/api/projects",
                new { title = "Clip · Vertical 1", vertical = true, content = TwoTracks(Piece("a", "t1", 0, 530, 100, 110, layout)) }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.True(vertical.GetProperty("vertical").GetBoolean());
            Assert.True(HasLayout(vertical));
            var verticalId = vertical.GetProperty("id").GetInt32();
            var again = await (await owner.PutAsJsonAsync($"/api/projects/{verticalId}", new { title = "Renamed", vertical = false }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.True(again.GetProperty("vertical").GetBoolean());

            // Lists say which is which and filter on it
            Assert.Equal([verticalId], (await GetJsonAsync(owner, "/api/projects?vodId=530&vertical=true")).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
            Assert.Equal([id], (await GetJsonAsync(owner, "/api/projects?vodId=530&vertical=false")).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
            Assert.Equal(2, (await GetJsonAsync(owner, "/api/projects?vodId=530")).GetArrayLength());
        }

        /// <summary>The stream lines and duration ffmpeg prints for a file.</summary>
        private static async Task<(string Streams, double Seconds)> ProbeAsync(string path)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(FakeCutter.Ffmpeg)
            {
                ArgumentList = { "-hide_banner", "-i", path },
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var streams = string.Join("\n", output.Split('\n').Where(x => x.Contains("Stream #")));
            var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration: (\d+):(\d+):(\d+(\.\d+)?)");
            var seconds = match.Success
                ? int.Parse(match.Groups[1].Value) * 3600 + int.Parse(match.Groups[2].Value) * 60
                  + double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            return (streams, seconds);
        }
    }
}
