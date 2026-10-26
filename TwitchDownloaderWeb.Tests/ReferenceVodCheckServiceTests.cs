using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Reference-only entries are marked when their VOD leaves Twitch, and unmarked when it comes back.</summary>
    public sealed class ReferenceVodCheckServiceTests : IDisposable
    {
        private const long VodId = 987654321;

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-web-refcheck-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _services;
        private readonly FakeVodLookup _vods = new();
        private readonly ReferenceVodCheckService _service;

        public ReferenceVodCheckServiceTests()
        {
            Directory.CreateDirectory(_dir);
            var collection = new ServiceCollection();
            collection.AddDbContext<LibraryDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_dir, "test.db")};Pooling=False"));
            _services = collection.BuildServiceProvider();

            using (var scope = _services.CreateScope())
                scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.Migrate();

            _service = new ReferenceVodCheckService(_services.GetRequiredService<IServiceScopeFactory>(), _vods,
                Options.Create(new DownloadSettings { MaxStoredClipSeconds = 180 }), NullLogger<ReferenceVodCheckService>.Instance);
        }

        public void Dispose()
        {
            _services.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Fact]
        public async Task MarksReferenceEntriesWhenTheVodIsGoneAndBackWhenItReturns()
        {
            var ct = TestContext.Current.CancellationToken;
            int reference, stored, withFile;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var a = new LibraryEntry { Title = "Long clip", VodId = VodId, ClipLengthSeconds = 300, Status = EntryStatus.ReferenceOnly };
                var b = new LibraryEntry { Title = "Short clip", VodId = VodId, ClipLengthSeconds = 60, Status = EntryStatus.Ready, FilePath = "library/2.mp4" };
                var c = new LibraryEntry
                {
                    Title = "Generated", VodId = VodId, ClipLengthSeconds = 300, Status = EntryStatus.ReferenceOnly,
                    GenerationStatus = GenerationStatus.Ready, GeneratedFilePath = "generated/3.mp4", GeneratedExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                };
                db.Entries.AddRange(a, b, c);
                await db.SaveChangesAsync(ct);
                (reference, stored, withFile) = (a.Id, b.Id, c.Id);
            }

            // Still on Twitch: nothing changes
            await _service.RunOnceAsync(ct);
            Assert.Equal(EntryStatus.ReferenceOnly, await StatusAsync(reference, ct));

            // Gone: only the reference entry without a usable file is marked
            _vods.Exists = false;
            await _service.RunOnceAsync(ct);
            Assert.Equal(EntryStatus.VodUnavailable, await StatusAsync(reference, ct));
            Assert.Equal(EntryStatus.Ready, await StatusAsync(stored, ct));
            Assert.Equal(EntryStatus.ReferenceOnly, await StatusAsync(withFile, ct));

            // Republished: back to a normal reference entry
            _vods.Exists = true;
            await _service.RunOnceAsync(ct);
            Assert.Equal(EntryStatus.ReferenceOnly, await StatusAsync(reference, ct));

            // Twitch unreachable: left as is
            _vods.Throw = true;
            await _service.RunOnceAsync(ct);
            Assert.Equal(EntryStatus.ReferenceOnly, await StatusAsync(reference, ct));
        }

        private async Task<EntryStatus> StatusAsync(int id, CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Entries.AsNoTracking().SingleAsync(x => x.Id == id, ct)).Status;
        }

        private sealed class FakeVodLookup : IVodLookup
        {
            public bool Exists { get; set; } = true;
            public bool Throw { get; set; }

            public Task<bool> ExistsAsync(long vodId) =>
                Throw ? Task.FromException<bool>(new HttpRequestException("offline")) : Task.FromResult(Exists);
        }
    }
}
