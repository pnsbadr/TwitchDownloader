using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <summary>
    /// How staff see a team: "Channel (team name)" (badrfly 2026-11-19), since teams pick their own names. The channel is the team's first
    /// one still assigned, else its first one; a team with no channel shows just its name.
    /// </summary>
    public static class TeamLabels
    {
        public static string Label(string teamName, IEnumerable<SavedChannel> channels)
        {
            var first = channels.OrderBy(x => x.AccessEndedAtUtc is null ? 0 : 1).ThenBy(x => x.AddedAtUtc).ThenBy(x => x.Id).FirstOrDefault();
            return first is null ? teamName : $"{first.DisplayName} ({teamName})";
        }

        /// <summary>Labels of these teams (staff code: every team's channels).</summary>
        public static async Task<Dictionary<int, string>> GetAsync(LibraryDbContext db, IEnumerable<int> teamIds, CancellationToken cancellationToken)
        {
            var ids = teamIds.Distinct().ToList();
            if (ids.Count == 0)
                return [];
            var names = await db.Teams.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
            var channels = (await db.Channels.IgnoreQueryFilters().AsNoTracking().Where(x => ids.Contains(x.TeamId)).ToListAsync(cancellationToken))
                .ToLookup(x => x.TeamId);
            return names.ToDictionary(x => x.Key, x => Label(x.Value, channels[x.Key]));
        }
    }
}
