using System.ComponentModel;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Data.Search;
using IVZVision.Vision.Pipeline;
using IVZVision.Web.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace IVZVision.Web.Mcp;

/// <summary>
/// Herramientas MCP que exponen el procesamiento visual a otros asistentes de IA.
///
/// El reparto de trabajo es deliberado: aquí se ofrecen operaciones estructuradas y
/// deterministas (qué se ve, qué se ha visto, quién está registrado) y es el
/// asistente que llama quien pone la comprensión del lenguaje. Así el sistema de
/// visión no depende de ningún modelo de lenguaje para funcionar.
/// </summary>
[McpServerToolType]
public sealed class VisionMcpTools
{
    private readonly LiveViewService _live;
    private readonly SearchService _search;
    private readonly CameraPipelineManager _pipeline;
    private readonly FrameBroadcaster _broadcaster;
    private readonly PendingSubjectService _pending;
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;

    public VisionMcpTools(LiveViewService live, SearchService search, CameraPipelineManager pipeline,
                          FrameBroadcaster broadcaster, PendingSubjectService pending,
                          IDbContextFactory<VisionDbContext> dbFactory)
    {
        _live = live;
        _search = search;
        _pipeline = pipeline;
        _broadcaster = broadcaster;
        _pending = pending;
        _dbFactory = dbFactory;
    }

    [McpServerTool(Name = "listar_camaras")]
    [Description("Lista las cámaras configuradas con su estado de conexión, resolución y cuántos objetos y alertas tienen ahora mismo.")]
    public object ListarCamaras()
    {
        var vista = _live.Build();

        return vista.detalle.Select(c => new
        {
            c.id,
            c.nombre,
            c.origen,
            c.conectada,
            c.estado,
            fps = c.fps,
            resolucion = c.ancho > 0 ? $"{c.ancho}x{c.alto}" : null,
            objetosVisibles = c.viendo.Count,
            alertasActivas = c.alertas.Count,
        }).ToList();
    }

    [McpServerTool(Name = "ver_objetos")]
    [Description("Devuelve lo que las cámaras están viendo en este instante: rostros identificados o desconocidos, matrículas, objetos, códigos QR o de barras, texto y alertas de actividad sospechosa.")]
    public object VerObjetos(
        [Description("Id de una cámara concreta. Omitir para consultar todas.")] string? camaraId = null,
        [Description("Filtra por tipo: rostro, matricula, objeto, codigo o texto.")] string? tipo = null)
    {
        Guid? id = Guid.TryParse(camaraId, out var parsed) ? parsed : null;
        return _live.Build(id, includeImage: false, kindFilter: tipo);
    }

    [McpServerTool(Name = "buscar")]
    [Description("Busca en el histórico y entre los sujetos aún sin identificar. Admite una frase en castellano como «personas desconocidas de anoche en la entrada» o «alertas de animales de esta semana», e informa de cómo ha interpretado la consulta.")]
    public async Task<object> Buscar(
        [Description("Frase de búsqueda en castellano.")] string prompt,
        [Description("Número máximo de resultados (1-500).")] int limite = 50,
        CancellationToken ct = default)
    {
        var result = await _search.SearchAsync(prompt, limite, includePending: true, ct);

        return new
        {
            interpretacion = result.Interpretation,
            total = result.Total,
            resultados = result.Hits,
        };
    }

    [McpServerTool(Name = "capturar_imagen")]
    [Description("Obtiene el fotograma anotado más reciente de una cámara, en JPEG codificado en base64, con los cuadrantes de los objetos ya dibujados.")]
    public object CapturarImagen(
        [Description("Id de la cámara.")] string camaraId)
    {
        if (!Guid.TryParse(camaraId, out var id))
            return new { error = "El id de cámara no es válido." };

        var jpeg = _broadcaster.GetLatest(id);
        if (jpeg is null)
            return new { error = "Esa cámara no tiene ningún fotograma disponible ahora mismo." };

        var status = _pipeline.GetStatus(id);

        return new
        {
            camara = status?.Name,
            formato = "image/jpeg",
            instante = DateTimeOffset.Now,
            resolucion = status is null ? null : $"{status.FrameWidth}x{status.FrameHeight}",
            base64 = Convert.ToBase64String(jpeg),
        };
    }

    [McpServerTool(Name = "listar_desconocidos")]
    [Description("Lista los rostros, matrículas y objetos que el sistema ha detectado pero no ha sabido identificar y que esperan que alguien les ponga nombre.")]
    public async Task<object> ListarDesconocidos(
        [Description("Filtra por tipo: Face, Plate u Object.")] string? tipo = null,
        [Description("Número máximo de fichas.")] int limite = 50,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.PendingSubjects.AsNoTracking().Where(p => p.Status == PendingStatus.Pending);

        if (Enum.TryParse<RecognitionKind>(tipo, ignoreCase: true, out var kind))
            query = query.Where(p => p.Kind == kind);

        return await query
            .OrderByDescending(p => p.Occurrences).ThenByDescending(p => p.LastSeenAt)
            .Take(Math.Clamp(limite, 1, 200))
            .Select(p => new
            {
                p.Id,
                tipo = p.Kind.ToString(),
                camara = p.CameraName,
                apariciones = p.Occurrences,
                primeraVez = p.FirstSeenAt,
                ultimaVez = p.LastSeenAt,
                matricula = p.PlateText,
                clase = p.ObjectClass,
                sePuedeAprender = p.Embedding != null || p.PlateText != null,
            })
            .ToListAsync(ct);
    }

    [McpServerTool(Name = "nombrar_desconocido")]
    [Description("Pone nombre a un sujeto desconocido para que el sistema lo reconozca a partir de ese momento. Funciona con rostros (crea o reutiliza una persona), matrículas (crea o reutiliza un vehículo) y objetos.")]
    public async Task<object> NombrarDesconocido(
        [Description("Id de la ficha pendiente, obtenido de listar_desconocidos.")] long fichaId,
        [Description("Nombre que se le asigna.")] string nombre,
        [Description("Si el sujeto queda autorizado a acceder.")] bool autorizado = true,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var pending = await db.PendingSubjects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == fichaId, ct);

        if (pending is null) return new { ok = false, mensaje = "La ficha no existe." };

        var result = pending.Kind switch
        {
            RecognitionKind.Face => await _pending.AssignFaceAsync(fichaId, null, nombre, autorizado, ct),
            RecognitionKind.Plate => await _pending.AssignPlateAsync(fichaId, null, nombre, null, autorizado, ct),
            RecognitionKind.Object => await _pending.AssignObjectAsync(fichaId, null, nombre, autorizado, ct),
            _ => new AssignResult(false, "Ese tipo de ficha no se puede nombrar."),
        };

        return new { ok = result.Success, mensaje = result.Message, id = result.EntityId };
    }

    [McpServerTool(Name = "consultar_registro")]
    [Description("Consulta el padrón: personas, vehículos y objetos que el sistema ya tiene registrados y por tanto reconoce.")]
    public async Task<object> ConsultarRegistro(
        [Description("Qué consultar: personas, vehiculos u objetos.")] string que = "personas",
        [Description("Texto a buscar en el nombre o la matrícula.")] string? filtro = null,
        [Description("Número máximo de resultados.")] int limite = 50,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var take = Math.Clamp(limite, 1, 200);
        var term = filtro?.Trim();

        switch (que?.Trim().ToLowerInvariant())
        {
            case "vehiculos" or "vehículos" or "matriculas" or "matrículas":
            {
                var query = db.Vehicles.AsNoTracking().Where(v => v.IsActive);
                if (!string.IsNullOrEmpty(term)) query = query.Where(v => v.Plate.Contains(term));

                return await query.OrderBy(v => v.Plate).Take(take)
                    .Select(v => new
                    {
                        v.Id, v.Plate, v.Make, v.Model, v.Color, v.IsAuthorized,
                        titular = v.OwnerPerson != null ? v.OwnerPerson.FullName : null,
                    })
                    .ToListAsync(ct);
            }

            case "objetos":
            {
                var query = db.KnownObjects.AsNoTracking().Where(o => o.IsActive);
                if (!string.IsNullOrEmpty(term)) query = query.Where(o => o.Name.Contains(term));

                return await query.OrderBy(o => o.Name).Take(take)
                    .Select(o => new
                    {
                        o.Id, o.Name, o.ObjectClass, o.IsAuthorized,
                        muestras = o.Templates.Count,
                    })
                    .ToListAsync(ct);
            }

            default:
            {
                var query = db.Persons.AsNoTracking().Where(p => p.IsActive);
                if (!string.IsNullOrEmpty(term)) query = query.Where(p => p.FullName.Contains(term));

                return await query.OrderBy(p => p.FullName).Take(take)
                    .Select(p => new
                    {
                        p.Id, p.FullName, p.DocumentId, p.Department, p.IsAuthorized,
                        rostros = p.FaceTemplates.Count,
                    })
                    .ToListAsync(ct);
            }
        }
    }
}
