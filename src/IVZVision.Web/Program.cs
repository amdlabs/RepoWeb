using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Vision.Engine;
using IVZVision.Vision.Pipeline;
using IVZVision.Web.Hubs;
using IVZVision.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var contentRoot = builder.Environment.ContentRootPath;
var settingsFile = builder.Configuration["IVZVision:SettingsFile"] ?? "App_Data/ivzvision.settings.json";
if (!Path.IsPathRooted(settingsFile))
    settingsFile = Path.Combine(contentRoot, settingsFile);

Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);

// ---- Configuración persistente (editable desde la web) --------------------
builder.Services.AddSingleton<IConfigStore>(sp =>
    new JsonFileConfigStore(settingsFile, sp.GetRequiredService<ILogger<JsonFileConfigStore>>()));

// ---- Datos ---------------------------------------------------------------
builder.Services.AddSingleton<IDbContextFactory<VisionDbContext>, VisionDbContextFactory>();
builder.Services.AddSingleton<DatabaseProvisioner>();
builder.Services.AddSingleton<KnownSubjectsIndex>();
builder.Services.AddSingleton<EventRecorder>();

// ---- Motor de visión -----------------------------------------------------
builder.Services.AddSingleton(sp => new RecognitionEngine(
    sp.GetRequiredService<IConfigStore>(),
    sp.GetRequiredService<KnownSubjectsIndex>(),
    sp.GetRequiredService<ILoggerFactory>(),
    contentRoot));

builder.Services.AddSingleton<FrameBroadcaster>();
builder.Services.AddSingleton<IObservationSink, SignalRObservationSink>();

builder.Services.AddSingleton(sp => new CameraPipelineManager(
    sp.GetRequiredService<IConfigStore>(),
    sp.GetRequiredService<RecognitionEngine>(),
    sp.GetRequiredService<EventRecorder>(),
    sp.GetRequiredService<KnownSubjectsIndex>(),
    sp.GetRequiredService<DatabaseProvisioner>(),
    sp.GetRequiredService<FrameBroadcaster>(),
    sp.GetServices<IObservationSink>(),
    sp.GetRequiredService<ILoggerFactory>(),
    contentRoot));

builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraPipelineManager>());

builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<SnapshotPathResolver>();
builder.Services.AddSingleton<DiagnosticsService>();

// ---- Web -----------------------------------------------------------------
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AddPageRoute("/Directo", "");
});
builder.Services.AddControllers();
builder.Services.AddSignalR();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapControllers();
app.MapHub<DetectionHub>("/hubs/detecciones");

// Deja el índice y los modelos preparados sin bloquear el arranque del servidor.
_ = Task.Run(() =>
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<RecognitionEngine>().EnsureLoaded();
});

app.Run();
