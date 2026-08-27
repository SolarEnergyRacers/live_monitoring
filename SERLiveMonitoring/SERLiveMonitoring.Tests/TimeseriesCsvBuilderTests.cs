using System.Globalization;
using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Tests;

public class TimeseriesCsvBuilderTests
{
    [Fact]
    public void Build_NoSeries_ReturnsHeaderOnly()
    {
        var csv = TimeseriesCsvBuilder.Build(new());

        Assert.Equal("timestamp,\n", csv);
    }

    [Fact]
    public void Build_SingleSeries_WritesOneRowPerPoint()
    {
        var series = new Dictionary<string, List<(long UnixTimestamp, double Value)>>
        {
            ["speed"] = [(100, 10), (101, 20)]
        };

        var csv = TimeseriesCsvBuilder.Build(series);

        Assert.Equal("timestamp,speed\n100,10\n101,20\n", csv);
    }

    [Fact]
    public void Build_SeriesWithDifferentExtents_OuterJoinsAndLeavesGapsBlank()
    {
        // "speed" only has a point at 101, but "battery_power" spans 100-102 - the missing
        // speed values at 100 and 102 must render as blank cells (pandas.read_csv -> NaN),
        // not be dropped from the table or defaulted to 0.
        var series = new Dictionary<string, List<(long UnixTimestamp, double Value)>>
        {
            ["speed"] = [(101, 50)],
            ["battery_power"] = [(100, 500), (101, 600), (102, 700)]
        };

        var csv = TimeseriesCsvBuilder.Build(series);

        Assert.Equal(
            "timestamp,speed,battery_power\n100,,500\n101,50,600\n102,,700\n",
            csv);
    }

    [Fact]
    public void Build_UsesInvariantCultureForDecimals()
    {
        // A comma-decimal locale (e.g. de-DE) would otherwise corrupt the CSV by turning "1.5"
        // into "1,5", colliding with the column separator - run under that culture to actually
        // exercise the bug this guards against, not just whatever culture happens to run the tests.
        var series = new Dictionary<string, List<(long UnixTimestamp, double Value)>>
        {
            ["battery_voltage"] = [(100, 1.5)]
        };

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var csv = TimeseriesCsvBuilder.Build(series);
            Assert.Equal("timestamp,battery_voltage\n100,1.5\n", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
