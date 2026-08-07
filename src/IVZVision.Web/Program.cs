using IVZVision.Core.Configuration;
using IVZVision.Data;
using Microsoft.AspNetCore.DataProtection;
using IVZVision.Vision.Engine;
using IVZVision.Vision.Pipeline;
using IVZVision.Web.Hubs;
using IVZVision.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Permite ejecutar la aplicación como servicio de Windows (scripts/instalar-servicio.ps1).
// Fuera de un servicio esta llamada no cambia nada.
builder.Host.UseWindowsService(options => options.ServiceName = "IVZVision");

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
// La cadena de conexión puede venir como parámetro de despliegue (web.config en IIS,
// appsettings.json o la variable de entorno ConnectionStrings__IVZVision): si está
// definida, manda sobre lo que haya en la configuración editable.
var forcedConnectionString = builder.Configuration.GetConnectionString("IVZVision");

// La base de datos es la copia autoritativa de la configuración (tabla AppConfiguration);
// el fichero JSON queda como caché de arranque.
builder.Services.AddSingleton<IConfigStore>(sp => new DbConfigStore(
    new JsonFileConfigStore(settingsFile, sp.GetRequiredService<ILogger<JsonFileConfigStore>>()),
    forcedConnectionString,
    sp.GetRequiredService<ILogger<DbConfigStore>>()));

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

// La validación de cada formulario es manual y por tipo de cámara: sin esto,
// los campos string no anulables (Host, URL RTSP…) serían obligatorios siempre,
// incluso ocultos (p. ej. al guardar una cámara USB).
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

// Documentación pública de la API REST (Swagger UI en /api/docs).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "IVZ Vision API",
        Version = "v1",
        Description = "API JSON para integraciones: lista de cámaras configuradas y " +
                      "detecciones (objetos, rostros y matrículas) de cada cámara. " +
                      "Autenticación: sesión del navegador o cabecera X-Api-Key " +
                      "(clave definida en Configuración → Seguridad de la API).",
    });

    options.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "Clave de la API definida en Configuración → Seguridad de la API.",
    });
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Tras un proxy inverso con HTTPS (Caddy, nginx, IIS) la aplicación debe ver el
// esquema y la IP reales del cliente. Sólo se confía en proxies de la propia máquina.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger(options => options.RouteTemplate = "api/docs/{documentName}/swagger.json");
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "api/docs";
    options.SwaggerEndpoint("/api/docs/v1/swagger.json", "IVZ Vision API v1");
    options.DocumentTitle = "IVZ Vision · API";
});

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
