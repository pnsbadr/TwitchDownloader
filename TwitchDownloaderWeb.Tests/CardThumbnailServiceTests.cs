using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Playback;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Card thumbnails: which frame, which segment, and a real frame from a fake VOD's HLS (needs ffmpeg for the last).</summary>
    public sealed class CardThumbnailServiceTests : IDisposable
    {
        private static readonly string Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } p ? p : "ffmpeg";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-web-thumbs-" + Guid.NewGuid().ToString("N"));

        public CardThumbnailServiceTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Fact]
        public void FrameIsTheMiddleOfWhatPlays()
        {
            Assert.Equal(15, CardThumbnailService.Middle([(10, 20)]));
            Assert.Equal(33, CardThumbnailService.Middle([(10, 14), (30, 40)])); // 14 s kept: 7 s in = 3 s into the second part
            Assert.Null(CardThumbnailService.Middle([]));

            // Cut-out parts move it; a re-trim gives a new URL
            var entry = new LibraryEntry { Id = 5, Title = "x", VodId = 1, StartTime = TimeSpan.FromSeconds(10), EndTime = TimeSpan.FromSeconds(20) };
            Assert.Equal("/api/entries/5/thumbnail?t=150", CardThumbnailService.EntryUrl(entry));
            entry.Cuts = CutOuts.Write([new CutRange(10, 14)]);
            Assert.Equal("/api/entries/5/thumbnail?t=170", CardThumbnailService.EntryUrl(entry));
            entry.EndTime = null;
            entry.VodLengthSeconds = 30;
            Assert.Equal("/api/entries/5/thumbnail?t=220", CardThumbnailService.EntryUrl(entry));

            // Twitch clips keep Twitch's own thumbnail
            entry.Kind = EntryKind.Clip;
            Assert.Null(CardThumbnailService.EntryUrl(entry));
        }

        [Fact]
        public void FindsTheSegmentAndTheTimeInIt()
        {
            const string playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.000,\n0.ts\n#EXTINF:10.000,\n1-muted.ts\n#EXTINF:4.000,\n2.ts\n#EXT-X-ENDLIST\n";
            Assert.Equal(("0.ts", null, 3.5), CardThumbnailService.FindSegment(playlist, 3.5));
            Assert.Equal(("1-muted.ts", null, 2), CardThumbnailService.FindSegment(playlist, 12));
            Assert.Equal(("2.ts", null, 3.95), CardThumbnailService.FindSegment(playlist, 99)); // past the end (a live VOD): the last frame

            const string fmp4 = "#EXTM3U\n#EXT-X-MAP:URI=\"init-0.mp4\"\n#EXTINF:2.000,\n0.mp4\n#EXTINF:2.000,\n1.mp4\n";
            Assert.Equal(("1.mp4", "init-0.mp4", 1), CardThumbnailService.FindSegment(fmp4, 3));

            // Anything not a plain name in the rendition's folder isn't fetched
            Assert.Null(CardThumbnailService.FindSegment("#EXTM3U\n#EXTINF:2.000,\n../x/0.ts\n", 1));
        }

        [Fact]
        public void PicksALowRendition()
        {
            static PlaybackVariant V(uint height) => new($"{height}p", $"https://cdn.test/{height}/index.m3u8", 1, [], height * 16 / 9, height, 30);
            Assert.Equal(2, CardThumbnailService.PickVariant([V(1080), V(720), V(480), V(160)]));
            Assert.Equal(1, CardThumbnailService.PickVariant([V(720), V(360)]));
            Assert.Equal(1, CardThumbnailService.PickVariant([V(240), V(160)]));
        }

        [Fact]
        public async Task TakesTheFrameFromTheVodsHls()
        {
            SkipIfNoFfmpeg();
            var ct = TestContext.Current.CancellationToken;

            // Two Twitch-like 5 s segments (timestamps far from zero): red, then blue
            await Ff(ct, "-f", "lavfi", "-i", "color=red:s=320x180:r=30:d=5", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-output_ts_offset", "1000", "-f", "mpegts", "0.ts");
            await Ff(ct, "-f", "lavfi", "-i", "color=blue:s=320x180:r=30:d=5", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-output_ts_offset", "1005", "-f", "mpegts", "1-muted.ts");
            var cdn = new FakeCdn(new Dictionary<string, byte[]>
            {
                ["/360/index-dvr.m3u8"] = "#EXTM3U\n#EXTINF:5.000,\n0.ts\n#EXTINF:5.000,\n1-muted.ts\n#EXT-X-ENDLIST\n"u8.ToArray(),
                ["/360/0.ts"] = await File.ReadAllBytesAsync(Path.Combine(_dir, "0.ts"), ct),
                ["/360/1-muted.ts"] = await File.ReadAllBytesAsync(Path.Combine(_dir, "1-muted.ts"), ct),
            });
            var variants = new PlaybackVariants(PlaybackStatus.Ok, [
                new PlaybackVariant("1080p60", "https://cdn.test/1080/index-dvr.m3u8", 1, [], 1920, 1080, 60),
                new PlaybackVariant("360p30", "https://cdn.test/360/index-dvr.m3u8", 1, [], 640, 360, 30),
            ]);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var factory = new FakeHttpClientFactory(cdn);
            var playback = new VodPlaybackService(new FakeSource(variants), cache, factory);
            var storage = new LibraryStorage(Options.Create(new DownloadSettings { OutputFolder = Path.Combine(_dir, "downloads") }), new FakeEnvironment(_dir),
                NullLogger<LibraryStorage>.Instance);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ffmpeg:Path"] = Ffmpeg }).Build();
            var service = new CardThumbnailService(playback, storage, configuration, cache, NullLogger<CardThumbnailService>.Instance);

            // A reference-only clip from 2 s to 12 s: the middle (7 s) is 2 s into the blue segment
            var entry = new LibraryEntry
            {
                Id = 1, Title = "x", VodId = 42, StartTime = TimeSpan.FromSeconds(2), EndTime = TimeSpan.FromSeconds(12), Status = EntryStatus.ReferenceOnly,
            };
            var path = await service.GetForEntryAsync(entry, ct);
            Assert.NotNull(path);
            Assert.EndsWith(Path.Combine("thumbs", "42", "70.jpg"), path);
            var (r, g, b) = await AverageColorAsync(path, ct);
            Assert.True(b > 150 && r < 80, $"expected blue, got {r},{g},{b}");
            Assert.Contains("/360/1-muted.ts", cdn.Requested);
            Assert.DoesNotContain(cdn.Requested, x => x.StartsWith("/1080/"));

            // Made once: shown again without asking Twitch
            var requests = cdn.Requested.Count;
            Assert.Equal(path, await service.GetForEntryAsync(entry, ct));
            Assert.Equal(requests, cdn.Requested.Count);

            // A frame in the first segment is red
            entry.EndTime = TimeSpan.FromSeconds(4);
            var red = await AverageColorAsync((await service.GetForEntryAsync(entry, ct))!, ct);
            Assert.True(red.R > 150 && red.B < 80, $"expected red, got {red}");
        }

        [Fact]
        public async Task GrabsAFullSizeFrameForTheScreenshotButton()
        {
            SkipIfNoFfmpeg();
            var ct = TestContext.Current.CancellationToken;

            await Ff(ct, "-f", "lavfi", "-i", "color=red:s=640x360:r=30:d=5", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-output_ts_offset", "1000", "-f", "mpegts", "0.ts");
            await Ff(ct, "-f", "lavfi", "-i", "color=blue:s=640x360:r=30:d=5", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-output_ts_offset", "1005", "-f", "mpegts", "1.ts");
            var cdn = new FakeCdn(new Dictionary<string, byte[]>
            {
                ["/best/index-dvr.m3u8"] = "#EXTM3U\n#EXTINF:5.000,\n0.ts\n#EXTINF:5.000,\n1.ts\n#EXT-X-ENDLIST\n"u8.ToArray(),
                ["/best/0.ts"] = await File.ReadAllBytesAsync(Path.Combine(_dir, "0.ts"), ct),
                ["/best/1.ts"] = await File.ReadAllBytesAsync(Path.Combine(_dir, "1.ts"), ct),
            });
            var variants = new PlaybackVariants(PlaybackStatus.Ok, [
                new PlaybackVariant("360p30", "https://cdn.test/best/index-dvr.m3u8", 2, [], 640, 360, 30),
                new PlaybackVariant("160p30", "https://cdn.test/low/index-dvr.m3u8", 1, [], 284, 160, 30),
            ]);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var playback = new VodPlaybackService(new FakeSource(variants), cache, new FakeHttpClientFactory(cdn));
            var storage = new LibraryStorage(Options.Create(new DownloadSettings { OutputFolder = Path.Combine(_dir, "downloads") }), new FakeEnvironment(_dir),
                NullLogger<LibraryStorage>.Instance);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ffmpeg:Path"] = Ffmpeg }).Build();
            var service = new CardThumbnailService(playback, storage, configuration, cache, NullLogger<CardThumbnailService>.Instance);

            var png = await service.GrabFrameAsync(42, 7.2, null, ct);
            Assert.NotNull(png);
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
            // Full size (PNG IHDR width/height, big-endian), from the best rendition
            Assert.Equal(640, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
            Assert.Equal(360, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
            Assert.DoesNotContain(cdn.Requested, x => x.StartsWith("/low/"));
            var file = Path.Combine(_dir, "frame.png");
            await File.WriteAllBytesAsync(file, png, ct);
            var (r, _, b) = await AverageColorAsync(file, ct);
            Assert.True(b > 150 && r < 80, $"expected blue, got {r},{b}");
            // Nothing kept on disk
            Assert.False(Directory.Exists(Path.Combine(_dir, "downloads", CardThumbnailService.FolderName)));
        }

        private async Task<(int R, int G, int B)> AverageColorAsync(string jpg, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-v", "error", "-i", jpg, "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
            await process.WaitForExitAsync(ct);
            var bytes = output.ToArray();
            return (bytes[0], bytes[1], bytes[2]);
        }

        private async Task Ff(CancellationToken ct, params string[] args)
        {
            var psi = new ProcessStartInfo(Ffmpeg) { WorkingDirectory = _dir, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-hide_banner", "-y" }.Concat(args))
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            Assert.True(process.ExitCode == 0, await error);
        }

        private static void SkipIfNoFfmpeg()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Ffmpeg, "-version") { RedirectStandardOutput = true, UseShellExecute = false });
                p!.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip($"ffmpeg not found ({Ffmpeg}); set FFMPEG_PATH to run this test.");
            }
        }

        private sealed class FakeSource(PlaybackVariants variants) : IVodPlaybackSource
        {
            public Task<PlaybackVariants> GetVariantsAsync(long vodId, CancellationToken cancellationToken) => Task.FromResult(variants);
        }

        private sealed class FakeCdn(Dictionary<string, byte[]> files) : HttpMessageHandler
        {
            public List<string> Requested { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                lock (Requested) Requested.Add(path);
                return Task.FromResult(files.TryGetValue(path, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, false);
        }

        private sealed class FakeEnvironment(string root) : IWebHostEnvironment
        {
            public string WebRootPath { get; set; } = root;
            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
            public string ApplicationName { get; set; } = "Tests";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
            public string ContentRootPath { get; set; } = root;
            public string EnvironmentName { get; set; } = "Test";
        }
    }
}
