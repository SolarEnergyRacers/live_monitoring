using System.Globalization;
using SERLiveMonitoring.Models;

namespace SERLiveMonitoring.Services;

public sealed record GoogleMapsTrackExport(
    string Url,
    int SourceCount,
    int ExportedCount,
    int GapPoints,
    int GapSeconds,
    bool ReducedByUrlLimit);

public static class GoogleMapsTrackBuilder
{
    private const string BaseUrl = "https://www.google.com/maps/dir/";
    public const int DefaultMaxUrlLength = 1900;

    public static GoogleMapsTrackExport Build(IReadOnlyList<GpsPoint> sourcePoints, int maxUrlLength = DefaultMaxUrlLength)
    {
        if (sourcePoints.Count == 0)
            return new GoogleMapsTrackExport(BaseUrl, 0, 0, 0, 0, false);

        if (sourcePoints.Count == 1)
        {
            var singleUrl = BuildUrl(sourcePoints);
            return new GoogleMapsTrackExport(singleUrl, 1, 1, 1, 1, false);
        }

        var fullUrl = BuildUrl(sourcePoints);
        if (fullUrl.Length <= maxUrlLength)
        {
            return new GoogleMapsTrackExport(
                fullUrl,
                sourcePoints.Count,
                sourcePoints.Count,
                1,
                1,
                false);
        }

        var sampled = FindLargestSampleWithinLimit(sourcePoints, maxUrlLength);
        var sampledUrl = BuildUrl(sampled);
        var gapPoints = ComputeGapPoints(sourcePoints.Count, sampled.Count);

        return new GoogleMapsTrackExport(
            sampledUrl,
            sourcePoints.Count,
            sampled.Count,
            gapPoints,
            gapPoints,
            true);
    }

    private static List<GpsPoint> FindLargestSampleWithinLimit(IReadOnlyList<GpsPoint> sourcePoints, int maxUrlLength)
    {
        var low = 2;
        var high = sourcePoints.Count;
        var best = new List<GpsPoint> { sourcePoints[0], sourcePoints[^1] };

        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var candidate = DownsampleEvenly(sourcePoints, mid);
            var candidateUrl = BuildUrl(candidate);

            if (candidateUrl.Length <= maxUrlLength)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static List<GpsPoint> DownsampleEvenly(IReadOnlyList<GpsPoint> points, int targetCount)
    {
        if (targetCount >= points.Count)
            return [.. points];

        if (targetCount <= 1)
            return [points[0]];

        var result = new List<GpsPoint>(targetCount);
        var maxIndex = points.Count - 1;

        for (var i = 0; i < targetCount; i++)
        {
            var index = (int)Math.Floor(i * (double)maxIndex / (targetCount - 1));
            result.Add(points[index]);
        }

        result[^1] = points[^1];
        return result;
    }

    private static int ComputeGapPoints(int sourceCount, int exportedCount)
    {
        if (sourceCount <= 1 || exportedCount <= 1)
            return 1;

        return Math.Max(1, (int)Math.Ceiling((sourceCount - 1d) / (exportedCount - 1d)));
    }

    private static string BuildUrl(IReadOnlyList<GpsPoint> points)
    {
        return BaseUrl + string.Join("/", points.Select(FormatWaypoint));
    }

    private static string FormatWaypoint(GpsPoint point)
    {
        var lat = point.Latitude.ToString("0.000000", CultureInfo.InvariantCulture);
        var lon = point.Longitude.ToString("0.000000", CultureInfo.InvariantCulture);
        return $"{lat},{lon}";
    }
}
