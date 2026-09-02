namespace SERLiveMonitoring.Endpoints;

// Shared "from"/"to" parsing for GET .../range endpoints (GpsEndpoints, TimeseriesEndpoints).
// Honors an explicit offset/"Z" when given, otherwise assumes the server's local time zone,
// and accepts shortened prefixes (day/hour/minute precision), padding whatever is missing with
// zero - i.e. flooring to the start of that unit - since from/to mark the outer limits of the
// range rather than an exact instant. The result is always converted to UTC.
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

        // Full ISO 8601 - e.g. 2026-09-02T19:43:04+00:00 (offset honored) or
        // 2026-09-02T19:43:04 (no offset, assumed local).
        if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed.UtcDateTime;

        // Shortened prefix with no offset - e.g. 2026-09-02T19 or 2026-09-02 - assumed local.
        if (DateTime.TryParseExact(raw, ShortenedFormats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var floored))
            return DateTime.SpecifyKind(floored, DateTimeKind.Local).ToUniversalTime();

        return null;
    }
}
