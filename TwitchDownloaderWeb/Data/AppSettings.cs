namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// What the app Owner sets on the Admin page's App tab (badrfly 2026-11-19): the app's name and the X / Discord links in the start
    /// screen's corner. One row (Id 1). Null = use <c>App:Name</c> / <c>App:Links:X</c> / <c>App:Links:Discord</c> from configuration;
    /// an empty string = hidden (links) even when configuration has one.
    /// </summary>
    public class AppSettings
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? XUrl { get; set; }
        public string? DiscordUrl { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public int? UpdatedByStaffId { get; set; }
    }
}
