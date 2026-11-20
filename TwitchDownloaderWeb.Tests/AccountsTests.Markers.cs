using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task MarkersAreSharedInTheTeamAndOnlyTheirs()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("m1");
            AddEntry(teamId, memberId, "Clip on VOD 1");
            var adder = await CreateRoleAsync(owner, "Adder", "ViewLibrary", "AddEntries");
            Assert.Equal(HttpStatusCode.NoContent, await AssignAsync(owner, memberId, adder));

            var added = await member.PostAsJsonAsync("/api/vods/1/markers", new { seconds = 3725.456, label = "Funny bit", color = "pink" }, ct);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            var mine = (await added.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var ownerMarker = (await (await owner.PostAsJsonAsync("/api/vods/1/markers", new { seconds = 10, label = "" }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/1/markers", new { seconds = -1 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/1/markers", new { seconds = 5, color = "red; x" }, ct)).StatusCode);

            // The whole team sees both, with who made them and the team's clips on the VOD
            var timeline = await GetJsonAsync(member, "/api/vods/1/timeline");
            var markers = timeline.GetProperty("markers").EnumerateArray().ToList();
            Assert.Equal([10, 3725.46], markers.Select(x => x.GetProperty("seconds").GetDouble()));
            Assert.Equal("m1member", markers[1].GetProperty("createdBy").GetString());
            Assert.Equal([false, true], markers.Select(x => x.GetProperty("canDelete").GetBoolean()));
            Assert.Equal(["Clip on VOD 1"], timeline.GetProperty("clips").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
            // Moving a clip's edges on the player needs EditOwn/AnyEntries (the role has neither); the owner may
            Assert.False(timeline.GetProperty("clips")[0].GetProperty("canEdit").GetBoolean());
            Assert.True((await GetJsonAsync(owner, "/api/vods/1/timeline")).GetProperty("clips")[0].GetProperty("canEdit").GetBoolean());
            // The clip tool's limit comes with the timeline
            Assert.Equal(180, timeline.GetProperty("maxStoredClipSeconds").GetInt32());
            // The cards' strips list the team's entries on each VOD
            var ranges = await GetJsonAsync(member, "/api/timeline/entries?vodIds=1,2");
            Assert.Equal(["Clip on VOD 1"], ranges.EnumerateArray().Select(x => x.GetProperty("title").GetString()));
            Assert.Equal(1, ranges[0].GetProperty("vodId").GetInt64());
            Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync("/api/timeline/entries?vodIds=x", ct)).StatusCode);

            // Another team sees none of it (the Main team has its own entry on VOD 1)
            var staff = await StaffClientAsync();
            var registration = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration", channel = "arky" });
            var other = NewClient();
            await AcceptAsync(other, registration, "m1other", newAccount: true, teamName: "Other team");
            var otherTimeline = await GetJsonAsync(other, "/api/vods/1/timeline");
            Assert.Equal(0, otherTimeline.GetProperty("markers").GetArrayLength());
            Assert.Equal(0, otherTimeline.GetProperty("clips").GetArrayLength());
            Assert.Equal(0, (await GetJsonAsync(other, "/api/timeline/entries?vodIds=1")).GetArrayLength());
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/markers/{mine}", ct)).StatusCode);

            // Own markers yes, others' no
            Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/markers/{ownerMarker}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/markers/{mine}", ct)).StatusCode);

            // Without AddEntries no new markers
            var viewer = await CreateRoleAsync(owner, "Viewer", "ViewLibrary");
            await AssignAsync(owner, memberId, viewer);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/vods/1/markers", new { seconds = 1 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{ownerMarker}", ct)).StatusCode);
        }

        [Fact]
        public async Task SegmentsHaveAnEndAndAreListedForSeveralVods()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("m2");

            var segment = await owner.PostAsJsonAsync("/api/vods/7/markers", new { seconds = 60, endSeconds = 95.555, label = "Intro", color = "yellow" }, ct);
            Assert.Equal(HttpStatusCode.Created, segment.StatusCode);
            Assert.Equal(95.56, (await segment.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("endSeconds").GetDouble());
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/vods/8/markers", new { seconds = 5, label = "Moment" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/vods/9/markers", new { seconds = 5 }, ct)).StatusCode);

            // The end must come after the start
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/vods/7/markers", new { seconds = 60, endSeconds = 60.2 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/vods/7/markers", new { seconds = 60, endSeconds = 30 }, ct)).StatusCode);

            var listed = (await GetJsonAsync(owner, "/api/markers?vodIds=7,8,10")).EnumerateArray().ToList();
            Assert.Equal([7L, 8L], listed.Select(x => x.GetProperty("vodId").GetInt64()));
            Assert.Equal(JsonValueKind.Null, listed[1].GetProperty("endSeconds").ValueKind);
            Assert.Equal("Intro", listed[0].GetProperty("label").GetString());

            Assert.Equal(0, (await GetJsonAsync(owner, "/api/markers")).GetArrayLength());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/markers?vodIds=7,abc", ct)).StatusCode);
        }
    
        [Fact]
        public async Task MarkersOnALiveStreamMakeNoEntry()
        {
            // The live markers entry was removed (badrfly 2026-11-20): markers never make or move a library entry
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("m3");
            var streamStart = new DateTime(2026, 11, 10, 20, 9, 0, DateTimeKind.Utc);
            _vods.Vods[500] = new VodDetails(500, "Live grind", "Subroza", 1600, streamStart, null, IsLive: true);

            var moment = await owner.PostAsJsonAsync("/api/vods/500/markers", new { seconds = 600, label = "round 9" }, ct);
            Assert.Equal(HttpStatusCode.Created, moment.StatusCode);
            Assert.Equal(JsonValueKind.Null, (await moment.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("liveEntry").ValueKind);
            var segment = await owner.PostAsJsonAsync("/api/vods/500/markers", new { seconds = 1500, endSeconds = 1590 }, ct);
            Assert.Equal(HttpStatusCode.Created, segment.StatusCode);
            var segmentId = (await segment.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{segmentId}", ct)).StatusCode);
            Assert.Equal(0, (await GetJsonAsync(owner, "/api/entries")).GetArrayLength());
        }
    
        [Fact]
        public async Task MarkersCanBeChangedByTheirMakerOrWithEditAny()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("m4");
            var adder = await CreateRoleAsync(owner, "Adder", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, adder);

            async Task<int> AddAsync(HttpClient client, object body) =>
                (await (await client.PostAsJsonAsync("/api/vods/40/markers", body, ct)).Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var mine = await AddAsync(member, new { seconds = 10, endSeconds = 20, label = "Intro" });
            var theirs = await AddAsync(owner, new { seconds = 30, label = "Owner's" });

            var flags = (await GetJsonAsync(member, "/api/vods/40/timeline")).GetProperty("markers").EnumerateArray()
                .ToDictionary(x => x.GetProperty("id").GetInt32(), x => x.GetProperty("canEdit").GetBoolean());
            Assert.True(flags[mine]);
            Assert.False(flags[theirs]);

            // Resize, retitle and recolor in one go (the body is the whole marker); the new colors are accepted
            var changed = await member.PutAsJsonAsync($"/api/markers/{mine}", new { seconds = 12.346, endSeconds = 25, label = "Intro part 1", color = "teal" }, ct);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var json = await changed.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(12.35, json.GetProperty("seconds").GetDouble());
            Assert.Equal(25, json.GetProperty("endSeconds").GetDouble());
            Assert.Equal("Intro part 1", json.GetProperty("label").GetString());
            Assert.Equal("teal", json.GetProperty("color").GetString());

            Assert.Equal(HttpStatusCode.BadRequest, (await member.PutAsJsonAsync($"/api/markers/{mine}", new { seconds = 12, endSeconds = 12.2 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/markers/{theirs}", new { seconds = 31 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsJsonAsync("/api/markers/999999", new { seconds = 31 }, ct)).StatusCode);

            // EditAnyEntries lets a member change anyone's
            var editor = await CreateRoleAsync(owner, "Editor", "ViewLibrary", "EditAnyEntries");
            await AssignAsync(owner, memberId, editor);
            Assert.Equal(HttpStatusCode.OK, (await member.PutAsJsonAsync($"/api/markers/{theirs}", new { seconds = 31, label = "Moved", color = "violet" }, ct)).StatusCode);
            var log = await GetJsonAsync(owner, "/api/team/audit");
            Assert.Contains(log.EnumerateArray(), x => x.GetProperty("action").GetString() == "marker.changed");
        }
}
}
