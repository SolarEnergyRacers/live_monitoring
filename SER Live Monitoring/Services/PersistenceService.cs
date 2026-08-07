namespace SER_Live_Monitoring.Services;

/// <summary>
/// Persists DataManager's timeseries to disk as one append-only binary file per series, so history
/// survives an accidental close or an overnight shutdown instead of starting empty every launch.
/// Format: [4-byte magic "SRTS"][1-byte version][8-byte StartTimestamp, little-endian][doubles...].
/// LastTimestamp/count are never stored - they're derived from file length - so appending new
/// samples is a single sequential write with nothing to rewrite or keep in sync, which keeps a
/// flush cheap no matter how much history has already accumulated.
/// </summary>
public class PersistenceService : IDisposable
{
    private static readonly byte[] Magic = "SRTS"u8.ToArray();
    private const byte FormatVersion = 1;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    private readonly DataManager _dataManager;
    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, int> _flushedCounts = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushLoop;
    private readonly Lock _flushLock = new();
    private string _activeDataDirectory;

    public PersistenceService(DataManager dataManager, SettingsService settingsService)
    {
        _dataManager = dataManager;
        _settingsService = settingsService;

        _activeDataDirectory = DataDirectory;
        LoadAll();

        _flushLoop = Task.Run(() => RunFlushLoopAsync(_cts.Token));
    }

    private string DataDirectory
    {
        get
        {
            var configured = _settingsService.Current.DataDirectory;
            return string.IsNullOrWhiteSpace(configured) ? SettingsService.DefaultDataDirectory : configured;
        }
    }

    // Forces an immediate flush instead of waiting for the next timer tick. Safe to call anytime.
    public void Flush()
    {
        lock (_flushLock)
            FlushAll();
    }

    private void LoadAll()
    {
        var dir = _activeDataDirectory;
        if (!Directory.Exists(dir))
            return;

        foreach (var name in _dataManager.SeriesNames)
        {
            var path = SeriesPath(dir, name);
            if (!File.Exists(path))
                continue;

            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);

                var magic = reader.ReadBytes(Magic.Length);
                if (!magic.AsSpan().SequenceEqual(Magic) || reader.ReadByte() != FormatVersion)
                    continue; // unrecognized/corrupt file - skip rather than fail startup

                var startTimestamp = reader.ReadInt64();
                var points = new List<double>();
                while (stream.Position < stream.Length)
                    points.Add(reader.ReadDouble());

                _dataManager.RestoreSeries(name, startTimestamp, points);
                _flushedCounts[name] = points.Count;
            }
            catch (Exception)
            {
                // Missing/unreadable/corrupt series file - start that series fresh rather than
                // fail application startup over historical data.
            }
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                Flush();
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }

        Flush(); // capture anything added since the last tick
    }

    private void FlushAll()
    {
        var dir = DataDirectory;

        if (dir != _activeDataDirectory)
        {
            // Data directory changed since the last flush (e.g. edited on the Settings page).
            // Appending just the new tail into the new location would leave a file whose header
            // claims data starting at the series' original timestamp but whose body only has the
            // latest few samples - do a full re-dump instead so every file is self-consistent.
            FullDump(dir);
            _activeDataDirectory = dir;
            return;
        }

        foreach (var (name, startTimestamp, newPoints) in _dataManager.DrainNewPoints(_flushedCounts))
            TryWrite(dir, name, startTimestamp, newPoints, append: true);
    }

    private void FullDump(string dir)
    {
        var freshCounts = new Dictionary<string, int>();
        foreach (var (name, startTimestamp, allPoints) in _dataManager.DrainNewPoints(freshCounts))
            TryWrite(dir, name, startTimestamp, allPoints, append: false);

        _flushedCounts.Clear();
        foreach (var (name, count) in freshCounts)
            _flushedCounts[name] = count;
    }

    private static void TryWrite(string dir, string name, long startTimestamp, double[] points, bool append)
    {
        if (points.Length == 0)
            return;

        try
        {
            Directory.CreateDirectory(dir);
            var path = SeriesPath(dir, name);
            var isNewFile = append && !File.Exists(path);

            using var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream);

            if (!append || isNewFile)
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(startTimestamp);
            }

            foreach (var value in points)
                writer.Write(value);
        }
        catch (Exception)
        {
            // Disk full, permissions, etc. - drop this flush rather than crash the flush loop;
            // the next tick retries with whatever's accumulated since.
        }
    }

    private static string SeriesPath(string dir, string name) => Path.Combine(dir, $"{name}.bin");

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _flushLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // best-effort on shutdown
        }
        _cts.Dispose();
    }
}
