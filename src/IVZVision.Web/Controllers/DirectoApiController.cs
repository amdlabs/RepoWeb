using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Vision.Pipeline;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Controllers;

/// <summary>
/// Datos de arranque de la vista en directo (cámaras, estados y detecciones
/// recientes). Requiere sesión: lo consume live.js, no las integraciones externas.
/// </summary>
[ApiController]
[Authorize]
[Route("api/directo")]
public class DirectoApiController : ControllerBase
{
    private readonly IConfigStore _config;
    private readonly CameraPipelineManager _pipeline;
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;

    public DirectoApiController(IConfigStore config, CameraPipelineManager pipeline,
                                IDbContextFactory<VisionDbContext> dbFactory)
    {
        _config = config;
        _pipeline = pipeline;
        _dbFactory = dbFactory;
    }

    [HttpGet("estado")]
    public async Task<IActionResult> Estado(CancellationToken ct)
    {
        var camaras = _config.Current.Cameras
            .Where(c => c.Enabled)
            .Select(c => new { id = c.Id.ToString(), nombre = c.Name })
            .ToList();

        var estados = _pipeline.Statuses.Select(s => new CameraStatusDto(
            s.CameraId.ToString(), s.Name, s.Connected, s.State, s.LastError,
            s.MeasuredFps,
            s.FrameWidth > 0 ? $"{s.FrameWidth}×{s.FrameHeight}" : "—",
            s.FramesProcessed)).ToList();

        // El búfer en memoria se vacía al reiniciar el servicio; si está corto se
        // completa con el histórico para que los paneles nunca aparezcan vacíos.
        var recientes = _pipeline.GetRecentObservations(take: 30)
            .Select(SignalRObservationSink.ToDto)
            .ToList();

        if (recientes.Count < 30)
            recientes.AddRange(await LeerHistoricoAsync(30 - recientes.Count, recientes, ct));

        return Ok(new { camaras, estados, recientes });
    }

    /// <summary>Últimas detecciones guardadas, con el mismo formato que las que llegan por SignalR.</summary>
    private async Task<List<ObservationDto>> LeerHistoricoAsync(int take, List<ObservationDto> yaPresentes,
                                                                CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var eventos = await db.RecognitionEvents
                .AsNoTracking()
                .OrderByDescending(e => e.OccurredAt)
                .Take(take + yaPresentes.Count)
                .Select(e => new
                {
                    e.Id, e.Kind, e.CameraId, e.CameraName, e.OccurredAt, e.Label, e.PlateText,
                    e.ObjectClass, e.IsKnown, e.IsAuthorized, e.DetectionScore, e.MatchScore,
                    e.OcrConfidence, e.CropBase64, e.CropPath,
                })
                .ToListAsync(ct);

            var vistos = yaPresentes.Select(o => o.EventoId).Where(id => id.HasValue).ToHashSet();

            return eventos
                .Where(e => !vistos.Contains(e.Id))
                .Take(take)
                .Select(e => new ObservationDto(
                    e.Id.ToString(),
                    e.Kind switch
                    {
                        RecognitionKind.Plate => "matricula",
                        RecognitionKind.Object => "objeto",
                        RecognitionKind.Text => "texto",
                        _ => "rostro",
                    },
                    e.CameraId.ToString(),
                    e.CameraName,
                    e.OccurredAt.ToLocalTime().ToString("HH:mm:ss"),
                    e.Label,
                    e.PlateText,
                    e.IsKnown,
                    e.IsAuthorized,
                    (int)Math.Round(e.DetectionScore * 100),
                    (int)Math.Round((e.Kind == RecognitionKind.Plate ? (e.OcrConfidence ?? 0) : e.MatchScore) * 100),
                    e.CropBase64 is not null
                        ? $"data:image/jpeg;base64,{e.CropBase64}"
                        : e.CropPath is not null
                            ? $"/media/recorte?path={Uri.EscapeDataString(e.CropPath)}"
                            : null,
                    e.Id,
                    e.ObjectClass))
                .ToList();
        }
        catch (Exception)
        {
            // Sin base de datos los paneles siguen funcionando en vivo.
            return new List<ObservationDto>();
        }
    }
}
