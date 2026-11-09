using TwitchDownloaderWeb.Services.Playback;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <summary>
    /// HLS playlists for the page's own VOD player (hls.js). The server gets the access token and master playlist from Twitch;
    /// the browser then loads segments straight from Twitch's CDN, or through <c>/play/{variant}/{segment}</c> with
    /// <c>?segments=proxy</c> (for when the CDN won't serve the page directly).
    /// </summary>
    public static class PlaybackEndpoints
    {
        public static IEndpointRouteBuilder MapPlaybackEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/{vodId:long}/play.m3u8", GetMaster);
            app.MapGet("/api/vods/{vodId:long}/play/{variant:int}.m3u8", GetMedia);
            app.MapGet("/api/vods/{vodId:long}/play/{variant:int}/{segment}", GetSegment);
            app.MapGet("/api/vods/{vodId:long}/play/{variant:int}/clean/{key}.ts", GetCleanSegment);
            return app;
        }

        private static bool UseProxy(string? segments) => string.Equals(segments, "proxy", StringComparison.OrdinalIgnoreCase);

        /// <summary>Response header on the master: how many of the VOD's segments have saved clean audio the player will use.</summary>
        public const string CleanAudioHeader = "X-Clean-Audio-Segments";

        private static async Task<IResult> GetMaster(long vodId, string? segments, VodPlaybackService playback, PlaybackAudioSwap audioSwap,
            HttpContext context, CancellationToken cancellationToken)
        {
            PlaybackVariants variants;
            try
            {
                variants = await playback.GetVariantsAsync(vodId, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(detail: "Couldn't reach Twitch. Try again in a moment.", statusCode: StatusCodes.Status502BadGateway);
            }

            switch (variants.Status)
            {
                case PlaybackStatus.Restricted:
                    return Results.Problem(detail: "This VOD is for subscribers only, so the app player can't play it.", statusCode: StatusCodes.Status403Forbidden);
                case PlaybackStatus.NotFound:
                case PlaybackStatus.Ok when variants.Variants.Count == 0:
                    return Results.Problem(detail: $"VOD {vodId} was not found. It may be deleted, expired or private.", statusCode: StatusCodes.Status404NotFound);
            }

            var query = UseProxy(segments) ? "?segments=proxy" : "";
            context.Response.Headers[CleanAudioHeader] = (await audioSwap.GetCleanKeysAsync(vodId, cancellationToken)).Count.ToString();
            NoStore(context);
            return Results.Text(HlsPlaylists.BuildMaster(variants.Variants, i => $"/api/vods/{vodId}/play/{i}.m3u8{query}"), HlsPlaylists.ContentType);
        }

        private static async Task<IResult> GetMedia(long vodId, int variant, string? segments, VodPlaybackService playback, PlaybackAudioSwap audioSwap,
            HttpContext context, CancellationToken cancellationToken)
        {
            var proxy = UseProxy(segments);
            var cleanKeys = await audioSwap.GetCleanKeysAsync(vodId, cancellationToken);
            string? playlist;
            try
            {
                playlist = await playback.GetMediaPlaylistAsync(vodId, variant, (segment, baseUrl) =>
                {
                    var name = HlsPlaylists.ProxyableSegmentName(segment, baseUrl);
                    // Muted with clean audio saved: our server swaps the audio in (in either mode)
                    if (name is not null && PlaybackAudioSwap.MutedKey(name) is { } key && cleanKeys.Contains(key))
                        return $"/api/vods/{vodId}/play/{variant}/clean/{key}.ts";
                    return proxy && name is not null ? $"/api/vods/{vodId}/play/{variant}/{name}" : segment.AbsoluteUri;
                }, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(detail: "Couldn't reach Twitch. Try again in a moment.", statusCode: StatusCodes.Status502BadGateway);
            }

            if (playlist is null)
                return Results.Problem(detail: "This quality of the VOD isn't available.", statusCode: StatusCodes.Status404NotFound);

            // A live VOD's playlist grows: the player re-reads it, so it must never come from a cache
            NoStore(context);
            return Results.Text(playlist, HlsPlaylists.ContentType);
        }

        private static async Task<IResult> GetSegment(long vodId, int variant, string segment, VodPlaybackService playback, HttpContext context, CancellationToken cancellationToken)
        {
            HttpResponseMessage? response;
            try
            {
                response = await playback.GetSegmentAsync(vodId, variant, segment, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            if (response is null)
                return Results.NotFound();

            context.Response.RegisterForDispose(response);
            if (!response.IsSuccessStatusCode)
                return Results.StatusCode((int)response.StatusCode is 403 or 404 or 410 ? (int)response.StatusCode : StatusCodes.Status502BadGateway);

            if (response.Content.Headers.ContentLength is { } length)
                context.Response.ContentLength = length;
            // Segments of a finished VOD never change
            context.Response.Headers.CacheControl = "private, max-age=3600";
            var contentType = response.Content.Headers.ContentType?.MediaType
                ?? (segment.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "video/mp2t" : "video/mp4");
            return Results.Stream(await response.Content.ReadAsStreamAsync(cancellationToken), contentType);
        }

        /// <summary>A muted segment with the saved clean audio swapped in, or the muted one if that can't be done.</summary>
        private static async Task<IResult> GetCleanSegment(long vodId, int variant, string key, VodPlaybackService playback, PlaybackAudioSwap audioSwap,
            HttpContext context, CancellationToken cancellationToken)
        {
            if (!PlaybackAudioSwap.KeyRegex.IsMatch(key))
                return Results.NotFound();

            if (await audioSwap.GetSegmentAsync(vodId, variant, key, cancellationToken) is { } path)
            {
                context.Response.Headers.CacheControl = "private, max-age=3600";
                return Results.File(path, "video/mp2t", enableRangeProcessing: true);
            }

            return await GetSegment(vodId, variant, $"{key}-muted.ts", playback, context, cancellationToken);
        }

        private static void NoStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";
    }
}
