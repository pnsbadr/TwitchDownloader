using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TwitchDownloaderWeb.Services.Live;

namespace TwitchDownloaderWeb.Tests
{
    public sealed class EventSubMessagesTests
    {
        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public void ReadsWelcomeAndReconnect()
        {
            var welcome = Parse("""
                {"metadata":{"message_id":"1","message_type":"session_welcome","message_timestamp":"2026-11-18T20:00:00Z"},
                 "payload":{"session":{"id":"AQoQexAWVYKSTIu4ec_2VAxyuhAB","status":"connected","keepalive_timeout_seconds":30,"reconnect_url":null}}}
                """);
            Assert.Equal("session_welcome", EventSubMessages.TypeOf(welcome));
            Assert.Equal(new EventSubMessages.WelcomeInfo("AQoQexAWVYKSTIu4ec_2VAxyuhAB", 30), EventSubMessages.ReadWelcome(welcome));

            var reconnect = Parse("""
                {"metadata":{"message_type":"session_reconnect"},
                 "payload":{"session":{"id":"x","status":"reconnecting","reconnect_url":"wss://eventsub.wss.twitch.tv/ws?abc=1"}}}
                """);
            Assert.Equal("wss://eventsub.wss.twitch.tv/ws?abc=1", EventSubMessages.ReconnectUrl(reconnect)?.ToString());

            // Only Twitch's own socket host
            var elsewhere = Parse("""{"payload":{"session":{"reconnect_url":"wss://example.com/ws"}}}""");
            Assert.Null(EventSubMessages.ReconnectUrl(elsewhere));
        }

        [Fact]
        public void ReadsStreamOnlineAndOfflineFromBothTransports()
        {
            var online = Parse("""
                {"metadata":{"message_type":"notification","subscription_type":"stream.online"},
                 "payload":{"subscription":{"id":"s1","type":"stream.online","version":"1","condition":{"broadcaster_user_id":"1337"}},
                  "event":{"id":"9001","broadcaster_user_id":"1337","broadcaster_user_login":"Arky","broadcaster_user_name":"Arky","type":"live",
                           "started_at":"2026-11-18T18:30:05Z"}}}
                """);
            var change = EventSubMessages.ReadStreamChange(online)!;
            Assert.Equal("arky", change.Login);
            Assert.Equal("1337", change.UserId);
            Assert.True(change.Live);
            Assert.Equal(new DateTime(2026, 11, 18, 18, 30, 5, DateTimeKind.Utc), change.StartedAtUtc);
            Assert.Equal(DateTimeKind.Utc, change.StartedAtUtc!.Value.Kind);

            // Webhook bodies have subscription + event at the top
            var offline = Parse("""
                {"subscription":{"type":"stream.offline","version":"1"},
                 "event":{"broadcaster_user_id":"1337","broadcaster_user_login":"arky","broadcaster_user_name":"Arky"}}
                """);
            var ended = EventSubMessages.ReadStreamChange(offline)!;
            Assert.False(ended.Live);
            Assert.Null(ended.StartedAtUtc);

            Assert.Null(EventSubMessages.ReadStreamChange(Parse("""{"payload":{"subscription":{"type":"channel.follow"},"event":{}}}""")));
        }

        [Fact]
        public void ReadsLiveStreams()
        {
            var streams = EventSubMessages.ReadLiveStreams(Parse("""
                {"data":[{"user_id":"1","user_login":"a","type":"live","started_at":"2026-11-18T10:00:00Z"},
                         {"user_id":"2","user_login":"b","type":""}],"pagination":{}}
                """));
            Assert.Equal(["1"], streams.Keys);
            Assert.Equal(new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc), streams["1"]);
        }

        [Fact]
        public async Task HubSendsOnlyChanges()
        {
            var hub = new LiveStreamHub(NullLogger<LiveStreamHub>.Instance);
            hub.SetChannels([("arky", "1", true), ("qsmp", "2", false)]);
            using var listener = hub.Listen();

            var started = new DateTime(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc);
            Assert.True(hub.Apply(new StreamChange("arky", "1", true, started, DateTime.UtcNow)));
            Assert.False(hub.Apply(new StreamChange("arky", "1", true, started, DateTime.UtcNow)));  // the catch-up check agreeing
            Assert.True(hub.Apply(new StreamChange("arky", "1", false, null, DateTime.UtcNow)));
            Assert.False(hub.Apply(new StreamChange("arky", "1", false, null, DateTime.UtcNow)));

            Assert.True((await listener.Reader.ReadAsync(TestContext.Current.CancellationToken)).Live);
            Assert.False((await listener.Reader.ReadAsync(TestContext.Current.CancellationToken)).Live);
            Assert.False(listener.Reader.TryRead(out _));
            Assert.True(hub.Get("arky")!.Watched);

            // Not connected any more: nothing is watched
            hub.SetStatus(LiveWatchStatus.Retrying);
            Assert.False(hub.Get("arky")!.Watched);
            hub.SetChannels([("qsmp", "2", true)]);
            Assert.Null(hub.Get("arky"));
        }
    }
}
