using TwitchDownloaderCore;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderWeb.Services
{
    /// <param name="VodId">The VOD the clip was cut from, or null if Twitch doesn't say (e.g. the VOD is gone).</param>
    /// <param name="VodOffsetSeconds">Where the clip starts in that VOD. Twitch only gives whole seconds.</param>
    /// <param name="Qualities">Quality names as Core's <see cref="ClipDownloader"/> understands them, best first.</param>
    public sealed record ClipDetails(string Slug, string Title, string? ChannelName, int DurationSeconds, DateTime CreatedAtUtc,
        string? ThumbnailUrl, long? VodId, int? VodOffsetSeconds, string[] Qualities);

    /// <summary>Looks up clips.twitch.tv clips with Core's public Twitch helpers.</summary>
    public sealed class ClipInfoService
    {
        /// <returns>The clip slug in a clips.twitch.tv / twitch.tv/{channel}/clip/ link or a bare slug, or null. VOD links and IDs are not clips.</returns>
        public static string? ParseClipSlug(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || VodInfoService.ParseVodId(text) is not null)
                return null;

            var match = IdParse.MatchClipId(text);
            return match is { Success: true } ? match.Value : null;
        }

        /// <returns>The clip's details, or null if it doesn't exist (deleted, or never existed).</returns>
        public async Task<ClipDetails?> GetClipAsync(string slug)
        {
            // GetClipInfo returns a null clip for a missing one; the render status call below throws instead, so check first
            var clip = (await TwitchHelper.GetClipInfo(slug)).data?.clip;
            if (clip is null)
                return null;

            var renderStatus = (await TwitchHelper.GetShareClipRenderStatus(slug)).data?.clip;
            var qualities = renderStatus is { assets.Length: > 0 }
                ? VideoQualities.FromClip(renderStatus).Qualities.Select(x => x.Name).ToArray()
                : [];

            long? vodId = long.TryParse(clip.video?.id, out var id) ? id : null;
            var createdAt = clip.createdAt.Kind == DateTimeKind.Local ? clip.createdAt.ToUniversalTime() : DateTime.SpecifyKind(clip.createdAt, DateTimeKind.Utc);

            return new ClipDetails(slug, clip.title, clip.broadcaster?.displayName, clip.durationSeconds, createdAt, clip.thumbnailURL,
                vodId, vodId is null ? null : clip.videoOffsetSeconds, qualities);
        }
    }
}
