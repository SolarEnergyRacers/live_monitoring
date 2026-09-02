namespace SERLiveMonitoring.Endpoints;

// Shared "from"/"to" parsing for GET .../range endpoints (GpsEndpoints, TimeseriesEndpoints).
// Always treats the result as UTC and accepts shortened prefixes (day/hour/minute precision),
// padding whatever is missing with zero - i.e. flooring to the start of that unit - since from/to
// mark the outer limits of the range rather than an exact instant.
internal static class UtcRangeTimestampParser
{
    private static readonly string[] ShortenedFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH",
        "yyyy-MM-dd"
    ];

    public static DateTime? Parse(string raw)
    {
        raw = raw.Trim();

        // Full ISO 8601, with offset or "Z" - e.g. 2026-09-02T19:43:04+00:00.
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

        // Shortened prefix with no offset - e.g. 2026-09-02T19 or 2026-09-02.
        if (DateTime.TryParseExact(raw, ShortenedFormats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var floored))
            return DateTime.SpecifyKind(floored, DateTimeKind.Utc);

        return null;
    }
}
