using MudBlazor;

namespace SERLiveMonitoring.Services;

/// <summary>
/// Built-in color themes selectable via the "Theme" key in appsettings.json.
/// </summary>
public static class ThemeCatalog
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string Solarized = "Solarized";

    public static readonly string[] Names = [Dark, Light, Solarized];

    public static (MudTheme Theme, bool IsDarkMode) Resolve(string? name) => name switch
    {
        Light => (LightTheme, false),
        Solarized => (SolarizedTheme, true),
        _ => (DarkTheme, true),
    };

    // MudBlazor's own defaults, used as-is.
    private static readonly MudTheme DarkTheme = new();
    private static readonly MudTheme LightTheme = new();

    // https://ethanschoonover.com/solarized/ - dark background variant.
    private static readonly MudTheme SolarizedTheme = new()
    {
        PaletteDark = new PaletteDark
        {
            Black = "#00212b",
            Background = "#002b36",
            BackgroundGray = "#00212b",
            Surface = "#073642",
            AppbarBackground = "#073642",
            DrawerBackground = "#073642",
            DrawerText = "#93a1a1",
            DrawerIcon = "#93a1a1",
            TextPrimary = "#eee8d5",
            TextSecondary = "#93a1a1",
            Primary = "#268bd2",
            Info = "#2aa198",
            Success = "#859900",
            Warning = "#cb4b16",
            Error = "#dc322f",
            LinesDefault = "#586e75",
            TableLines = "#586e75",
            Divider = "#586e75",
        },
    };
}
