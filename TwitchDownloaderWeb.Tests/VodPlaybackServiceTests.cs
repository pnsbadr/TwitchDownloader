using Microsoft.Extensions.Caching.Memory;
using TwitchDownloaderWeb.Services.Playback;

namespace TwitchDownloaderWeb.Tests
{
    public class VodPlaybackServiceTests
    {
        private sealed class SlowSource : IVodPlaybackSource
        {
            public int Calls;
            public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<PlaybackVariants> GetVariantsAsync(long vodId, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                await Release.Task;
                return PlaybackVariants.NotFound;
            }
        }

        [Fact]
        public async Task CallsAtOnceShareOneTwitchLookup()
        {
            var source = new SlowSource();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var playback = new VodPlaybackService(source, cache, null!);
            var ct = TestContext.Current.CancellationToken;

            var calls = Enumerable.Range(0, 3).Select(_ => playback.GetVariantsAsync(42, ct)).ToList();
            source.Release.SetResult();
            foreach (var call in calls)
                Assert.Same(PlaybackVariants.NotFound, await call);
            Assert.Equal(1, source.Calls);

            // Then it comes from the cache
            await playback.GetVariantsAsync(42, ct);
            Assert.Equal(1, source.Calls);
        }

        [Fact]
        public async Task OneCallerGivingUpDoesntCancelTheOthers()
        {
            var source = new SlowSource();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var playback = new VodPlaybackService(source, cache, null!);
            using var leaving = new CancellationTokenSource();

            var left = playback.GetVariantsAsync(7, leaving.Token);
            var staying = playback.GetVariantsAsync(7, TestContext.Current.CancellationToken);
            leaving.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => left);
            source.Release.SetResult();
            Assert.Same(PlaybackVariants.NotFound, await staying);
            Assert.Equal(1, source.Calls);
        }
    }
}
