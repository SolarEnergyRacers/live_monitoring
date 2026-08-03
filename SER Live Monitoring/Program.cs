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

builder.Services.AddSingleton<IDataDecoder, CANFrameDecoder>();
builder.Services.AddSingleton<SerialPortMonitorService>();
builder.Services.AddSingleton<DataManager>();
//builder.Services.AddSingleton<IReadingCache>(sp => sp.GetRequiredService<DataManager>());

var app = builder.Build();

// Force the cache to instantiate now so it starts buffering readings immediately, not on first page visit.
app.Services.GetRequiredService<DataManager>();

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
