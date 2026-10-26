using TwitchDownloaderCore;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Interfaces;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services
{
    public sealed record VodDetails(long VodId, string Title, string? ChannelName, int LengthSeconds, DateTime StreamDateUtc, string? ThumbnailUrl);

    /// <summary>Looks up VOD metadata and qualities from Twitch, and holds the request checks shared by the endpoints.</summary>
    public sealed class VodInfoService : IVodLookup
    {
        public static long? ParseVodId(string? text)
        {
            var match = IdParse.MatchVideoId(text ?? "");
            return match is { Success: true } ? long.Parse(match.ValueSpan) : null;
        }

        // Uses the video info, not the playlist: a subscriber-only VOD has no public playlist but still exists
        public async Task<bool> ExistsAsync(long vodId) => await GetVodAsync(vodId) is not null;

        /// <returns>The VOD's details, or null if it doesn't exist (deleted, expired or never existed).</returns>
        public async Task<VodDetails?> GetVodAsync(long vodId)
        {
            var video = (await TwitchHelper.GetVideoInfo(vodId)).data?.video;
            if (video is null)
                return null;

            var streamDate = video.createdAt.Kind switch
            {
                DateTimeKind.Local => video.createdAt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(video.createdAt, DateTimeKind.Utc)
            };

            return new VodDetails(vodId, video.title, video.owner?.displayName, video.lengthSeconds, streamDate, video.thumbnailURLs?.FirstOrDefault());
        }

        /// <returns>The VOD's qualities, or null if the playlist is restricted (e.g. subscriber-only).</returns>
        public async Task<IVideoQualities<M3U8.Stream>?> GetQualitiesAsync(long vodId)
        {
            var accessToken = (await TwitchHelper.GetVideoToken(vodId, null)).data.videoPlaybackAccessToken;
            if (accessToken is null)
                return null;

            var playlist = await TwitchHelper.GetVideoPlaylist(vodId, accessToken.value, accessToken.signature);
            if (playlist.Contains("vod_manifest_restricted") || playlist.Contains("unauthorized_entitlements"))
                return null;

            return VideoQualities.FromM3U8(M3U8.Parse(playlist));
        }

        public static string[] GetVideoQualityNames(IVideoQualities<M3U8.Stream> qualities)
        {
            return qualities.Qualities
                .Select(x => x.Name)
                .Where(x => !IsAudioQuality(x))
                .ToArray();
        }

        public static bool IsAudioQuality(string? quality) => quality?.Contains("audio", StringComparison.OrdinalIgnoreCase) == true;

        /// <returns>Validation errors keyed by field, or null if the trim is valid for a VOD of this length.</returns>
        public static Dictionary<string, string[]>? ValidateTrim(TimeSpan startTime, TimeSpan? endTime, int vodLengthSeconds)
        {
            var vodLength = TimeSpan.FromSeconds(vodLengthSeconds);

            if (startTime < TimeSpan.Zero)
                return new() { ["startTime"] = ["Start time cannot be negative."] };

            if (endTime is { } end && end <= startTime)
                return new() { ["endTime"] = ["End time must be after start time."] };

            if (startTime >= vodLength)
                return new() { ["startTime"] = [$"Start time is past the end of the VOD ({TrimTime.Format(vodLength)})."] };

            if (endTime > vodLength)
                return new() { ["endTime"] = [$"End time is past the end of the VOD ({TrimTime.Format(vodLength)})."] };

            return null;
        }
    }
}
