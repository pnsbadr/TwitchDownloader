using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>"Sync with": the same moment on another channel's VOD, found by stream time (badrfly 2026-11-20).</summary>
    public sealed partial class AccountsTests
    {
        /// <summary>Channels' past broadcasts, newest first, two to a page; a channel not in here has none.</summary>
        private readonly FakeChannelVods _channelVods = new();

        private sealed class FakeChannelVods : IChannelVodSource
        {
            public readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<ChannelVod>> Vods = new();
            public int Calls;

            public Task<ChannelVodPage?> GetVodsAsync(string login, VodType type, string? cursor, int limit)
            {
                Interlocked.Increment(ref Calls);
                var all = Vods.GetValueOrDefault(login) ?? [];
                var from = int.Parse(cursor ?? "0");
                var page = all.OrderByDescending(x => x.CreatedAtUtc).Skip(from).Take(2).ToList();
                return Task.FromResult<ChannelVodPage?>(new ChannelVodPage(page, from + 2 < all.Count ? (from + 2).ToString() : null, all.Count));
            }
        }

        private static ChannelVod Broadcast(long id, DateTime start, int seconds) => new(id, $"Bonnie {id}", seconds, start, null, 0, null, false);

        [Fact]
        public async Task SyncFindsTheOtherChannelsVodByStreamTime()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("sy1");
            var staff = await StaffClientAsync();
            var arkyStart = new DateTime(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);
            _vods.Vods[700] = new VodDetails(700, "Arky collab", "Arky", 4 * 3600, arkyStart, null);
            _vods.Vods[801] = new VodDetails(801, "Bonnie 801", "Bonnie", 3600, arkyStart.AddMinutes(30), null, ChannelLogin: "bonnie");
            _vods.Vods[802] = new VodDetails(802, "Bonnie 802", "Bonnie", 3600, arkyStart.AddMinutes(95), null, ChannelLogin: "bonnie");
            // Bonnie started 30 min after Arky, streamed an hour, came back 5 min later for another hour; older streams days before
            _channelVods.Vods["bonnie"] =
            [
                Broadcast(802, arkyStart.AddMinutes(95), 3600), Broadcast(801, arkyStart.AddMinutes(30), 3600),
                Broadcast(799, arkyStart.AddDays(-3), 7200), Broadcast(798, arkyStart.AddDays(-4), 7200),
                Broadcast(797, arkyStart.AddDays(-5), 7200), Broadcast(796, arkyStart.AddDays(-6), 7200),
            ];

            // Only the team's channels
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/vods/700/sync?channel=bonnie&start=2400&end=2460", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "bonnie" }, ct)).StatusCode);

            // 40:00–41:00 into Arky's VOD = 10:00–11:00 into Bonnie's first
            var sync = await GetJsonAsync(owner, "/api/vods/700/sync?channel=bonnie&start=2400&end=2460");
            var match = Assert.Single(sync.GetProperty("matches").EnumerateArray());
            Assert.Equal(801, match.GetProperty("vodId").GetInt64());
            Assert.Equal(600, match.GetProperty("start").GetDouble());
            Assert.Equal(660, match.GetProperty("end").GetDouble());
            Assert.Equal(0, match.GetProperty("offset").GetDouble());
            Assert.Equal(60, sync.GetProperty("coveredSeconds").GetDouble());
            Assert.Equal("Bonnie", sync.GetProperty("displayName").GetString());
            // It stopped paging once it was past anything that could reach the range
            Assert.True(_channelVods.Calls <= 3);

            // 25:00–35:00: Bonnie wasn't live for the first 5 minutes
            sync = await GetJsonAsync(owner, "/api/vods/700/sync?channel=bonnie&start=1500&end=2100");
            match = Assert.Single(sync.GetProperty("matches").EnumerateArray());
            Assert.Equal(0, match.GetProperty("start").GetDouble());
            Assert.Equal(300, match.GetProperty("end").GetDouble());
            Assert.Equal(300, match.GetProperty("offset").GetDouble());
            Assert.Equal(JsonValueKind.Null, sync.GetProperty("after").ValueKind);

            // Across Bonnie's restart: two VODs, with the 5-minute break between them
            sync = await GetJsonAsync(owner, "/api/vods/700/sync?channel=bonnie&start=5280&end=5820");
            Assert.Equal([801L, 802L], sync.GetProperty("matches").EnumerateArray().Select(x => x.GetProperty("vodId").GetInt64()));
            Assert.Equal(240, sync.GetProperty("coveredSeconds").GetDouble());
            Assert.Equal(420, sync.GetProperty("matches")[1].GetProperty("offset").GetDouble());

            // Bonnie wasn't streaming: nothing, with the streams around it
            sync = await GetJsonAsync(owner, "/api/vods/700/sync?channel=bonnie&start=60&end=120");
            Assert.Empty(sync.GetProperty("matches").EnumerateArray());
            Assert.Equal(799, sync.GetProperty("before").GetProperty("vodId").GetInt64());
            Assert.Equal(801, sync.GetProperty("after").GetProperty("vodId").GetInt64());

            // Its own channel, bad ranges, a VOD the team can't open
            var same = await owner.GetAsync("/api/vods/700/sync?channel=arky&start=0&end=60", ct);
            Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
            Assert.Equal("same-channel", await CodeOf(same));
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/vods/700/sync?channel=bonnie&start=60&end=60.2", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/vods/700/sync?channel=bonnie&start=0&end=20000", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/vods/700/sync?channel=no%20way&start=0&end=60", ct)).StatusCode);
            _vods.Vods[703] = new VodDetails(703, "Elsewhere", "Qsmp", 3600, arkyStart, null, ChannelLogin: "qsmp");
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/vods/703/sync?channel=bonnie&start=0&end=60", ct)).StatusCode);

            // The synced row saves in Arky's edit: an edit may now use another VOD the team can open (not one it can't)
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, clipper);
            var created = await member.PostAsJsonAsync("/api/projects", new { vodId = 700, content = TwoTracks(Piece("a", "t1", 0, 700, 2400, 2460)) }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();
            var synced = await member.PutAsJsonAsync($"/api/projects/{id}",
                new { content = TwoTracks(Piece("a", "t1", 0, 700, 2400, 2460), Piece("b", "t2", 0, 801, 600, 660)) }, ct);
            Assert.Equal(HttpStatusCode.OK, synced.StatusCode);
            var saved = await synced.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(700, saved.GetProperty("vodId").GetInt64());
            Assert.Equal([700L, 801L], saved.GetProperty("vodIds").EnumerateArray().Select(x => x.GetInt64()));
            var refused = await member.PutAsJsonAsync($"/api/projects/{id}",
                new { content = TwoTracks(Piece("a", "t1", 0, 700, 2400, 2460), Piece("b", "t2", 0, 703, 0, 60)) }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            // It stays Arky's VOD's work
            var work = await GetJsonAsync(member, "/api/vods/work?vodIds=700,801");
            Assert.Equal([700L], work.EnumerateArray().Select(x => x.GetProperty("vodId").GetInt64()));
        }
    }
}
