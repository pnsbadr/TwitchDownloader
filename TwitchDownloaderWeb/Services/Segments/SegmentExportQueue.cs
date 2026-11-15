using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TwitchDownloaderWeb.Services.Segments
{
    /// <summary>
    /// In-memory queue of segment downloads (by export ID), processed by <see cref="SegmentExportWorker"/>. The database status is the
    /// source of truth; anything queued or running at shutdown is queued again at startup.
    /// </summary>
    public sealed class SegmentExportQueue : IdJobQueue;

    /// <summary>
    /// In-memory queue of export batches (by batch ID), processed by <see cref="ExportWorker"/>. The database status is the source of truth;
    /// anything queued or running at shutdown is queued again at startup.
    /// </summary>
    public sealed class ExportQueue : IdJobQueue;

    /// <summary>A queue of jobs by row ID, with cancellation of the running one (the row was deleted).</summary>
    public abstract class IdJobQueue
    {
        private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

        public ChannelReader<int> Reader => _channel.Reader;

        public void Enqueue(int exportId) => _channel.Writer.TryWrite(exportId);

        /// <summary>Stops the export's job if it is running (it was deleted).</summary>
        public void Cancel(int exportId)
        {
            if (!_running.TryGetValue(exportId, out var cts))
                return;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Finished between the lookup and the cancel
            }
        }

        public bool IsRunning(int exportId) => _running.ContainsKey(exportId);

        internal CancellationTokenSource BeginJob(int exportId, CancellationToken stoppingToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[exportId] = cts;
            return cts;
        }

        internal void EndJob(int exportId)
        {
            if (_running.TryRemove(exportId, out var cts))
                cts.Dispose();
        }
    }
}
