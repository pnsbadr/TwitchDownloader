using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <summary>How many of a list's items fall on one day of the viewer's time zone (the library's calendar, badrfly 2026-11-21).</summary>
    internal sealed record DayCount(string Day, int Count);

    /// <summary>
    /// Shared bits of the library's clip and video lists (badrfly 2026-11-21, the Details view): several members in the Made by / Edited by
    /// filters, sorting by name, length or maker (paged by offset), the calendar's day counts and who made edits of each item.
    /// </summary>
    internal static class LibraryListing
    {
        public const int MaxMembers = 50;

        /// <summary>"3,7" → [3, 7]; blank → null.</summary>
        public static (List<int>? Ids, IResult? Problem) ParseMembers(string? text, string field)
        {
            if (string.IsNullOrWhiteSpace(text))
                return (null, null);
            var ids = new List<int>();
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    return (null, Results.ValidationProblem(new Dictionary<string, string[]> { [field] = ["Use member IDs separated by commas."] }));
                if (!ids.Contains(id))
                    ids.Add(id);
            }
            if (ids.Count > MaxMembers)
                return (null, Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [$"At most {MaxMembers} members."] }));
            return (ids.Count == 0 ? null : ids, null);
        }

        /// <summary>A time zone by IANA (or Windows) ID; blank → null, unknown → a problem.</summary>
        public static (TimeZoneInfo? Zone, IResult? Problem) ParseZone(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return (null, null);
            try
            {
                return (TimeZoneInfo.FindSystemTimeZoneById(id.Trim()), null);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Use a time zone like America/Los_Angeles."] }));
            }
        }

        /// <summary>Counts the instants per day in <paramref name="zone"/>, newest day first.</summary>
        public static List<DayCount> CountDays(IEnumerable<DateTime> instants, TimeZoneInfo zone) =>
            instants
                .Select(x => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .GroupBy(x => x)
                .OrderByDescending(x => x.Key, StringComparer.Ordinal)
                .Select(x => new DayCount(x.Key, x.Count()))
                .ToList();

        /// <summary>A page of a list sorted by something other than a date: "o_200" = skip the first 200.</summary>
        public static bool TryParseOffset(string? cursor, out int offset)
        {
            offset = 0;
            return cursor is not null && cursor.StartsWith("o_", StringComparison.Ordinal)
                   && int.TryParse(cursor.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out offset);
        }

        public static string OffsetCursor(int offset) => string.Create(CultureInfo.InvariantCulture, $"o_{offset}");

        /// <summary>
        /// The members who made edits of these clips or videos (their usernames, first edit first). Teammates' vertical edits are only
        /// told to members who may see them (<see cref="VerticalAccess"/>); horizontal edits are everyone's.
        /// </summary>
        public static async Task<Dictionary<int, List<string>>> EditorsAsync(LibraryDbContext db, TeamContext team, IReadOnlyCollection<int> ids,
            bool videos, CancellationToken cancellationToken)
        {
            if (ids.Count == 0)
                return [];
            var canSee = VerticalAccess.CanSeeTeam(team);
            var me = team.UserId;
            var projects = db.Projects.AsNoTracking().Where(p => canSee || !p.Vertical || p.UserId == me);
            var rows = videos
                ? await projects.Where(p => p.VideoId != null && ids.Contains(p.VideoId.Value))
                    .Select(p => new { ItemId = p.VideoId!.Value, p.UserId, p.CreatedAtUtc }).ToListAsync(cancellationToken)
                : await projects.Where(p => p.EntryId != null && ids.Contains(p.EntryId.Value))
                    .Select(p => new { ItemId = p.EntryId!.Value, p.UserId, p.CreatedAtUtc }).ToListAsync(cancellationToken);
            var userIds = rows.Select(x => x.UserId).Distinct().ToList();
            var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancellationToken);
            return rows.GroupBy(x => x.ItemId).ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.CreatedAtUtc).Select(x => names.GetValueOrDefault(x.UserId)).OfType<string>().Distinct().ToList());
        }
    }
}
