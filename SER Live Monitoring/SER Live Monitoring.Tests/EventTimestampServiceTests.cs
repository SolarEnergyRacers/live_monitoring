using SER_Live_Monitoring.Services;

namespace SER_Live_Monitoring.Tests;

public class EventTimestampServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ser-live-monitoring-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public void Add_ThenGetAll_ReturnsNewestFirst()
    {
        using var service = new EventTimestampService(_dbPath);
        var now = DateTime.Now;

        service.Add("charge start", now.AddMinutes(-10));
        service.Add("driver change", now);

        var events = service.GetAll();

        Assert.Equal(2, events.Count);
        Assert.Equal("driver change", events[0].Name);
        Assert.Equal("charge start", events[1].Name);
    }

    [Fact]
    public void Delete_RemovesEvent()
    {
        using var service = new EventTimestampService(_dbPath);
        var added = service.Add("charge stop", DateTime.Now);

        service.Delete(added.Id);

        Assert.Empty(service.GetAll());
    }

    [Fact]
    public void ReopeningSameDbPath_RestoresPreviouslyAddedEvents()
    {
        var timestamp = DateTime.Now;
        using (var service = new EventTimestampService(_dbPath))
            service.Add("driver change", timestamp);

        using var reopened = new EventTimestampService(_dbPath);
        var events = reopened.GetAll();

        Assert.Single(events);
        Assert.Equal("driver change", events[0].Name);
        Assert.Equal(timestamp.ToString("o"), events[0].Timestamp.ToString("o"));
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }
}
