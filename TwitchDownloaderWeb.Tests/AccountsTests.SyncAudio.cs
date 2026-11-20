using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Sync;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Lining two streams up by their sound, and remembering it per pair of VODs (badrfly 2026-11-20).</summary>
    public sealed partial class AccountsTests
    {
        private readonly FakeLoudness _loudness = new();

        /// <summary>
        /// One shared voice call heard on every stream: the loudness at a wall-clock moment is the same everywhere, each stream adds its own
        /// noise and hears it <c>Delay</c> seconds late (its stream delay). VODs not in <c>Streams</c> have no audio.
        /// </summary>
        private sealed class FakeLoudness : IVodLoudness
        {
            public readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime Start, double Delay, int Noise)> Streams = new();

            public static float Call(long tenMs)
            {
                // Talk bursts: on/off every 0.3–1.5 s, loudness varying per burst
                var block = tenMs / 37;
                var on = Hash(block) % 3 != 0;
                return on ? 1f + Hash(block * 7 + 1) % 100 / 50f : 0f;
            }

            private static long Hash(long x)
            {
                unchecked
                {
                    var h = (ulong)x * 0x9E3779B97F4A7C15UL;
                    h ^= h >> 31;
                    return (long)(h % 1_000_003);
                }
            }

            public Task<float[]?> GetAsync(long vodId, double start, double seconds, CancellationToken cancellationToken)
            {
                if (!Streams.TryGetValue(vodId, out var stream))
                    return Task.FromResult<float[]?>(null);
                var origin = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
                var frames = (int)(seconds * AudioAligner.Rate);
                var result = new float[frames];
                var random = new Random(stream.Noise);
                for (var i = 0; i < frames; i++)
                {
                    var wall = (stream.Start - origin).TotalSeconds + start + (double)i / AudioAligner.Rate - stream.Delay;
                    result[i] = Call((long)Math.Round(wall * AudioAligner.Rate)) + (float)(random.NextDouble() * 0.6);
                }
                return Task.FromResult<float[]?>(result);
            }
        }

        [Fact]
        public void AlignerFindsTheLagAndRefusesNoise()
        {
            var a = new float[3000];
            var b = new float[7000];
            var random = new Random(1);
            for (var i = 0; i < b.Length; i++) b[i] = FakeLoudness.Call(i) + (float)random.NextDouble() * 0.5f;
            for (var i = 0; i < a.Length; i++) a[i] = FakeLoudness.Call(i + 1234) + (float)random.NextDouble() * 0.5f;
            var match = AudioAligner.Find(a, b)!;
            Assert.Equal(1234, match.Lag);
            Assert.True(match.Trusted);

            var noise = new float[3000];
            for (var i = 0; i < noise.Length; i++) noise[i] = (float)random.NextDouble();
            Assert.False(AudioAligner.Find(noise, b)!.Trusted);
            Assert.Null(AudioAligner.Find(new float[3000], b)); // silence

            // The envelope takes the level out: a quieter copy has the same shape
            var loud = new short[8000 * 4];
            for (var i = 0; i < loud.Length; i++) loud[i] = (short)((i / 4000 % 2 == 0 ? 8000 : 500) * Math.Sin(i * 0.3));
            var quiet = loud.Select(x => (short)(x / 4)).ToArray();
            var e1 = AudioAligner.Envelope(loud, 8000);
            var e2 = AudioAligner.Envelope(quiet, 8000);
            Assert.Equal(400, e1.Length);
            Assert.True(e1.Zip(e2).All(x => Math.Abs(x.First - x.Second) < 0.05));
        }

        [Fact]
        public async Task SyncLinesUpByAudioAndRemembersThePair()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, teamId, memberId) = await TeamWithMemberAsync("sy2");
            var staff = await StaffClientAsync();
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "bonnie" }, ct)).StatusCode);
            var arkyStart = new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
            var bonnieStart = arkyStart.AddMinutes(30);
            _vods.Vods[710] = new VodDetails(710, "Arky collab", "Arky", 4 * 3600, arkyStart, null);
            _vods.Vods[811] = new VodDetails(811, "Bonnie collab", "Bonnie", 3 * 3600, bonnieStart, null, ChannelLogin: "bonnie");
            _channelVods.Vods["bonnie"] = [Broadcast(811, bonnieStart, 3 * 3600)];
            _channelVods.Vods["arky"] = [Broadcast(710, arkyStart, 4 * 3600)];
            // Bonnie's stream runs 4.3 s later than Arky's for the same moment
            _loudness.Streams[710] = (arkyStart, 0, 1);
            _loudness.Streams[811] = (bonnieStart, 4.3, 2);

            // By start time alone: 1:00:00 into Arky = 30:00 into Bonnie
            var sync = await GetJsonAsync(owner, "/api/vods/710/sync?channel=bonnie&start=3600&end=3630");
            var match = Assert.Single(sync.GetProperty("matches").EnumerateArray());
            Assert.Equal(1800, match.GetProperty("start").GetDouble());
            Assert.Equal("start", match.GetProperty("lined").GetString());

            // The audio finds the 4.3 s and saves it for the pair
            var audio = await PostJsonAsync(owner, "/api/vods/710/sync/811/audio", new { at = 3600, otherAt = 1800 }, HttpStatusCode.OK);
            Assert.True(audio.GetProperty("trusted").GetBoolean());
            Assert.Equal(4.3, audio.GetProperty("shift").GetDouble(), 2);
            Assert.Equal(1804.3, audio.GetProperty("otherAt").GetDouble(), 2);

            // Every later sync between them uses it, from either side
            sync = await GetJsonAsync(owner, "/api/vods/710/sync?channel=bonnie&start=5000&end=5010");
            match = Assert.Single(sync.GetProperty("matches").EnumerateArray());
            Assert.Equal(3204.3, match.GetProperty("start").GetDouble(), 2);
            Assert.Equal("audio", match.GetProperty("lined").GetString());
            sync = await GetJsonAsync(owner, "/api/vods/811/sync?channel=arky&start=3204.3&end=3214.3");
            Assert.Equal(5000, Assert.Single(sync.GetProperty("matches").EnumerateArray()).GetProperty("start").GetDouble(), 2);

            // A nudge by hand replaces it (the member's role needs AddEntries)
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/vods/710/sync/811", new { at = 100, otherAt = 0 }, ct)).StatusCode);
            await AssignAsync(owner, memberId, await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries"));
            Assert.Equal(HttpStatusCode.OK, (await member.PutAsJsonAsync("/api/vods/811/sync/710", new { at = 1804.5, otherAt = 3600 }, ct)).StatusCode);
            sync = await GetJsonAsync(member, "/api/vods/710/sync?channel=bonnie&start=3600&end=3610");
            match = Assert.Single(sync.GetProperty("matches").EnumerateArray());
            Assert.Equal(1804.5, match.GetProperty("start").GetDouble(), 2);
            Assert.Equal("manual", match.GetProperty("lined").GetString());

            // No audio to read: nothing changes and it says why
            _vods.Vods[812] = new VodDetails(812, "Bonnie solo", "Bonnie", 3600, bonnieStart, null, ChannelLogin: "bonnie");
            var none = await PostJsonAsync(owner, "/api/vods/710/sync/812/audio", new { at = 3600, otherAt = 1800 }, HttpStatusCode.OK);
            Assert.False(none.GetProperty("trusted").GetBoolean());
            Assert.Equal("Couldn't read the audio.", none.GetProperty("reason").GetString());

            // Another team's VOD, or the same VOD, is refused
            _vods.Vods[713] = new VodDetails(713, "Elsewhere", "Qsmp", 3600, arkyStart, null, ChannelLogin: "qsmp");
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/vods/710/sync/713/audio", new { at = 0, otherAt = 0 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/vods/710/sync/710", new { at = 0, otherAt = 0 }, ct)).StatusCode);
        }
    }
}
