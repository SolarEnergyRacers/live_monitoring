using System.Text.Json;
using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

/// <summary>
/// Loads and persists application settings (CAN bus addresses, warning thresholds) to a JSON file
/// under %LocalAppData%, and is the single source of truth other services read from so changes made
/// on the settings page take effect immediately without a restart.
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Default location for persisted timeseries data (see PersistenceService) when AppSettings.DataDirectory
    // is left blank. Lives next to settings.json so both stay under the same app-data root by default.
    public static string DefaultDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SER Live Monitoring", "data");

    private readonly Lock _lock = new();
    private readonly string _filePath;
    private AppSettings _current;

    public event Action? SettingsChanged;

    // filePath is overridable so tests don't read/write the real user settings file.
    public SettingsService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SER Live Monitoring", "settings.json");
        _current = Load();
    }

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public void Update(AppSettings settings)
    {
        lock (_lock)
        {
            _current = settings;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
        }

        SettingsChanged?.Invoke();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath), JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (Exception)
        {
            // Missing, unreadable, or corrupt settings file - fall back to defaults rather than
            // fail application startup over it.
        }

        return new AppSettings();
    }
}
