using IVZVision.Core.Configuration;
using IVZVision.Data;
using Microsoft.AspNetCore.DataProtection;
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

// ---- Primer arranque en contenedor -----------------------------------------
// Si no existe el fichero de configuración y hay variables de entorno de base de
// datos (docker-compose), se genera una configuración inicial que apunta a ellas.
if (!File.Exists(settingsFile))
{
    var envServer = Environment.GetEnvironmentVariable("IVZVISION_DB_SERVER");
    if (!string.IsNullOrWhiteSpace(envServer))
    {
        var seed = new AppConfig();
        seed.Database.Server = envServer;
        seed.Database.Database = Environment.GetEnvironmentVariable("IVZVISION_DB_NAME") ?? "IVZVision";
        seed.Database.IntegratedSecurity = false;
        seed.Database.UserId = Environment.GetEnvironmentVariable("IVZVISION_DB_USER") ?? "sa";
        seed.Database.Password = Environment.GetEnvironmentVariable("IVZVISION_DB_PASSWORD") ?? "";
        seed.Database.TrustServerCertificate = true;

        SecretProtector.ProtectSecrets(seed);
        File.WriteAllText(settingsFile,
            System.Text.Json.JsonSerializer.Serialize(seed, ConfigJson.Options));
    }
}

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
// Las claves que firman las cookies (sesión y antiforgery) se guardan junto a la
// configuración: en Docker esa carpeta es un volumen, así que las sesiones y los
// formularios sobreviven a reinicios y recreaciones del contenedor.
var keysDir = Path.Combine(Path.GetDirectoryName(settingsFile)!, "claves-dataprotection");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("IVZVision");

builder.Services
    .AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(options =>
{
    // Los valores coinciden con SystemUserRole.ToString().
    options.AddPolicy("Administrador", p => p.RequireRole("Administrator"));
    options.AddPolicy("Operador", p => p.RequireRole("Administrator", "Operator"));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AddPageRoute("/Directo", "");

    // Toda la web exige sesión iniciada, salvo el login y la página de error.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Error");

    // Reparto por roles: configuración y usuarios sólo para administradores;
    // las altas y ediciones del padrón para operadores o superiores.
    options.Conventions.AuthorizeFolder("/Configuracion", "Administrador");
    options.Conventions.AuthorizePage("/Usuarios", "Administrador");
    options.Conventions.AuthorizePage("/Persona", "Operador");
    options.Conventions.AuthorizePage("/Vehiculo", "Operador");
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
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapHub<DetectionHub>("/hubs/detecciones");

// Deja el índice y los modelos preparados sin bloquear el arranque del servidor.
_ = Task.Run(() =>
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<RecognitionEngine>().EnsureLoaded();
});

// Cada guardado de configuración deja también una copia histórica en la base de
// datos (con las contraseñas cifradas), como respaldo de la configuración de dispositivos.
{
    var store = app.Services.GetRequiredService<IConfigStore>();
    var dbFactory = app.Services.GetRequiredService<IDbContextFactory<VisionDbContext>>();
    var snapshotLogger = app.Services.GetRequiredService<ILogger<Program>>();

    store.Changed += (_, cfg) => _ = Task.Run(async () =>
    {
        try
        {
            var copy = cfg.Clone();
            SecretProtector.ProtectSecrets(copy);

            await using var db = await dbFactory.CreateDbContextAsync();
            db.ConfigSnapshots.Add(new IVZVision.Data.Entities.ConfigSnapshot
            {
                Json = System.Text.Json.JsonSerializer.Serialize(copy, ConfigJson.Options),
            });
            await db.SaveChangesAsync();

            // Se conservan las últimas 50 copias.
            var stale = db.ConfigSnapshots.OrderByDescending(s => s.SavedAt).Skip(50);
            db.ConfigSnapshots.RemoveRange(stale);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            snapshotLogger.LogDebug(ex, "No se pudo guardar la copia de configuración en la base de datos");
        }
    });
}

app.Run();
