using System.Globalization;
using System.Text.Json;

namespace TwitchDownloaderWeb.Services.Live
{
    /// <summary>
    /// Reads Twitch EventSub messages. The notification part (<see cref="ReadStreamChange"/>: subscription + event) is the same for the
    /// WebSocket and webhook transports, so a webhook endpoint on a shared server can reuse it.
    /// </summary>
    public static class EventSubMessages
    {
        public sealed record WelcomeInfo(string SessionId, int KeepaliveSeconds);

        /// <summary>The WebSocket message type (metadata.message_type): session_welcome, session_keepalive, notification, session_reconnect, revocation.</summary>
        public static string? TypeOf(JsonElement message) =>
            message.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("message_type", out var type) ? type.GetString() : null;

        public static WelcomeInfo? ReadWelcome(JsonElement message)
        {
            if (TypeOf(message) != "session_welcome" || !TryGetSession(message, out var session))
                return null;
            var id = session.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var keepalive = session.TryGetProperty("keepalive_timeout_seconds", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 30;
            return string.IsNullOrEmpty(id) ? null : new WelcomeInfo(id, Math.Clamp(keepalive, 10, 600));
        }

        /// <summary>The URL to move to on session_reconnect (only Twitch's own wss host is accepted).</summary>
        public static Uri? ReconnectUrl(JsonElement message)
        {
            if (!TryGetSession(message, out var session) || !session.TryGetProperty("reconnect_url", out var url) || url.GetString() is not { } text)
                return null;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "wss"
                && (uri.Host == "eventsub.wss.twitch.tv" || uri.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase))
                ? uri
                : null;
        }

        public static string? RevocationReason(JsonElement message) =>
            message.TryGetProperty("payload", out var payload) && payload.TryGetProperty("subscription", out var sub)
            && sub.TryGetProperty("status", out var status) ? status.GetString() : null;

        /// <summary>
        /// A stream.online / stream.offline notification as a change. Accepts the WebSocket message (with "payload") or a webhook body
        /// (subscription + event at the top).
        /// </summary>
        public static StreamChange? ReadStreamChange(JsonElement message)
        {
            var body = message.TryGetProperty("payload", out var payload) ? payload : message;
            if (!body.TryGetProperty("subscription", out var subscription) || !body.TryGetProperty("event", out var ev))
                return null;
            var type = subscription.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is not ("stream.online" or "stream.offline"))
                return null;

            var userId = ev.TryGetProperty("broadcaster_user_id", out var id) ? id.GetString() : null;
            var login = ev.TryGetProperty("broadcaster_user_login", out var l) ? l.GetString() : null;
            if (string.IsNullOrEmpty(login))
                return null;

            DateTime? startedAt = null;
            if (type == "stream.online" && ev.TryGetProperty("started_at", out var started) && started.GetString() is { } startedText
                && DateTime.TryParse(startedText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                startedAt = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

            return new StreamChange(login.ToLowerInvariant(), userId, type == "stream.online", startedAt, DateTime.UtcNow);
        }

        /// <summary>Helix "Get Streams": user ID → started_at of each live stream.</summary>
        public static Dictionary<string, DateTime?> ReadLiveStreams(JsonElement response)
        {
            var result = new Dictionary<string, DateTime?>();
            if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var stream in data.EnumerateArray())
            {
                if (stream.TryGetProperty("type", out var type) && type.GetString() != "live")
                    continue;
                if (!stream.TryGetProperty("user_id", out var id) || id.GetString() is not { } userId)
                    continue;
                DateTime? startedAt = null;
                if (stream.TryGetProperty("started_at", out var s) && s.GetString() is { } text
                    && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                    startedAt = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                result[userId] = startedAt;
            }
            return result;
        }

        private static bool TryGetSession(JsonElement message, out JsonElement session)
        {
            session = default;
            return message.TryGetProperty("payload", out var payload) && payload.TryGetProperty("session", out session);
        }
    }
}
