using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Services.Live;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Live stream updates (badrfly 2026-11-18): open pages hear when the team's channels start or stop streaming.</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task LiveEventsTellAPageAboutItsTeamsChannelsOnly()
        {
            var ct = TestContext.Current.CancellationToken;
            var staff = await StaffClientAsync();
            var token = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration", channels = new[] { "arky", "qsmp" } });
            var owner = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(owner, token, "liveowner", newAccount: true, teamName: "Live team")).StatusCode);

            var hub = _factory.Services.GetRequiredService<LiveStreamHub>();
            hub.Apply(new StreamChange("arky", "id-arky", true, new DateTime(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc), DateTime.UtcNow));

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/live/events");
            using var response = await owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));

            // The state now, for the team's channels
            var (name, hello) = await ReadEventAsync(reader, ct);
            Assert.Equal("hello", name);
            Assert.False(hello.GetProperty("watching").GetBoolean());
            var channels = hello.GetProperty("channels").EnumerateArray().ToDictionary(x => x.GetProperty("login").GetString()!);
            Assert.Equal(["arky", "qsmp"], channels.Keys.Order());
            Assert.True(channels["arky"].GetProperty("live").GetBoolean());
            Assert.Equal(JsonValueKind.Null, channels["qsmp"].GetProperty("live").ValueKind);

            // Another team's channel isn't sent; a repeated state isn't either
            hub.Apply(new StreamChange("ellsya", "id-ellsya", true, null, DateTime.UtcNow));
            hub.Apply(new StreamChange("arky", "id-arky", true, new DateTime(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc), DateTime.UtcNow));
            hub.Apply(new StreamChange("ARKY", "id-arky", false, null, DateTime.UtcNow));

            var (streamName, stream) = await ReadEventAsync(reader, ct);
            Assert.Equal("stream", streamName);
            Assert.Equal("arky", stream.GetProperty("login").GetString());
            Assert.False(stream.GetProperty("live").GetBoolean());
        }

        [Fact]
        public async Task TwitchSettingsAreOwnerOnly()
        {
            var ct = TestContext.Current.CancellationToken;
            var staff = await StaffClientAsync();
            var state = await GetJsonAsync(staff, "/api/admin/twitch");
            Assert.False(state.GetProperty("clientIdSet").GetBoolean());
            Assert.Equal("NotConnected", state.GetProperty("status").GetString());
            Assert.Contains(state.GetProperty("channels").EnumerateArray(), x => x.GetProperty("login").GetString() == "arky");

            // Starting a sign-in needs a Client ID; a bad one is refused
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsync("/api/admin/twitch/connect", null, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync("/api/admin/twitch/client-id", new { clientId = "abc def" }, ct)).StatusCode);
            var saved = await (await staff.PutAsJsonAsync("/api/admin/twitch/client-id", new { clientId = "abc123" }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("abc123", saved.GetProperty("clientId").GetString());
            Assert.False(saved.GetProperty("clientIdFromConfig").GetBoolean());

            // Not for team users, nor for moderators
            var token = await MakeLinkAsync(staff, "/api/admin/links", new { kind = "registration" });
            var owner = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(owner, token, "twitchowner", newAccount: true, teamName: "T")).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await owner.GetAsync("/api/admin/twitch", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().GetAsync("/api/live/events", ct)).StatusCode);
        }

        private static async Task<(string Name, JsonElement Data)> ReadEventAsync(StreamReader reader, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            string? name = null;
            string? data = null;
            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException();
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                    name = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                    data = line[6..];
                else if (line.Length == 0 && name is not null && data is not null)
                    return (name, JsonDocument.Parse(data).RootElement.Clone());
            }
        }
    }
}
