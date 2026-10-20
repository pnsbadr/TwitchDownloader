namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>Where VOD renditions come from. Twitch in the app; a fake in tests.</summary>
    public interface IVodPlaylistSource
    {
        /// <summary>The audio-only rendition (or the lowest video quality), or null if the VOD is gone or restricted.</summary>
        Task<VodRendition?> GetAudioRenditionAsync(HttpClient httpClient, long vodId, CancellationToken cancellationToken);

        /// <summary>The rendition Core downloads for <paramref name="quality"/>, or null if the VOD is gone or restricted.</summary>
        Task<VodRendition?> GetRenditionAsync(HttpClient httpClient, long vodId, string? quality, CancellationToken cancellationToken);
    }

    public sealed class TwitchVodPlaylistSource : IVodPlaylistSource
    {
        public Task<VodRendition?> GetAudioRenditionAsync(HttpClient httpClient, long vodId, CancellationToken cancellationToken) =>
            VodPlaylist.GetAudioRenditionAsync(httpClient, vodId, cancellationToken);

        public Task<VodRendition?> GetRenditionAsync(HttpClient httpClient, long vodId, string? quality, CancellationToken cancellationToken) =>
            VodPlaylist.GetRenditionAsync(httpClient, vodId, quality, cancellationToken);
    }
}
