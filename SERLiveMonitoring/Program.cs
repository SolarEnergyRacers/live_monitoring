using SERLiveMonitoring.Components;
using SERLiveMonitoring.Endpoints;
using SERLiveMonitoring.Services;
using MudBlazor.Services;
using ApexCharts;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();
builder.Services.AddApexCharts();

// Lets a GPS-reporting device on a different origin (e.g. a phone browser page, not a native app)
// POST to /api/gps. Wide open since this is a trusted-network tool with no auth anywhere else.
builder.Services.AddCors(options =>
{
    options.AddPolicy(GpsEndpoints.GpsCorsPolicy, policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<IDataDecoder, CANFrameDecoder>();
builder.Services.AddSingleton<SerialPortMonitorService>();
builder.Services.AddSingleton<DataManager>();
builder.Services.AddSingleton<PersistenceService>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var dataDir = PersistenceService.ResolveDataDirectory(config);
    return new EventTimestampService(Path.Combine(dataDir, "events.db"));
});
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var dataDir = PersistenceService.ResolveDataDirectory(config);
    var dataManager = sp.GetRequiredService<DataManager>();
    return new GpsTrackService(dataManager, Path.Combine(dataDir, "gps.db"));
});
builder.Services.AddSingleton<AnalyticsDataService>();
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

// Opens/creates the GPS track database and restores prior history into DataManager now, rather
// than lazily on the first API call.
app.Services.GetRequiredService<GpsTrackService>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.UseCors();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapGpsEndpoints();

app.Run();
