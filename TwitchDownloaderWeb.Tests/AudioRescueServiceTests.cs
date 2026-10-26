using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>The grab → live re-check → prune lifecycle, against a fake Twitch serving fake segments.</summary>
    public sealed class AudioRescueServiceTests : IDisposable
    {
        private const long VodId = 123456789;
        private static readonly Uri BaseUrl = new("https://vod.example/abc/audio_only/");

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-web-rescue-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _services;
        private readonly FakePlaylists _playlists = new();
        private readonly FakeTwitchCdn _cdn = new();
        private readonly FakeVodLookup _vods = new();
        private readonly AudioRescueService _service;
        private readonly LibraryStorage _storage;

        public AudioRescueServiceTests()
        {
            Directory.CreateDirectory(_dir);
            var collection = new ServiceCollection();
            collection.AddDbContext<LibraryDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_dir, "test.db")};Pooling=False"));
            _services = collection.BuildServiceProvider();

            using (var scope = _services.CreateScope())
                scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.Migrate();

            _storage = new LibraryStorage(Options.Create(new DownloadSettings { OutputFolder = Path.Combine(_dir, "downloads") }), new FakeEnvironment(_dir),
                NullLogger<LibraryStorage>.Instance);

            _service = new AudioRescueService(_services.GetRequiredService<IServiceScopeFactory>(), new ChannelService(), _storage, new FakeHttpClientFactory(_cdn),
                _playlists, _vods, Options.Create(new AudioRescueSettings { Enabled = false, PruneAfterHours = 48 }), NullLogger<AudioRescueService>.Instance);
        }

        public void Dispose()
        {
            _services.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Fact]
        public async Task GrabsAllAudioThenKeepsOnlyMutedParts()
        {
            var ct = TestContext.Current.CancellationToken;

            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                AudioRescueService.AddRescue(db, VodId, "arky", "Test stream", DateTime.UtcNow.AddHours(-1), "test");
                await db.SaveChangesAsync(ct);
            }

            // 1. Live: segment 1 is already muted, so its clean copy (1.ts) is fetched instead
            _cdn.Serve("0.ts", 1000);
            _cdn.Serve("1.ts", 2000);
            _cdn.Serve("2.ts", 3000);
            _playlists.Set(live: true, ("0.ts", 10), ("1-muted.ts", 10), ("2.ts", 10));
            await _service.ProcessDueAsync(ct);

            var rescue = await GetRescueAsync(ct);
            Assert.Equal(AudioRescueStatus.Grabbing, rescue.Status);
            Assert.Equal(3, rescue.SavedCount);
            Assert.Equal(1, rescue.MutedCount);
            Assert.True(rescue.NextCheckAtUtc > DateTime.UtcNow, "live VODs are re-checked later, not immediately");
            Assert.DoesNotContain("1-muted.ts", _cdn.Requested);

            // 2. Stream ended; the new muted segment 3 has no clean copy any more (403)
            _cdn.Forbid("3.ts");
            _playlists.Set(live: false, ("0.ts", 10), ("1-muted.ts", 10), ("2.ts", 10), ("3-muted.ts", 10));
            await MakeDueAsync(ct);
            await _service.ProcessDueAsync(ct);

            rescue = await GetRescueAsync(ct);
            Assert.Equal(AudioRescueStatus.WaitingToPrune, rescue.Status);
            Assert.Equal(1, rescue.UnrecoverableCount);
            Assert.NotNull(rescue.PruneAfterUtc);
            Assert.True(rescue.PruneAfterUtc > DateTime.UtcNow.AddHours(47));
            Assert.Equal(1, _cdn.Requested.Count(x => x == "0.ts")); // already saved segments aren't fetched again

            // 3. Twitch muted segment 2 after the stream: its audio was already saved, so it's kept at prune time
            _playlists.Set(live: false, ("0.ts", 10), ("1-muted.ts", 10), ("2-muted.ts", 10), ("3-muted.ts", 10));
            await MakeDueAsync(ct);
            await _service.ProcessDueAsync(ct);

            rescue = await GetRescueAsync(ct);
            Assert.Equal(AudioRescueStatus.Pruned, rescue.Status);
            Assert.Equal(2, rescue.SavedCount);
            Assert.Equal(5000, rescue.StoredBytes);

            using var scope2 = _services.CreateScope();
            var segments = await scope2.ServiceProvider.GetRequiredService<LibraryDbContext>().RescuedAudioSegments
                .Where(x => x.VodId == VodId).OrderBy(x => x.Key).ToListAsync(ct);
            Assert.Equal(["1", "2", "3"], segments.Select(x => x.Key));
            Assert.True(segments.Single(x => x.Key == "3").Unrecoverable);
            Assert.False(File.Exists(_storage.Resolve(LibraryStorage.RescuedSegmentRelativePath(VodId, "0"))));
            Assert.True(File.Exists(_storage.Resolve(LibraryStorage.RescuedSegmentRelativePath(VodId, "2"))));
        }

        [Fact]
        public async Task ReportsMutedPartsWithNoCleanCopy()
        {
            var ct = TestContext.Current.CancellationToken;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                AudioRescueService.AddRescue(db, VodId, "arky", "Muted before we started", DateTime.UtcNow.AddHours(-12), "test");
                await db.SaveChangesAsync(ct);
            }

            // Twitch had already muted segment 1 and won't serve its clean copy
            _cdn.Serve("0.ts", 1000);
            _cdn.Forbid("1.ts");
            _playlists.Set(live: false, ("0.ts", 10), ("1-muted.ts", 10));
            await _service.ProcessDueAsync(ct);

            var restore = new AudioRestoreService(null!, _storage, new FakeHttpClientFactory(_cdn), _playlists, new ConfigurationBuilder().Build(), null!);
            using var scope2 = _services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<LibraryDbContext>();

            var entry = new LibraryEntry { Title = "Clip", VodId = VodId, StartTime = TimeSpan.FromSeconds(5), EndTime = TimeSpan.FromSeconds(15) };
            var result = await restore.TryDownloadRestoredAsync(db2, entry, Path.Combine(_dir, "out.mp4"), NullLogger.Instance, ct);
            Assert.Equal(new AudioRestoreResult(0, 5, false), result);

            // No rescue for this VOD: we don't know what's muted
            var other = new LibraryEntry { Title = "Other", VodId = VodId + 1, StartTime = TimeSpan.Zero };
            Assert.Null(await restore.TryDownloadRestoredAsync(db2, other, Path.Combine(_dir, "out2.mp4"), NullLogger.Instance, ct));
        }

        [Fact]
        public async Task DeletesAudioOnceTheVodIsGoneUnlessTheLibraryUsesIt()
        {
            var ct = TestContext.Current.CancellationToken;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                AudioRescueService.AddRescue(db, VodId, "arky", "Test stream", DateTime.UtcNow.AddHours(-1), "test");
                await db.SaveChangesAsync(ct);
            }

            _cdn.Serve("0.ts", 1000);
            _cdn.Serve("1.ts", 2000);
            _playlists.Set(live: false, ("0.ts", 10), ("1-muted.ts", 10));
            await _service.ProcessDueAsync(ct);
            await MakeDueAsync(ct);
            await _service.ProcessDueAsync(ct);
            Assert.Equal(AudioRescueStatus.Pruned, (await GetRescueAsync(ct)).Status);
            var kept = _storage.Resolve(LibraryStorage.RescuedSegmentRelativePath(VodId, "1"));
            Assert.True(File.Exists(kept));

            // Still on Twitch: kept
            await MakeDueAsync(ct);
            await _service.RemoveGoneVodsAsync(ct);
            Assert.Equal(AudioRescueStatus.Pruned, (await GetRescueAsync(ct)).Status);
            Assert.True((await GetRescueAsync(ct)).NextCheckAtUtc > DateTime.UtcNow.AddHours(1), "the next check is hours away");

            // Gone from Twitch, but a library entry uses the VOD: kept
            _vods.Exists = false;
            int entryId;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var entry = new LibraryEntry { Title = "Clip", VodId = VodId };
                db.Entries.Add(entry);
                await db.SaveChangesAsync(ct);
                entryId = entry.Id;
            }

            var calls = _vods.Calls;
            await MakeDueAsync(ct);
            await _service.RemoveGoneVodsAsync(ct);
            Assert.Equal(AudioRescueStatus.Pruned, (await GetRescueAsync(ct)).Status);
            Assert.True(File.Exists(kept));
            Assert.Equal(calls, _vods.Calls); // no need to ask Twitch while the library uses it

            // Entry deleted: the audio goes
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                db.Entries.Remove(await db.Entries.SingleAsync(x => x.Id == entryId, ct));
                await db.SaveChangesAsync(ct);
            }

            // Missing, but maybe only unpublished: kept during the grace period
            await MakeDueAsync(ct);
            await _service.RemoveGoneVodsAsync(ct);
            var rescue = await GetRescueAsync(ct);
            Assert.Equal(AudioRescueStatus.Pruned, rescue.Status);
            Assert.NotNull(rescue.VodMissingSinceUtc);
            Assert.True(File.Exists(kept));

            // Republished: the clock resets
            _vods.Exists = true;
            await MakeDueAsync(ct);
            await _service.RemoveGoneVodsAsync(ct);
            Assert.Null((await GetRescueAsync(ct)).VodMissingSinceUtc);

            // Missing for longer than the grace period: the audio goes
            _vods.Exists = false;
            await MakeDueAsync(ct);
            await _service.RemoveGoneVodsAsync(ct);
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var row = await db.AudioRescues.SingleAsync(x => x.VodId == VodId, ct);
                row.VodMissingSinceUtc = DateTime.UtcNow.AddDays(-15);
                await db.SaveChangesAsync(ct);
            }

            await MakeDueAsync(ct);
            await _service.RemoveGoneVodsAsync(ct);
            rescue = await GetRescueAsync(ct);
            Assert.Equal(AudioRescueStatus.VodGone, rescue.Status);
            Assert.Equal(0, rescue.StoredBytes);
            Assert.False(File.Exists(kept));
            using var scope2 = _services.CreateScope();
            Assert.False(await scope2.ServiceProvider.GetRequiredService<LibraryDbContext>().RescuedAudioSegments.AnyAsync(x => x.VodId == VodId, ct));
        }

        [Fact]
        public async Task FailsWhenTheVodIsGone()
        {
            var ct = TestContext.Current.CancellationToken;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                AudioRescueService.AddRescue(db, VodId, "arky", "Gone", DateTime.UtcNow.AddHours(-1), "test");
                await db.SaveChangesAsync(ct);
            }

            await _service.ProcessDueAsync(ct);

            var rescue = await GetRescueAsync(ct);
            Assert.Equal(AudioRescueStatus.Failed, rescue.Status);
            Assert.NotNull(rescue.Error);
        }

        private async Task<VodAudioRescue> GetRescueAsync(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().AudioRescues.AsNoTracking().SingleAsync(x => x.VodId == VodId, ct);
        }

        private async Task MakeDueAsync(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var rescue = await db.AudioRescues.SingleAsync(x => x.VodId == VodId, ct);
            rescue.NextCheckAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync(ct);
        }

        private sealed class FakeVodLookup : IVodLookup
        {
            public bool Exists { get; set; } = true;
            public int Calls { get; private set; }

            public Task<bool> ExistsAsync(long vodId)
            {
                Calls++;
                return Task.FromResult(Exists);
            }
        }

        private sealed class FakePlaylists : IVodPlaylistSource
        {
            private VodRendition? _rendition;

            public void Set(bool live, params (string Name, decimal Duration)[] segments)
            {
                var text = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n" + string.Concat(segments.Select(s => $"#EXTINF:{s.Duration},\n{s.Name}\n")) + (live ? "" : "#EXT-X-ENDLIST\n");
                _rendition = new VodRendition("Audio", BaseUrl, VodPlaylist.ParseSegments(text), VodPlaylist.IsLivePlaylist(text));
            }

            public Task<VodRendition?> GetAudioRenditionAsync(HttpClient httpClient, long vodId, CancellationToken cancellationToken) => Task.FromResult(_rendition);

            public Task<VodRendition?> GetRenditionAsync(HttpClient httpClient, long vodId, string? quality, CancellationToken cancellationToken) => Task.FromResult(_rendition);
        }

        private sealed class FakeTwitchCdn : HttpMessageHandler
        {
            private readonly Dictionary<string, int> _files = new();
            private readonly HashSet<string> _forbidden = new();
            public List<string> Requested { get; } = new();

            public void Serve(string name, int size) => _files[name] = size;
            public void Forbid(string name) => _forbidden.Add(name);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var name = request.RequestUri!.Segments.Last();
                lock (Requested) Requested.Add(name);

                if (_forbidden.Contains(name))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                if (!_files.TryGetValue(name, out var size))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

                // Not real TS data: the service copes with files that have no readable timestamps
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[size]) });
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
