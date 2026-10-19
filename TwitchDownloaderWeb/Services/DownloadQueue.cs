using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TwitchDownloaderWeb.Services
{
    public enum DownloadJobKind
    {
        /// <summary>Download a stored entry into the library.</summary>
        Store,

        /// <summary>Generate a temporary file for a reference-only entry.</summary>
        Generate
    }

    public readonly record struct DownloadJob(int EntryId, DownloadJobKind Kind);

    /// <summary>
    /// In-memory job queue processed by <see cref="DownloadWorker"/>. The database status is the source of truth;
    /// queued jobs lost on restart are re-queued from it at startup.
    /// </summary>
    public sealed class DownloadQueue
    {
        private readonly Channel<DownloadJob> _channel = Channel.CreateUnbounded<DownloadJob>();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

        public ChannelReader<DownloadJob> Reader => _channel.Reader;

        public void Enqueue(int entryId, DownloadJobKind kind)
        {
            _channel.Writer.TryWrite(new DownloadJob(entryId, kind));
        }

        /// <summary>Cancels the running job for an entry, if any.</summary>
        public void Cancel(int entryId)
        {
            if (!_running.TryGetValue(entryId, out var cts))
                return;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job finished between the lookup and the cancel
            }
        }

        internal CancellationTokenSource BeginJob(int entryId, CancellationToken stoppingToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[entryId] = cts;
            return cts;
        }

        internal void EndJob(int entryId)
        {
            if (_running.TryRemove(entryId, out var cts))
                cts.Dispose();
        }
    }
}
