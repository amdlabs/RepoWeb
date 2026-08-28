using System.Diagnostics;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Vision.Engine;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;

namespace IVZVision.Web.Services;

public sealed record BackfillResult(int Agrupadas, int Descartadas, int Pendientes, string Mensaje);

/// <summary>
/// Agrupa los rostros que ya estaban guardados antes de existir el agrupamiento.
/// El vector facial no se conserva en el histórico, así que se vuelve a calcular a
/// partir del recorte de cada foto y luego se asigna al grupo que le corresponde.
/// Se trabaja a tandas para que la web responda: quien lo lanza ve cuántas quedan.
/// </summary>
public sealed class FaceClusterBackfill
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly RecognitionEngine _engine;
    private readonly FaceClusterIndex _clusters;
    private readonly SnapshotPathResolver _paths;
    private readonly ILogger<FaceClusterBackfill> _logger;

    /// <summary>Tope por tanda: acota tanto el trabajo como el tiempo de respuesta.</summary>
    private const int BatchSize = 200;
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(25);

    public FaceClusterBackfill(IDbContextFactory<VisionDbContext> dbFactory, RecognitionEngine engine,
                               FaceClusterIndex clusters, SnapshotPathResolver paths,
                               ILogger<FaceClusterBackfill> logger)
    {
        _dbFactory = dbFactory;
        _engine = engine;
        _clusters = clusters;
        _paths = paths;
        _logger = logger;
    }

    public async Task<BackfillResult> RunBatchAsync(CancellationToken ct = default)
    {
        var status = _engine.EnsureLoaded();
        if (!status.FacesAvailable)
            return new BackfillResult(0, 0, 0,
                "El reconocimiento facial no está disponible: revise los modelos en Configuración.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var pendientes = await db.RecognitionEvents
            .Where(e => e.FaceClusterId == null && e.Kind == RecognitionKind.Face
                        && (e.CropBase64 != null || e.CropPath != null))
            .OrderByDescending(e => e.OccurredAt)
            .Take(BatchSize)
            .Select(e => new { e.Id, e.CropBase64, e.CropPath })
            .ToListAsync(ct);

        var reloj = Stopwatch.StartNew();
        var agrupadas = 0;
        var descartadas = 0;

        foreach (var evento in pendientes)
        {
            if (ct.IsCancellationRequested || reloj.Elapsed > TimeBudget) break;

            var bytes = await LeerRecorteAsync(evento.CropBase64, evento.CropPath, ct);
            int? grupo = null;

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

            if (grupo is null)
            {
                // Marcarla con el grupo 0 la dejaría fuera de futuras tandas, pero
                // preferimos no tocarla: si más adelante mejora el modelo, se reintenta.
                descartadas++;
                continue;
            }

            await db.RecognitionEvents.Where(e => e.Id == evento.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.FaceClusterId, grupo), ct);
            agrupadas++;
        }

        var quedan = await db.RecognitionEvents
            .CountAsync(e => e.FaceClusterId == null && e.Kind == RecognitionKind.Face
                             && (e.CropBase64 != null || e.CropPath != null), ct);

        _logger.LogInformation("Reproceso de rostros: {Agrupadas} agrupadas, {Descartadas} sin cara reconocible, " +
                               "{Quedan} pendientes", agrupadas, descartadas, quedan);

        var mensaje = agrupadas == 0 && descartadas == 0
            ? "No quedaban fotos anteriores por agrupar."
            : $"Se han agrupado {agrupadas} foto(s)." +
              (descartadas > 0 ? $" Otras {descartadas} no tienen una cara aprovechable y se han dejado como estaban." : "") +
              (quedan > 0 ? $" Quedan {quedan} por procesar: vuelva a pulsar para continuar." : " No queda ninguna pendiente.");

        return new BackfillResult(agrupadas, descartadas, quedan, mensaje);
    }

    /// <summary>Número de fotos de rostro del histórico que todavía no pertenecen a ningún grupo.</summary>
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
