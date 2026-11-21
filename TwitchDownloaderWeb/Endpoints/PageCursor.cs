using System.Globalization;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <summary>
    /// Where a page of a long list ended (the library's clips and videos, badrfly 2026-11-20): the last row's sort date and ID, written
    /// "ticks_id". The next page asks for the rows after it in the same order, so clips added meanwhile don't shift or repeat pages.
    /// </summary>
    internal readonly record struct PageCursor(DateTime AtUtc, int Id)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{AtUtc.Ticks}_{Id}");

        public static bool TryParse(string? text, out PageCursor? cursor)
        {
            cursor = null;
            var parts = text?.Split('_');
            if (parts is not { Length: 2 }
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || ticks > DateTime.MaxValue.Ticks)
                return false;
            cursor = new PageCursor(new DateTime(ticks, DateTimeKind.Utc), id);
            return true;
        }
    }

    /// <summary>
    /// One page of a long list: the rows, the cursor for the next page (null = last page), how many match in all and, when asked
    /// (?days=&lt;time zone&gt;), how many match on each day without the from/to bounds (the library's calendar).
    /// </summary>
    internal sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, int Total, IReadOnlyList<DayCount>? Days = null);
}
