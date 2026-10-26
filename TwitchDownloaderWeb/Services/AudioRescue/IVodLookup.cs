namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>Whether a VOD still exists on Twitch. Separate so tests can fake Twitch.</summary>
    public interface IVodLookup
    {
        /// <returns>False if Twitch has deleted the VOD (expired, or removed by the streamer). Throws if Twitch can't be reached.</returns>
        Task<bool> ExistsAsync(long vodId);
    }
}
