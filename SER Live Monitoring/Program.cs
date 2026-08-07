using SER_Live_Monitoring.Components;
using SER_Live_Monitoring.Services;
using MudBlazor.Services;
using ApexCharts;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();
builder.Services.AddApexCharts();

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<IDataDecoder, CANFrameDecoder>();
builder.Services.AddSingleton<SerialPortMonitorService>();
builder.Services.AddSingleton<DataManager>();
builder.Services.AddSingleton<PersistenceService>();
builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<SettingsService>();
    var dataDir = string.IsNullOrWhiteSpace(settings.Current.DataDirectory)
        ? SettingsService.DefaultDataDirectory
        : settings.Current.DataDirectory;
    return new EventTimestampService(Path.Combine(dataDir, "events.db"));
});
//builder.Services.AddSingleton<IReadingCache>(sp => sp.GetRequiredService<DataManager>());

var app = builder.Build();

// Load settings from disk before anything else needs them.
app.Services.GetRequiredService<SettingsService>();

// Force the cache to instantiate now so it starts buffering readings immediately, not on first page visit.
app.Services.GetRequiredService<DataManager>();

// Restores persisted timeseries history and starts periodic flushing. Must run before the user can
// hit Connect on the Home page, so restored data is in place before any live reading arrives.
app.Services.GetRequiredService<PersistenceService>();

// Opens/creates the event timestamps database now rather than on first page visit.
app.Services.GetRequiredService<EventTimestampService>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
