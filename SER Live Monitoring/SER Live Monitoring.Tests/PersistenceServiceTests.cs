using SERLiveMonitoring.Models;
using SERLiveMonitoring.Services;
using Microsoft.Extensions.Configuration;

namespace SERLiveMonitoring.Tests;

public class PersistenceServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ser-live-monitoring-tests-{Guid.NewGuid():N}");
        _tempDirs.Add(dir);
        return dir;
    }

    private static DataManager NewDataManager(SettingsService settings)
        => new(new SerialPortMonitorService(new CANFrameDecoder(settings)));

    private static IConfiguration NewStorageConfig(string dataDir)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataDirectory"] = dataDir
            })
            .Build();

    private static Reading NewReading(DateTime ts, string name, double value, params (string Key, string Value)[] tags)
        => new() { Timestamp = ts, ReadingName = name, Value = value, Unit = "", Tags = tags.ToDictionary(t => t.Key, t => t.Value) };

    [Fact]
    public void Flush_ThenLoadInNewInstance_RestoresSeries()
    {
        var dataDir = NewTempDir();
        var settings = new SettingsService(TestSettingsPath.NewTempPath());
        var config = NewStorageConfig(dataDir);

        var now = DateTime.Now;
        var dataManager = NewDataManager(settings);
        dataManager.Ingest(
        [
            NewReading(now.AddSeconds(-2), "speed", 10),
            NewReading(now.AddSeconds(-1), "speed", 20),
            NewReading(now, "speed", 30),
        ]);

        using (var persistence = new PersistenceService(dataManager, config))
            persistence.Flush();

        Assert.True(File.Exists(Path.Combine(dataDir, "speed.bin")));

        // Fresh DataManager/PersistenceService pair pointed at the same directory, simulating an
        // app restart - PersistenceService's constructor loads history before anything else runs.
        var reloadedManager = NewDataManager(settings);
        using var reloadedPersistence = new PersistenceService(reloadedManager, config);

        var avg = reloadedManager.GetAverage(ChartSeries.Speed, TimeSpan.FromSeconds(15));

        Assert.NotNull(avg);
        Assert.Equal(20, avg!.Value);
    }

    [Fact]
    public void Load_CorruptFile_StartsThatSeriesFreshWithoutThrowing()
    {
        var dataDir = NewTempDir();
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "speed.bin"), [1, 2, 3]); // too short / bad magic

        var settings = new SettingsService(TestSettingsPath.NewTempPath());
        var config = NewStorageConfig(dataDir);

        var dataManager = NewDataManager(settings);
        using var persistence = new PersistenceService(dataManager, config);

        Assert.Null(dataManager.GetAverage(ChartSeries.Speed, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void UsesConfiguredDataDirectory_ForWritesAndReload()
    {
        var dataDir = NewTempDir();
        var settings = new SettingsService(TestSettingsPath.NewTempPath());
        var config = NewStorageConfig(dataDir);

        var now = DateTime.Now;
        var dataManager = NewDataManager(settings);
        dataManager.Ingest([NewReading(now, "speed", 42)]);

        using var persistence = new PersistenceService(dataManager, config);
        persistence.Flush();
        Assert.True(File.Exists(Path.Combine(dataDir, "speed.bin")));

        var reloadedManager = NewDataManager(settings);
        using var reloadedPersistence = new PersistenceService(reloadedManager, config);
        Assert.Equal(42, reloadedManager.GetAverage(ChartSeries.Speed, TimeSpan.FromSeconds(15)));
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
