using System.Collections.Concurrent;

namespace TwitchDownloaderWeb.Services.Sync
{
    /// <summary>
    /// Sound waves for Player 2's pieces and the side-by-side sync view (badrfly 2026-11-20): a VOD's loudness in blocks of
    /// <see cref="BlockSeconds"/>, <see cref="AudioAligner.WaveRate"/> levels a second, read from its audio-only rendition
    /// (<see cref="IVodLoudness.GetLevelsAsync"/>). Full blocks are kept as "waves/{vodId}/{block}.bin" (a VOD's sound never changes once
    /// recorded; the write time is touched when served, and ones not shown for <see cref="KeepFor"/> are deleted by the cleanup service).
    /// A block cut short (the end of the VOD, or a live one still recording) is kept in memory for a minute only.
    /// </summary>
    public sealed class VodWaveService(IVodLoudness loudness, LibraryStorage storage, ILogger<VodWaveService> logger)
    {
        public const string FolderName = "waves";
        public const int BlockSeconds = 60;
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
        private static readonly TimeSpan ShortKeep = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan FailureKeep = TimeSpan.FromMinutes(5);

        private readonly SemaphoreSlim _running = new(2);
        private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _inFlight = new();
        private readonly ConcurrentDictionary<string, (byte[]? Levels, DateTime Until)> _short = new();

        /// <summary>The block's levels (empty = nothing recorded there), or null when the audio can't be read.</summary>
        public async Task<byte[]?> GetBlockAsync(long vodId, int block, CancellationToken cancellationToken)
        {
            var key = $"{vodId}/{block}";
            var path = Path.Combine(storage.RootFolder, FolderName, vodId.ToString(), $"{block}.bin");
            if (File.Exists(path))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    return bytes;
                }
                catch (IOException) { }
            }
            if (_short.TryGetValue(key, out var kept) && kept.Until > DateTime.UtcNow)
                return kept.Levels;

            // One read per block at a time, whoever asks; the read itself isn't cancelled by one page leaving
            var task = _inFlight.GetOrAdd(key, _ => new Lazy<Task<byte[]?>>(() => ReadAsync(vodId, block, key, path))).Value;
            try
            {
                return await task.WaitAsync(cancellationToken);
            }
            finally
            {
                if (task.IsCompleted)
                    _inFlight.TryRemove(key, out _);
            }
        }

        private async Task<byte[]?> ReadAsync(long vodId, int block, string key, string path)
        {
            await _running.WaitAsync();
            try
            {
                byte[]? levels;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    levels = await loudness.GetLevelsAsync(vodId, (double)block * BlockSeconds, BlockSeconds, timeout.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or IOException)
                {
                    logger.LogWarning("Sound wave of VOD {VodId} block {Block} couldn't be read: {Error}", vodId, block, ex.GetType().Name);
                    levels = null;
                }

                if (levels is not null && levels.Length >= BlockSeconds * AudioAligner.WaveRate - AudioAligner.WaveRate / 2)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        var temp = path + ".tmp";
                        await File.WriteAllBytesAsync(temp, levels);
                        File.Move(temp, path, true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning("Sound wave of VOD {VodId} couldn't be saved: {Error}", vodId, ex.Message);
                    }
                }
                else
                {
                    _short[key] = (levels, DateTime.UtcNow + (levels is null ? FailureKeep : ShortKeep));
                }
                return levels;
            }
            finally
            {
                _running.Release();
                _inFlight.TryRemove(key, out _);
                foreach (var (k, v) in _short)
                    if (v.Until < DateTime.UtcNow)
                        _short.TryRemove(k, out _);
            }
        }

        /// <summary>Deletes blocks not shown for <see cref="KeepFor"/> (and leftovers of interrupted writes).</summary>
        public void Cleanup()
        {
            var root = Path.Combine(storage.RootFolder, FolderName);
            if (!Directory.Exists(root))
                return;
            var cutoff = DateTime.UtcNow - KeepFor;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    if (written < cutoff || (file.EndsWith(".tmp", StringComparison.Ordinal) && written < DateTime.UtcNow.AddHours(-1)))
                        storage.TryDelete(file);
                }
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
