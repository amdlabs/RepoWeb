using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Vision.Engine;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;

namespace IVZVision.Web.Services;

/// <summary>Estado del reproceso, para enseñarlo en la pantalla de personas.</summary>
public sealed record BackfillStatus(bool Trabajando, int Pendientes, int Agrupadas, int SinCara, string Mensaje);

/// <summary>
/// Agrupa en segundo plano los rostros guardados antes de existir el agrupamiento.
/// El vector facial no se conserva en el histórico, así que hay que recalcularlo a
/// partir del recorte de cada foto; se hace poco a poco y con pausas para no quitarle
/// tiempo de proceso a las cámaras, y sigue solo hasta terminar sin que nadie lo pida.
/// </summary>
public sealed class FaceClusterBackfill : BackgroundService
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly RecognitionEngine _engine;
    private readonly FaceClusterIndex _clusters;
    private readonly SnapshotPathResolver _paths;
    private readonly IConfigStore _config;
    private readonly ILogger<FaceClusterBackfill> _logger;

    /// <summary>Marca de «revisada y sin cara aprovechable»: evita reintentarla en cada vuelta.</summary>
    private const int SinCara = -1;

    private const int BatchSize = 25;

    /// <summary>Respiro entre tandas: el motor de las cámaras tiene preferencia.</summary>
    private static readonly TimeSpan PausaEntreTandas = TimeSpan.FromSeconds(3);

    /// <summary>Cada cuánto se vuelve a mirar si ha aparecido trabajo nuevo.</summary>
    private static readonly TimeSpan PausaSinTrabajo = TimeSpan.FromMinutes(2);

    private volatile bool _trabajando;
    private int _agrupadas;
    private int _sinCara;
    private int _pendientes;
    private string _mensaje = "En espera.";

    public FaceClusterBackfill(IDbContextFactory<VisionDbContext> dbFactory, RecognitionEngine engine,
                               FaceClusterIndex clusters, SnapshotPathResolver paths, IConfigStore config,
                               ILogger<FaceClusterBackfill> logger)
    {
        _dbFactory = dbFactory;
        _engine = engine;
        _clusters = clusters;
        _paths = paths;
        _config = config;
        _logger = logger;
    }

    public BackfillStatus Status => new(_trabajando, _pendientes, _agrupadas, _sinCara, _mensaje);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reproceso de rostros en marcha; primera tanda en 30 s");

        // Un margen al arrancar: primero que se levanten las cámaras.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var espera = PausaSinTrabajo;

            try
            {
                // Si el motor está apagado desde el panel, el reproceso también descansa.
                if (_config.Current.EngineEnabled)
                {
                    var hechas = await ProcesarTandaAsync(stoppingToken);
                    if (hechas > 0) espera = PausaEntreTandas;
                }
                else
                {
                    _mensaje = "El motor está apagado: el reproceso se reanudará al encenderlo.";
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallo en el reproceso de rostros; se reintentará");
                _mensaje = $"Se reintentará: {ex.Message}";
            }
            finally
            {
                _trabajando = false;
            }

            try { await Task.Delay(espera, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Procesa una tanda y devuelve cuántas fotos se han mirado.</summary>
    private async Task<int> ProcesarTandaAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        _pendientes = await db.RecognitionEvents
            .CountAsync(e => e.FaceClusterId == null && e.Kind == RecognitionKind.Face
                             && (e.CropBase64 != null || e.CropPath != null), ct);

        if (_pendientes == 0)
        {
            _mensaje = "Todas las fotos anteriores están agrupadas.";
            return 0;
        }

        var status = _engine.EnsureLoaded();
        if (!status.FacesAvailable)
        {
            _mensaje = "El reconocimiento facial no está disponible: revise los modelos en Configuración.";
            _logger.LogWarning("Reproceso de rostros en espera: detector={Detector}, embebedor={Embebedor}, " +
                               "motivo={Motivo}", status.FaceDetectorReady, status.FaceEmbedderReady,
                               status.FaceError ?? "sin detalle");
            return 0;
        }

        _logger.LogInformation("Reproceso de rostros: {Pendientes} pendiente(s), procesando tanda de {Tanda}",
                               _pendientes, BatchSize);

        _trabajando = true;

        var tanda = await db.RecognitionEvents
            .Where(e => e.FaceClusterId == null && e.Kind == RecognitionKind.Face
                        && (e.CropBase64 != null || e.CropPath != null))
            .OrderByDescending(e => e.OccurredAt)
            .Take(BatchSize)
            .Select(e => new { e.Id, e.CropBase64, e.CropPath })
            .ToListAsync(ct);

        foreach (var evento in tanda)
        {
            if (ct.IsCancellationRequested) break;

            int? grupo = null;
            var bytes = await LeerRecorteAsync(evento.CropBase64, evento.CropPath, ct);

            if (bytes is not null)
            {
                using var imagen = Cv2.ImDecode(bytes, ImreadModes.Color);
                if (!imagen.Empty())
                {
                    var facial = _engine.EnrollFace(imagen);
                    if (facial.Success && facial.Embedding is not null)
                        grupo = await _clusters.AssignAsync(facial.Embedding, ct);
                }
            }

            var valor = grupo ?? SinCara;
            await db.RecognitionEvents.Where(e => e.Id == evento.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.FaceClusterId, valor), ct);

            if (grupo is null) _sinCara++;
            else _agrupadas++;
        }

        _pendientes = Math.Max(0, _pendientes - tanda.Count);
        _logger.LogInformation("Tanda terminada: {Agrupadas} agrupadas y {SinCara} sin cara aprovechable en total; " +
                               "quedan {Pendientes}", _agrupadas, _sinCara, _pendientes);
        _mensaje = _pendientes > 0
            ? $"Agrupando fotos anteriores: quedan {_pendientes}."
            : "Todas las fotos anteriores están agrupadas.";

        return tanda.Count;
    }

    /// <summary>
    /// Deshace todos los grupos y deja las fotos listas para volver a agruparse.
    /// Se usa tras cambiar el umbral de agrupamiento, para que el criterio nuevo se
    /// aplique también a lo ya visto. El trabajo lo retoma el propio servicio.
    /// </summary>
    public async Task<int> ReagruparTodoAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var afectadas = await db.RecognitionEvents
            .Where(e => e.FaceClusterId != null)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.FaceClusterId, (int?)null), ct);

        await db.FaceClusters.ExecuteDeleteAsync(ct);
        _clusters.MarkDirty();

        _agrupadas = 0;
        _sinCara = 0;
        _mensaje = "Reagrupando desde cero…";
        _logger.LogInformation("Reagrupamiento solicitado: {Count} foto(s) vuelven a la cola", afectadas);

        return afectadas;
    }

    /// <summary>Fotos de rostro del histórico que todavía no se han mirado.</summary>
    public async Task<int> PendingCountAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.RecognitionEvents
            .CountAsync(e => e.FaceClusterId == null && e.Kind == RecognitionKind.Face
                             && (e.CropBase64 != null || e.CropPath != null), ct);
    }

    private async Task<byte[]?> LeerRecorteAsync(string? base64, string? ruta, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(base64))
        {
            try { return Convert.FromBase64String(base64); }
            catch (FormatException) { return null; }
        }

        if (string.IsNullOrEmpty(ruta)) return null;

        var completa = _paths.Resolve(ruta);
        if (completa is null || !File.Exists(completa)) return null;

        return await File.ReadAllBytesAsync(completa, ct);
    }
}
