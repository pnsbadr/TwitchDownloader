using System.Net;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>
    /// Saves VOD audio while Twitch still serves unmuted copies. For each VOD: download every segment of the audio-only
    /// rendition (the clean copy for segments already muted), keep fetching new segments while the stream is live, then
    /// after AudioRescue:PruneAfterHours keep only the segments that ended up muted. Grabbing everything early means
    /// segments Twitch mutes later are covered too.
    ///
    /// VODs come from saved channels with WatchMutedAudio (checked every AudioRescue:PollMinutes) or from
    /// POST /api/vods/{id}/rescue. The database is the source of truth, so unfinished work resumes after a restart.
    /// </summary>
    public sealed class AudioRescueService : BackgroundService
    {
        private const int HeadBytes = 256 * 1024;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ChannelService _channels;
        private readonly LibraryStorage _storage;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IVodPlaylistSource _playlists;
        private readonly IOptions<AudioRescueSettings> _settings;
        private readonly ILogger<AudioRescueService> _logger;
        private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private DateTime _nextDiscoveryUtc = DateTime.MinValue;

        public AudioRescueService(IServiceScopeFactory scopeFactory, ChannelService channels, LibraryStorage storage, IHttpClientFactory httpClientFactory,
            IVodPlaylistSource playlists, IOptions<AudioRescueSettings> settings, ILogger<AudioRescueService> logger)
        {
            _scopeFactory = scopeFactory;
            _channels = channels;
            _storage = storage;
            _httpClientFactory = httpClientFactory;
            _playlists = playlists;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>Process due rescues now instead of at the next minute tick (e.g. after a manual request).</summary>
        public void Wake() => _wake.Writer.TryWrite(true);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Turned off: unfinished rescues stay as they are and resume when it is turned back on
                    if (_settings.Value.Enabled)
                    {
                        if (DateTime.UtcNow >= _nextDiscoveryUtc)
                        {
                            _nextDiscoveryUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.Value.PollMinutes));
                            await DiscoverAsync(stoppingToken);
                        }

                        await ProcessDueAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audio rescue pass failed.");
                }

                using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                tick.CancelAfter(TimeSpan.FromMinutes(1));
                try
                {
                    await _wake.Reader.ReadAsync(tick.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Minute tick
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Starts rescues for saved channels' recent VODs that are still inside the unmute window.</summary>
        private async Task DiscoverAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

            var logins = await db.Channels.AsNoTracking().Where(x => x.WatchMutedAudio).Select(x => x.Login).ToListAsync(cancellationToken);
            foreach (var login in logins)
            {
                ChannelVodPage? page;
                try
                {
                    page = await _channels.GetVodsAsync(login, VodType.Archive, null, 5);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("Couldn't list VODs for {Login}: {Message}", login, ex.Message);
                    continue;
                }

                foreach (var vod in page?.Items.Where(x => x.UnmuteWindowOpen) ?? [])
                {
                    if (await db.AudioRescues.AnyAsync(x => x.VodId == vod.VodId, cancellationToken))
                        continue;

                    AddRescue(db, vod.VodId, login, vod.Title, vod.CreatedAtUtc, "Saved channel watch");
                    _logger.LogInformation("Started audio rescue for VOD {VodId} ({Login}).", vod.VodId, login);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        /// <returns>The new rescue (not yet saved).</returns>
        public static VodAudioRescue AddRescue(LibraryDbContext db, long vodId, string? channelLogin, string? title, DateTime streamStartUtc, string reason, string? userId = null)
        {
            var now = DateTime.UtcNow;
            var rescue = new VodAudioRescue
            {
                VodId = vodId,
                ChannelLogin = channelLogin,
                Title = title,
                StreamStartUtc = streamStartUtc,
                Status = AudioRescueStatus.Grabbing,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                NextCheckAtUtc = now,
                RequestedByUserId = userId,
            };
            db.AudioRescues.Add(rescue);
            db.Audit(AuditActions.AudioRescueStarted, null, $"VOD {vodId} ({channelLogin ?? "unknown channel"}): {reason}", userId);
            return rescue;
        }

        /// <summary>Runs every rescue step that is due, one VOD at a time. Called by the background loop (and by tests).</summary>
        public async Task ProcessDueAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

                var now = DateTime.UtcNow;
                var rescue = await db.AudioRescues
                    .Where(x => (x.Status == AudioRescueStatus.Grabbing || x.Status == AudioRescueStatus.WaitingToPrune) && x.NextCheckAtUtc <= now)
                    .OrderBy(x => x.NextCheckAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                if (rescue is null)
                    return;

                try
                {
                    if (rescue.Status == AudioRescueStatus.Grabbing)
                        await GrabAsync(db, rescue, cancellationToken);
                    else
                        await PruneAsync(db, rescue, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Transient (network, Twitch hiccup): try again shortly. Not a token-bearing message: URLs aren't included.
                    _logger.LogWarning(ex, "Audio rescue step for VOD {VodId} failed; retrying later.", rescue.VodId);
                    rescue.Error = ex.Message;
                    rescue.NextCheckAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.Value.LiveRecheckMinutes));
                    rescue.UpdatedAtUtc = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(cancellationToken);
            }
        }

        private async Task GrabAsync(LibraryDbContext db, VodAudioRescue rescue, CancellationToken cancellationToken)
        {
            var http = _httpClientFactory.CreateClient(nameof(AudioRescueService));
            var rendition = await _playlists.GetAudioRenditionAsync(http, rescue.VodId, cancellationToken);
            if (rendition is null)
            {
                Fail(db, rescue, "The VOD is no longer available on Twitch, or is restricted (e.g. subscriber-only).");
                return;
            }

            if (rendition.Segments.Any(x => !x.Path.Contains(".ts", StringComparison.OrdinalIgnoreCase)))
            {
                Fail(db, rescue, "This VOD doesn't use MPEG-TS segments (e.g. AV1), which audio rescue doesn't support yet.");
                return;
            }

            var existing = await db.RescuedAudioSegments.Where(x => x.VodId == rescue.VodId).ToDictionaryAsync(x => x.Key, cancellationToken);
            var pending = rendition.Segments
                .Where(s => !existing.TryGetValue(s.Key, out var row) || (row.FilePath is null && !row.Unrecoverable))
                .ToArray();

            Directory.CreateDirectory(_storage.RescueVodFolder(rescue.VodId));
            var results = new System.Collections.Concurrent.ConcurrentBag<(VodSegment Segment, string? Path, long Size, long? Pts, bool Unrecoverable)>();
            var failures = 0;

            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _settings.Value.Threads), CancellationToken = cancellationToken },
                async (segment, ct) =>
                {
                    try
                    {
                        results.Add(await DownloadSegmentAsync(http, rendition, rescue.VodId, segment, ct));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref failures);
                        _logger.LogDebug("Segment {Key} of VOD {VodId} failed: {Message}", segment.Key, rescue.VodId, ex.Message);
                    }
                });

            var now = DateTime.UtcNow;
            foreach (var (segment, path, size, pts, unrecoverable) in results)
            {
                if (!existing.TryGetValue(segment.Key, out var row))
                {
                    row = new RescuedAudioSegment { VodId = rescue.VodId, Key = segment.Key };
                    db.RescuedAudioSegments.Add(row);
                    existing[segment.Key] = row;
                }

                row.OffsetSeconds = segment.Offset;
                row.DurationSeconds = segment.Duration;
                row.WasMuted = segment.IsMuted;
                row.Unrecoverable = unrecoverable;
                row.FilePath = path;
                row.SizeBytes = size;
                row.FirstAudioPts = pts;
                row.SavedAtUtc = now;
            }

            // Segments muted since an earlier pass keep their saved (clean) audio; record that they're muted now
            foreach (var segment in rendition.Segments.Where(x => x.IsMuted))
            {
                if (existing.TryGetValue(segment.Key, out var row))
                    row.WasMuted = true;
            }

            var rows = existing.Values;
            rescue.SourceQuality = rendition.QualityName;
            rescue.SegmentCount = rendition.Segments.Count;
            rescue.SavedCount = rows.Count(x => x.FilePath is not null);
            rescue.MutedCount = rows.Count(x => x.WasMuted);
            rescue.UnrecoverableCount = rows.Count(x => x.Unrecoverable);
            rescue.StoredBytes = rows.Sum(x => x.SizeBytes);
            rescue.Error = failures > 0 ? $"{failures} segment(s) failed to download; retrying." : null;
            rescue.UpdatedAtUtc = now;

            var settings = _settings.Value;
            if (rendition.IsLive || failures > 0)
            {
                rescue.NextCheckAtUtc = now.AddMinutes(Math.Max(1, settings.LiveRecheckMinutes));
                return;
            }

            var streamEnd = rescue.StreamStartUtc.AddSeconds((double)rendition.Segments.Sum(x => x.Duration));
            rescue.Status = AudioRescueStatus.WaitingToPrune;
            rescue.GrabCompletedAtUtc = now;
            rescue.PruneAfterUtc = (streamEnd > now ? streamEnd : now).AddHours(settings.PruneAfterHours);
            rescue.NextCheckAtUtc = rescue.PruneAfterUtc.Value;

            db.Audit(AuditActions.AudioRescueSaved, null,
                $"VOD {rescue.VodId}: {rescue.SavedCount}/{rescue.SegmentCount} segments saved ({rescue.StoredBytes / (1024 * 1024)} MB), " +
                $"{rescue.MutedCount} muted, {rescue.UnrecoverableCount} unrecoverable");
            _logger.LogInformation("Saved audio for VOD {VodId}: {Saved}/{Total} segments, {Muted} muted, {Unrecoverable} unrecoverable.",
                rescue.VodId, rescue.SavedCount, rescue.SegmentCount, rescue.MutedCount, rescue.UnrecoverableCount);
        }

        private async Task<(VodSegment, string?, long, long?, bool)> DownloadSegmentAsync(HttpClient http, VodRendition rendition, long vodId, VodSegment segment,
            CancellationToken cancellationToken)
        {
            // For a muted segment, ask for the clean copy; Twitch answers 403 (or 404) once it's gone
            using var response = await http.GetAsync(rendition.SegmentUri(segment.UnmutedPath), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (segment.IsMuted && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return (segment, null, 0, null, true);

            response.EnsureSuccessStatusCode();

            var relativePath = LibraryStorage.RescuedSegmentRelativePath(vodId, segment.Key);
            var fullPath = _storage.Resolve(relativePath);
            var tempPath = fullPath + ".part";
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(file, cancellationToken);

            File.Move(tempPath, fullPath, true);

            var size = new FileInfo(fullPath).Length;
            var pts = await ReadTimestampsAsync(fullPath, cancellationToken);
            return (segment, relativePath, size, pts.FirstAudioPts, false);
        }

        public static async Task<TsTimestamps> ReadTimestampsAsync(string path, CancellationToken cancellationToken)
        {
            await using var file = File.OpenRead(path);
            var buffer = new byte[Math.Min(file.Length, HeadBytes)];
            await file.ReadExactlyAsync(buffer, cancellationToken);
            return TsTimestamps.Read(buffer);
        }

        /// <summary>Keeps only the clean audio of segments muted in the current playlist.</summary>
        private async Task PruneAsync(LibraryDbContext db, VodAudioRescue rescue, CancellationToken cancellationToken)
        {
            var http = _httpClientFactory.CreateClient(nameof(AudioRescueService));
            var rendition = await _playlists.GetAudioRenditionAsync(http, rescue.VodId, cancellationToken);
            var mutedKeys = rendition?.Segments.Where(x => x.IsMuted).Select(x => x.Key).ToHashSet();

            var rows = await db.RescuedAudioSegments.Where(x => x.VodId == rescue.VodId).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                // A VOD that's gone can't be downloaded again, so its audio is no use either
                var keep = mutedKeys is not null && (mutedKeys.Contains(row.Key) || row.WasMuted);
                if (keep)
                {
                    row.WasMuted = true;
                    continue;
                }

                if (row.FilePath is null || _storage.TryDelete(row.FilePath))
                    db.RescuedAudioSegments.Remove(row);
            }

            var kept = rows.Where(x => db.Entry(x).State != EntityState.Deleted).ToList();
            rescue.Status = AudioRescueStatus.Pruned;
            rescue.SavedCount = kept.Count(x => x.FilePath is not null);
            rescue.MutedCount = kept.Count;
            rescue.UnrecoverableCount = kept.Count(x => x.Unrecoverable);
            rescue.StoredBytes = kept.Sum(x => x.SizeBytes);
            rescue.Error = mutedKeys is null ? "The VOD was no longer available at prune time, so its audio was removed." : null;
            rescue.UpdatedAtUtc = DateTime.UtcNow;

            db.Audit(AuditActions.AudioRescuePruned, null,
                $"VOD {rescue.VodId}: kept {rescue.SavedCount} muted segment(s) ({rescue.StoredBytes / (1024 * 1024)} MB)");
        }

        private static void Fail(LibraryDbContext db, VodAudioRescue rescue, string message)
        {
            rescue.Status = AudioRescueStatus.Failed;
            rescue.Error = message;
            rescue.UpdatedAtUtc = DateTime.UtcNow;
            db.Audit(AuditActions.AudioRescueFailed, null, $"VOD {rescue.VodId}: {message}");
        }
    }
}
