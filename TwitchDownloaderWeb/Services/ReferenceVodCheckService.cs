using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Endpoints;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Every Downloads:VodCheckHours, checks the VODs of reference-only entries on Twitch. Entries whose VOD is gone become
    /// VodUnavailable ("VOD deleted" on the page, no Generate), so nobody finds out by a failed generate; they switch back to
    /// ReferenceOnly if the VOD shows up again (a streamer can unpublish and republish). Entries are never deleted.
    /// </summary>
    public sealed class ReferenceVodCheckService : BackgroundService
    {
        public const string VodGoneMessage = "The VOD is no longer available on Twitch.";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IVodLookup _vods;
        private readonly IOptions<DownloadSettings> _settings;
        private readonly ILogger<ReferenceVodCheckService> _logger;

        public ReferenceVodCheckService(IServiceScopeFactory scopeFactory, IVodLookup vods, IOptions<DownloadSettings> settings,
            ILogger<ReferenceVodCheckService> logger)
        {
            _scopeFactory = scopeFactory;
            _vods = vods;
            _settings = settings;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Reference VOD check failed.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromHours(Math.Max(1, _settings.Value.VodCheckHours)), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var maxStored = _settings.Value.MaxStoredClipSeconds;

            // Reference entries, plus ones already marked gone (too long to be stored, so they were reference entries)
            var entries = await db.Entries
                .Where(x => x.Status == EntryStatus.ReferenceOnly
                            || (x.Status == EntryStatus.VodUnavailable && x.FilePath == null && x.ClipLengthSeconds > maxStored))
                .ToListAsync(cancellationToken);

            var now = DateTime.UtcNow;
            foreach (var group in entries.GroupBy(x => x.VodId))
            {
                bool exists;
                try
                {
                    exists = await _vods.ExistsAsync(group.Key);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("Couldn't check whether VOD {VodId} still exists: {Message}", group.Key, ex.Message);
                    continue;
                }

                foreach (var entry in group)
                {
                    if (!exists && entry.Status == EntryStatus.ReferenceOnly)
                    {
                        // A ready file still works without the VOD, and a running job finds out by itself; mark them later
                        if (EntryDto.HasUnexpiredGeneratedFile(entry) || entry.GenerationStatus is GenerationStatus.Queued or GenerationStatus.Generating)
                            continue;

                        entry.Status = EntryStatus.VodUnavailable;
                        entry.GenerationStatus = GenerationStatus.None;
                        entry.Error = VodGoneMessage;
                        entry.UpdatedAtUtc = now;
                        db.Audit(AuditActions.EntryVodGone, entry.Id, $"VOD {entry.VodId} is no longer on Twitch");
                    }
                    else if (exists && entry.Status == EntryStatus.VodUnavailable)
                    {
                        entry.Status = EntryStatus.ReferenceOnly;
                        entry.Error = null;
                        entry.UpdatedAtUtc = now;
                        db.Audit(AuditActions.EntryVodBack, entry.Id, $"VOD {entry.VodId} is on Twitch again");
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
