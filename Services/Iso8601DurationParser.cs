using System.Text.RegularExpressions;

namespace MeTubeServer.Services;

public static class Iso8601DurationParser
{
    // Parses ISO 8601 duration format (e.g., "PT4M13S" = 4 minutes 13 seconds)
    public static TimeSpan? ParseDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
            return null;

        try
        {
            // Pattern: P[nY][nM][nD][T[nH][nM][nS]]
            var regex = new Regex(@"^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$");
            var match = regex.Match(duration);

            if (!match.Success)
                return null;

            var hours = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
            var minutes = match.Groups[5].Success ? int.Parse(match.Groups[5].Value) : 0;
            var seconds = match.Groups[6].Success ? double.Parse(match.Groups[6].Value) : 0;

            return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }
        catch
        {
            return null;
        }
    }
}
