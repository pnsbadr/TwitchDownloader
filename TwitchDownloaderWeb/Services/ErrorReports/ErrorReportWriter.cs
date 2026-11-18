using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.ErrorReports
{
    /// <summary>
    /// Saves queued <see cref="ErrorReport"/>s, filling in names (user, team, staff) and, for background jobs, who the job was for (the
    /// entry's adder, the export's or segment download's creator). Deletes reports older than <c>ErrorReports:KeepDays</c> (90) and keeps
    /// at most <c>ErrorReports:MaxReports</c> (20 000), checked hourly.
    /// </summary>
    public sealed class ErrorReportWriter(ErrorReportSink sink, IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ErrorReportWriter> logger)
        : BackgroundService
    {
        private DateTime _lastPrune = DateTime.MinValue;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var batch = new List<ErrorReport>();
            try
            {
                while (await sink.Reader.WaitToReadAsync(stoppingToken))
                {
                    // A moment for a burst to arrive together, then one save
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
                    while (batch.Count < 200 && sink.Reader.TryRead(out var report))
                        batch.Add(report);
                    await SaveAsync(batch, stoppingToken);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down: save what's left without waiting
                while (sink.Reader.TryRead(out var report))
                    batch.Add(report);
                if (batch.Count > 0)
                    await SaveAsync(batch, CancellationToken.None);
            }
        }

        /// <summary>Saves the reports now (tests, and the loop above).</summary>
        public async Task SaveAsync(IReadOnlyList<ErrorReport> reports, CancellationToken cancellationToken)
        {
            if (reports.Count == 0)
                return;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                await FillAsync(db, reports, cancellationToken);
                db.ErrorReports.AddRange(reports);
                await db.SaveChangesAsync(cancellationToken);
                if (DateTime.UtcNow - _lastPrune > TimeSpan.FromHours(1))
                {
                    _lastPrune = DateTime.UtcNow;
                    await PruneAsync(db, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Warning, not Error: an error here would be reported again
                logger.LogWarning("Couldn't save {Count} error reports: {Kind}", reports.Count, ex.GetType().Name);
            }
        }

        private static async Task FillAsync(LibraryDbContext db, IReadOnlyList<ErrorReport> reports, CancellationToken cancellationToken)
        {
            // Background jobs: the person the job was for
            var entryIds = reports.Where(x => x.UserId is null && x.EntryId is not null).Select(x => x.EntryId!.Value).Distinct().ToList();
            if (entryIds.Count > 0)
            {
                var entries = await db.Entries.IgnoreQueryFilters().AsNoTracking().Where(x => entryIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.AddedByUserId, x.TeamId, x.VodId }).ToDictionaryAsync(x => x.Id, cancellationToken);
                foreach (var report in reports.Where(x => x.UserId is null && x.EntryId is not null))
                {
                    if (!entries.TryGetValue(report.EntryId!.Value, out var entry))
                        continue;
                    report.UserId = int.TryParse(entry.AddedByUserId, out var adder) ? adder : null;
                    report.TeamId ??= entry.TeamId;
                    report.VodId ??= entry.VodId > 0 ? entry.VodId : null;
                }
            }
            var exportIds = reports.Where(x => x.UserId is null && x.ExportId is not null).Select(x => x.ExportId!.Value).Distinct().ToList();
            if (exportIds.Count > 0)
            {
                var exports = await db.ExportBatches.IgnoreQueryFilters().AsNoTracking().Where(x => exportIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.CreatedByUserId, x.TeamId }).ToDictionaryAsync(x => x.Id, cancellationToken);
                foreach (var report in reports.Where(x => x.UserId is null && x.ExportId is not null))
                    if (exports.TryGetValue(report.ExportId!.Value, out var export))
                        (report.UserId, report.TeamId) = (export.CreatedByUserId, report.TeamId ?? export.TeamId);
            }
            var segmentIds = reports.Where(x => x.UserId is null && x.SegmentDownloadId is not null).Select(x => x.SegmentDownloadId!.Value).Distinct().ToList();
            if (segmentIds.Count > 0)
            {
                var downloads = await db.SegmentExports.IgnoreQueryFilters().AsNoTracking().Where(x => segmentIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.CreatedByUserId, x.TeamId, x.VodId }).ToDictionaryAsync(x => x.Id, cancellationToken);
                foreach (var report in reports.Where(x => x.UserId is null && x.SegmentDownloadId is not null))
                    if (downloads.TryGetValue(report.SegmentDownloadId!.Value, out var download))
                        (report.UserId, report.TeamId, report.VodId) = (download.CreatedByUserId, report.TeamId ?? download.TeamId, report.VodId ?? download.VodId);
            }

            // Names, copied so the report still says who after an account or team is gone
            var userIds = reports.Where(x => x.UserId is not null && x.Username is null).Select(x => x.UserId!.Value).Distinct().ToList();
            var users = userIds.Count == 0 ? [] : await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
            var teamIds = reports.Where(x => x.TeamId is not null && x.TeamName is null).Select(x => x.TeamId!.Value).Distinct().ToList();
            var teams = teamIds.Count == 0 ? [] : await db.Teams.AsNoTracking().Where(x => teamIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
            var staffIds = reports.Where(x => x.StaffId is not null && x.StaffUsername is null).Select(x => x.StaffId!.Value).Distinct().ToList();
            var staff = staffIds.Count == 0 ? [] : await db.StaffAccounts.AsNoTracking().Where(x => staffIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
            foreach (var report in reports)
            {
                if (report.UserId is { } u && report.Username is null)
                    report.Username = users.GetValueOrDefault(u);
                if (report.TeamId is { } t && report.TeamName is null)
                    report.TeamName = teams.GetValueOrDefault(t);
                if (report.StaffId is { } s && report.StaffUsername is null)
                    report.StaffUsername = staff.GetValueOrDefault(s);
            }
        }

        private async Task PruneAsync(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var keepDays = Math.Max(1, configuration.GetValue("ErrorReports:KeepDays", 90));
            var maxReports = Math.Max(100, configuration.GetValue("ErrorReports:MaxReports", 20_000));
            var cutoff = DateTime.UtcNow.AddDays(-keepDays);
            await db.ErrorReports.Where(x => x.CreatedAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
            var newestKept = await db.ErrorReports.OrderByDescending(x => x.Id).Skip(maxReports - 1).Select(x => (long?)x.Id).FirstOrDefaultAsync(cancellationToken);
            if (newestKept is { } oldestKeptId)
                await db.ErrorReports.Where(x => x.Id < oldestKeptId).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
