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

            // Another team sees none of it (the Main team has its own entry on VOD 1)
            var staff = await StaffClientAsync();
            var registration = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });
            var other = NewClient();
            await AcceptAsync(other, registration, "m1other", newAccount: true, teamName: "Other team");
            var otherTimeline = await GetJsonAsync(other, "/api/vods/1/timeline");
            Assert.Equal(0, otherTimeline.GetProperty("markers").GetArrayLength());
            Assert.Equal(0, otherTimeline.GetProperty("clips").GetArrayLength());
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
        public async Task MarkersOnALiveStreamMakeAReferenceEntryThatFollowsThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("m3");
            var streamStart = new DateTime(2026, 11, 10, 20, 9, 0, DateTimeKind.Utc);
            _vods.Vods[500] = new VodDetails(500, "Live grind", "Subroza", 1600, streamStart, null, IsLive: true);
            _vods.Vods[501] = new VodDetails(501, "Finished stream", "Subroza", 5000, streamStart, null);

            async Task<JsonElement> AddAsync(long vodId, object body)
            {
                var response = await owner.PostAsJsonAsync($"/api/vods/{vodId}/markers", body, ct);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            }

            async Task<JsonElement[]> LiveEntriesAsync() =>
                (await GetJsonAsync(owner, "/api/entries")).EnumerateArray().Where(x => x.GetProperty("followsMarkers").GetBoolean()).ToArray();

            // A finished VOD's markers make nothing
            Assert.Equal(JsonValueKind.Null, (await AddAsync(501, new { seconds = 100 })).GetProperty("liveEntry").ValueKind);
            Assert.Empty(await LiveEntriesAsync());

            // The first marker on the live VOD makes a reference entry 30 s either side of it
            var first = await AddAsync(500, new { seconds = 600, label = "round 9" });
            var change = first.GetProperty("liveEntry");
            Assert.True(change.GetProperty("created").GetBoolean());
            var entry = Assert.Single(await LiveEntriesAsync());
            Assert.Equal(change.GetProperty("entryId").GetInt32(), entry.GetProperty("id").GetInt32());
            Assert.Equal("Live grind", entry.GetProperty("title").GetString());
            Assert.Equal("ReferenceOnly", entry.GetProperty("status").GetString());
            Assert.Equal(570, entry.GetProperty("startSeconds").GetDouble());
            Assert.Equal(630, entry.GetProperty("endSeconds").GetDouble());

            // A segment near the live edge: the end stops at what has been streamed so far (1600 s), but covers the segment
            var segment = await AddAsync(500, new { seconds = 1500, endSeconds = 1590 });
            Assert.False(segment.GetProperty("liveEntry").GetProperty("created").GetBoolean());
            entry = Assert.Single(await LiveEntriesAsync());
            Assert.Equal(570, entry.GetProperty("startSeconds").GetDouble());
            Assert.Equal(1600, entry.GetProperty("endSeconds").GetDouble());
            var past = await AddAsync(500, new { seconds = 1650 }); // the player is ahead of Twitch's length
            entry = Assert.Single(await LiveEntriesAsync());
            Assert.Equal(1650, entry.GetProperty("endSeconds").GetDouble());

            // The stream ended: the entry still follows its markers; deleting one shrinks it again
            _vods.Vods[500] = _vods.Vods[500] with { LengthSeconds = 4000, IsLive = false };
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{past.GetProperty("id").GetInt32()}", ct)).StatusCode);
            entry = Assert.Single(await LiveEntriesAsync());
            Assert.Equal(1620, entry.GetProperty("endSeconds").GetDouble());
            await AddAsync(500, new { seconds = 100 });
            entry = Assert.Single(await LiveEntriesAsync());
            Assert.Equal(70, entry.GetProperty("startSeconds").GetDouble());

            // A cut picked by hand stays put
            var id = entry.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/entries/{id}/trim", new { startTime = "0:01:00", endTime = "0:30:00" }, ct)).StatusCode);
            Assert.Equal(JsonValueKind.Null, (await AddAsync(500, new { seconds = 3000 })).GetProperty("liveEntry").ValueKind);
            var kept = await GetJsonAsync(owner, $"/api/entries/{id}");
            Assert.False(kept.GetProperty("followsMarkers").GetBoolean());
            Assert.Equal(1800, kept.GetProperty("endSeconds").GetDouble());
            Assert.Empty(await LiveEntriesAsync());
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
