namespace SERLiveMonitoring.Tests;

// SettingsService reads/writes a real file by default; tests point it at a path that doesn't exist
// so they always start from defaults and never touch (or collide over) the real user settings file.
internal static class TestSettingsPath
{
    public static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"ser-live-monitoring-tests-{Guid.NewGuid():N}.json");
}
