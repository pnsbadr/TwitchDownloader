using System.Text.RegularExpressions;
using TwitchDownloaderCore;

namespace TwitchDownloaderWeb.Services
{
    public enum VodType
    {
        All,
        Archive,
        Highlight,
        Upload
    }

    public sealed record ChannelDetails(string Login, string DisplayName, string TwitchUserId);

    public sealed record ChannelVod(long VodId, string Title, int LengthSeconds, DateTime CreatedAtUtc, string? ThumbnailUrl, int ViewCount,
        string? Game, bool UnmuteWindowOpen);

    public sealed record ChannelVodPage(IReadOnlyList<ChannelVod> Items, string? NextCursor, int TotalCount);

    /// <summary>Looks up Twitch channels and lists their VODs with Core's GQL helpers (the same ones the desktop Mass Downloader uses).</summary>
    public sealed partial class ChannelService
    {
        /// <summary>
        /// How long after a VOD ends Twitch's unmuted segments are assumed to still exist. Core uses the same 24 hours
        /// before it stops trying to unmute; Twitch doesn't publish the real figure.
        /// </summary>
        public static readonly TimeSpan UnmuteWindow = TimeSpan.FromHours(24);

        public const int MaxPageSize = 100;

        // Twitch logins are 1-25 letters, digits or underscores. Core pastes the login straight into its GQL query,
        // so anything else must be rejected before it gets there.
        [GeneratedRegex("^[a-z0-9_]{1,25}$")]
        private static partial Regex LoginRegex { get; }

        [GeneratedRegex(@"^(?:https?://)?(?:www\.|m\.)?twitch\.tv/([^/?#]+)", RegexOptions.IgnoreCase)]
        private static partial Regex ChannelUrlRegex { get; }

        // GQL cursors are opaque base64-ish strings; this is only to keep them from breaking Core's query string
        [GeneratedRegex("^[A-Za-z0-9+/=_-]{1,512}$")]
        private static partial Regex CursorRegex { get; }

        /// <summary>Accepts a login or a twitch.tv/&lt;login&gt; URL.</summary>
        /// <returns>The lower-case login, or null if it isn't a valid Twitch login.</returns>
        public static string? NormalizeLogin(string? text)
        {
            var value = text?.Trim() ?? "";
            if (ChannelUrlRegex.Match(value) is { Success: true } urlMatch)
                value = urlMatch.Groups[1].Value;

            value = value.TrimStart('@').ToLowerInvariant();
            return LoginRegex.IsMatch(value) ? value : null;
        }

        public static bool IsValidCursor(string? cursor) => string.IsNullOrEmpty(cursor) || CursorRegex.IsMatch(cursor);

        public static bool TryParseVodType(string? text, out VodType type)
        {
            type = VodType.Archive;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            // Enum.TryParse also accepts numbers, so require one of the names
            return Enum.GetNames<VodType>().Contains(text.Trim(), StringComparer.OrdinalIgnoreCase)
                   && Enum.TryParse(text.Trim(), true, out type);
        }

        /// <returns>The channel, or null if Twitch has no user with that login.</returns>
        public async Task<ChannelDetails?> GetChannelAsync(string login)
        {
            var ids = (await TwitchHelper.GetUserIds([login])).data?.users;
            var id = ids?.FirstOrDefault(x => x is not null)?.id;
            if (string.IsNullOrEmpty(id))
                return null;

            var user = (await TwitchHelper.GetUserInfo([id])).data?.users?.FirstOrDefault(x => x is not null);
            return new ChannelDetails(user?.login ?? login, user?.displayName ?? login, id);
        }

        /// <param name="login">A login already checked with <see cref="NormalizeLogin"/>.</param>
        /// <param name="cursor">Already checked with <see cref="IsValidCursor"/>.</param>
        /// <returns>The page, or null if Twitch has no user with that login.</returns>
        public async Task<ChannelVodPage?> GetVodsAsync(string login, VodType type, string? cursor, int limit)
        {
            var gqlType = type == VodType.All ? "" : type.ToString().ToUpperInvariant();
            var response = await TwitchHelper.GetGqlVideos(login, cursor ?? "", Math.Clamp(limit, 1, MaxPageSize), gqlType);

            var videos = response.data?.user?.videos;
            if (response.data?.user is null)
                return null;

            if (videos?.edges is null)
                return new ChannelVodPage([], null, 0);

            var now = DateTime.UtcNow;
            var items = videos.edges
                .Where(x => x?.node is not null && long.TryParse(x.node.id, out _))
                .Select(x =>
                {
                    var node = x.node;
                    var createdAt = node.createdAt.Kind switch
                    {
                        DateTimeKind.Local => node.createdAt.ToUniversalTime(),
                        _ => DateTime.SpecifyKind(node.createdAt, DateTimeKind.Utc)
                    };
                    var endedAt = createdAt.AddSeconds(node.lengthSeconds);

                    return new ChannelVod(long.Parse(node.id), node.title, node.lengthSeconds, createdAt, NullIfProcessing(node.previewThumbnailURL),
                        node.viewCount, node.game?.displayName, endedAt > now - UnmuteWindow);
                })
                .ToArray();

            // Relay-style paging: the next page starts after the last edge on this one
            var nextCursor = videos.pageInfo?.hasNextPage == true
                ? videos.edges.LastOrDefault(x => !string.IsNullOrEmpty(x?.cursor))?.cursor
                : null;

            return new ChannelVodPage(items, nextCursor, videos.totalCount);
        }

        /// <summary>VODs that are still processing (e.g. live) get a placeholder thumbnail; let the page show its own.</summary>
        private static string? NullIfProcessing(string? url) =>
            string.IsNullOrEmpty(url) || url.Contains("404_processing", StringComparison.OrdinalIgnoreCase) ? null : url;
    }
}
