using System.Globalization;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Parses and formats trim offsets as "h:mm:ss[.fff]". Unlike TimeSpan's own format, hours are not capped at 23,
    /// since VODs can be up to 48 hours long.
    /// </summary>
    public static class TrimTime
    {
        /// <summary>Accepts "h:mm:ss", "m:ss" or plain seconds, each with optional fractional seconds. Blank parses to null.</summary>
        public static bool TryParse(string? text, out TimeSpan? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var parts = text.Trim().Split(':');
            if (parts.Length > 3)
                return false;

            if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
                return false;

            var totalSeconds = seconds;
            var multiplier = 60;
            for (var i = parts.Length - 2; i >= 0; i--)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var unit))
                    return false;

                totalSeconds += unit * multiplier;
                multiplier *= 60;
            }

            // Minutes and seconds must be < 60 when a larger unit is present
            if (parts.Length > 1 && seconds >= 60)
                return false;
            if (parts.Length == 3 && int.Parse(parts[1], CultureInfo.InvariantCulture) >= 60)
                return false;

            // Rejects "NaN"/"Infinity" and values large enough to overflow TimeSpan; no VOD is longer than a week
            if (!double.IsFinite(totalSeconds) || totalSeconds > TimeSpan.FromDays(7).TotalSeconds)
                return false;

            value = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }

        public static string Format(TimeSpan time)
        {
            var formatted = $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
            return time.Milliseconds > 0 ? $"{formatted}.{time.Milliseconds:000}" : formatted;
        }

        public static string? Format(TimeSpan? time) => time is { } t ? Format(t) : null;
    }
}
