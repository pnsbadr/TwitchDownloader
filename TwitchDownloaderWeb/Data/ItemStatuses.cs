using System.Text;
using Microsoft.EntityFrameworkCore;

namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// The team's statuses and tags on clips and videos (badrfly 2026-11-20, see <see cref="TeamStatus"/>): the team's first statuses, statuses
    /// that are set by themselves (only ever forward, to a later status), and tag names.
    /// </summary>
    public static class ItemStatuses
    {
        /// <summary>
        /// The team's statuses in order, tracked. A team that never had any gets <see cref="TeamStatus.Defaults"/> (added to the context, saved
        /// with the caller's next save); <see cref="Team.StatusesMadeAtUtc"/> is claimed with a conditional update so two requests can't both.
        /// </summary>
        public static async Task<List<TeamStatus>> ListAsync(LibraryDbContext db, int teamId, CancellationToken cancellationToken)
        {
            var stored = await db.TeamStatuses.IgnoreQueryFilters().Where(x => x.TeamId == teamId).ToListAsync(cancellationToken);
            var all = stored.Concat(db.TeamStatuses.Local.Where(x => x.TeamId == teamId)).Distinct()
                .Where(x => db.Entry(x).State != EntityState.Deleted).ToList();
            if (all.Count == 0)
            {
                var now = DateTime.UtcNow;
                var claimed = await db.Teams.Where(x => x.Id == teamId && x.StatusesMadeAtUtc == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(t => t.StatusesMadeAtUtc, now), cancellationToken);
                if (claimed == 1)
                {
                    all = TeamStatus.Defaults.Select((x, i) => new TeamStatus
                    {
                        TeamId = teamId, Name = x.Name, Color = x.Color, SetWhen = x.SetWhen, Position = i, CreatedAtUtc = now,
                    }).ToList();
                    db.TeamStatuses.AddRange(all);
                }
            }
            return all.OrderBy(x => x.Position).ThenBy(x => x.Id).ToList();
        }

        /// <summary>The status a new clip or video of the team gets (<see cref="StatusTrigger.NewItem"/>), or null.</summary>
        public static async Task<TeamStatus?> ForNewItemAsync(LibraryDbContext db, int teamId, CancellationToken cancellationToken) =>
            (await ListAsync(db, teamId, cancellationToken)).FirstOrDefault(x => x.SetWhen == StatusTrigger.NewItem);

        /// <summary>
        /// Something happened to these clips / videos: those whose status is none or earlier than the status set by <paramref name="trigger"/> get
        /// it. Saved with the caller's next save; not audited (only changes by hand are).
        /// </summary>
        public static async Task AdvanceAsync(LibraryDbContext db, int teamId, StatusTrigger trigger, IEnumerable<int>? entryIds,
            IEnumerable<int>? videoIds, CancellationToken cancellationToken)
        {
            var statuses = await ListAsync(db, teamId, cancellationToken);
            if (statuses.FirstOrDefault(x => x.SetWhen == trigger) is not { } target)
                return;
            bool Behind(int? current) => current is null
                || statuses.FirstOrDefault(x => x.Id == current) is not { } status
                || statuses.IndexOf(status) < statuses.IndexOf(target);

            var entries = entryIds?.Distinct().ToList() ?? [];
            if (entries.Count > 0)
                foreach (var entry in await db.Entries.IgnoreQueryFilters().Where(x => x.TeamId == teamId && entries.Contains(x.Id)).ToListAsync(cancellationToken))
                    if (Behind(entry.TeamStatusId))
                        entry.TeamStatus = target;

            var videos = videoIds?.Distinct().ToList() ?? [];
            if (videos.Count > 0)
                foreach (var video in await db.Videos.IgnoreQueryFilters().Where(x => x.TeamId == teamId && videos.Contains(x.Id)).ToListAsync(cancellationToken))
                    if (Behind(video.TeamStatusId))
                        video.TeamStatus = target;
        }

        /// <summary>
        /// A tag as stored: lower case, no leading '#', spaces become '-', only letters, digits, '-' and '_' kept. Null when nothing is left or
        /// it's too long.
        /// </summary>
        public static string? NormalizeTag(string? text)
        {
            var trimmed = (text ?? "").Trim().TrimStart('#').Trim().ToLowerInvariant();
            var builder = new StringBuilder();
            foreach (var ch in trimmed)
            {
                if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                    builder.Append(ch);
                else if (char.IsWhiteSpace(ch) && builder.Length > 0 && builder[^1] != '-')
                    builder.Append('-');
            }
            var tag = builder.ToString().Trim('-');
            return tag.Length is 0 or > TeamTag.MaxNameLength ? null : tag;
        }

        /// <summary>The team's tags with these names, made when missing (tracked; new ones saved with the caller's next save).</summary>
        public static async Task<List<TeamTag>> TagsNamedAsync(LibraryDbContext db, int teamId, IReadOnlyCollection<string> names, CancellationToken cancellationToken)
        {
            var found = await db.TeamTags.IgnoreQueryFilters().Where(x => x.TeamId == teamId && names.Contains(x.Name)).ToListAsync(cancellationToken);
            foreach (var name in names.Where(n => found.All(t => t.Name != n)))
            {
                var tag = new TeamTag { TeamId = teamId, Name = name, CreatedAtUtc = DateTime.UtcNow };
                db.TeamTags.Add(tag);
                found.Add(tag);
            }
            return names.Select(n => found.First(t => t.Name == n)).ToList();
        }

        /// <summary>Tag names of these clips, by clip ID (alphabetical).</summary>
        public static async Task<Dictionary<int, List<string>>> EntryTagNamesAsync(LibraryDbContext db, IEnumerable<int> entryIds, CancellationToken cancellationToken)
        {
            var ids = entryIds.Distinct().ToList();
            if (ids.Count == 0)
                return [];
            var rows = await db.EntryTags.AsNoTracking().Where(x => ids.Contains(x.EntryId)).Select(x => new { x.EntryId, x.Tag!.Name }).ToListAsync(cancellationToken);
            return rows.GroupBy(x => x.EntryId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Order(StringComparer.Ordinal).ToList());
        }

        /// <summary>Tag names of these videos, by video ID (alphabetical).</summary>
        public static async Task<Dictionary<int, List<string>>> VideoTagNamesAsync(LibraryDbContext db, IEnumerable<int> videoIds, CancellationToken cancellationToken)
        {
            var ids = videoIds.Distinct().ToList();
            if (ids.Count == 0)
                return [];
            var rows = await db.VideoTags.AsNoTracking().Where(x => ids.Contains(x.VideoId)).Select(x => new { x.VideoId, x.Tag!.Name }).ToListAsync(cancellationToken);
            return rows.GroupBy(x => x.VideoId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Order(StringComparer.Ordinal).ToList());
        }
    }
}
