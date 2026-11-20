using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services.Sync
{
    /// <summary>A VOD's loudness envelope (<see cref="AudioAligner.Envelope"/>) over a range. <see cref="VodLoudness"/> in the app; a fake in tests.</summary>
    public interface IVodLoudness
    {
        /// <param name="start">VOD seconds.</param>
        /// <returns>The envelope (<see cref="AudioAligner.Rate"/> per second) from <paramref name="start"/>, or null when the VOD's audio
        /// can't be read (gone, sub-only).</returns>
        Task<float[]?> GetAsync(long vodId, double start, double seconds, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Reads a stretch of a VOD's audio-only rendition: the segments covering it are downloaded into a scratch folder, joined (TS files
    /// join end to end), decoded by ffmpeg to 8 kHz mono and turned into an envelope. Segment URLs never reach the logs.
    /// </summary>
    public sealed class VodLoudness(IVodPlaylistSource playlists, IHttpClientFactory httpClientFactory, LibraryStorage storage, IConfiguration configuration,
        ILogger<VodLoudness> logger) : IVodLoudness
    {
        public const string HttpClientName = nameof(VodLoudness);
        private const int SampleRate = 8000;

        public async Task<float[]?> GetAsync(long vodId, double start, double seconds, CancellationToken cancellationToken)
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            var rendition = await playlists.GetAudioRenditionAsync(http, vodId, cancellationToken);
            if (rendition is null)
                return null;

            var end = start + seconds;
            var segments = rendition.Segments.Where(x => (double)(x.Offset + x.Duration) > start && (double)x.Offset < end).ToList();
            if (segments.Count == 0)
                return null;

            var folder = storage.CreateWorkFolder($"sync-{vodId}");
            try
            {
                var joined = Path.Combine(folder, "audio.ts");
                await using (var output = File.Create(joined))
                {
                    foreach (var segment in segments)
                    {
                        // Muted segments are silent; the match then leans on the rest
                        using var response = await http.GetAsync(rendition.SegmentUri(segment.Path), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        if (!response.IsSuccessStatusCode)
                            return null;
                        await response.Content.CopyToAsync(output, cancellationToken);
                    }
                }

                var raw = Path.Combine(folder, "audio.raw");
                var skip = Math.Max(0, start - (double)segments[0].Offset);
                var ffmpeg = configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
                await FfmpegRunner.RunAsync(ffmpeg,
                [
                    "-hide_banner", "-nostdin", "-y", "-i", joined,
                    "-ss", skip.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-t", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "s16le", raw,
                ], folder, logger, cancellationToken);

                var bytes = await File.ReadAllBytesAsync(raw, cancellationToken);
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, bytes.Length & ~1));
                return AudioAligner.Envelope(samples, SampleRate);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
