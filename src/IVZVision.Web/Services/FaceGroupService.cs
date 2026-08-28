using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Vision.Engine;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;

namespace IVZVision.Web.Services;

public sealed record FaceGroupResult(bool Ok, string Mensaje, int? GrupoId = null, string? Nombre = null);

/// <summary>
/// Operaciones sobre los grupos de rostros —unificar y poner nombre— compartidas
/// por la pantalla de personas y por el muro de monitoreo, para que ambas hagan
/// exactamente lo mismo y el aprendizaje no dependa de por dónde se pida.
/// </summary>
public sealed class FaceGroupService
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly FaceClusterIndex _clusters;
    private readonly KnownSubjectsIndex _index;
    private readonly EnrollmentService _enrollment;
    private readonly SnapshotPathResolver _paths;
    private readonly RecognitionEngine _engine;
    private readonly ILogger<FaceGroupService> _logger;

    public FaceGroupService(IDbContextFactory<VisionDbContext> dbFactory, FaceClusterIndex clusters,
                            KnownSubjectsIndex index, EnrollmentService enrollment,
                            SnapshotPathResolver paths, RecognitionEngine engine,
                            ILogger<FaceGroupService> logger)
    {
        _dbFactory = dbFactory;
        _clusters = clusters;
        _index = index;
        _enrollment = enrollment;
        _paths = paths;
        _engine = engine;
        _logger = logger;
    }

    /// <summary>
    /// Une varios grupos en uno: son la misma persona vista de frente, de lado o en
    /// otra cámara. La cara promedio se recalcula ponderando cada grupo por las fotos
    /// que aporta, y si el conjunto ya tiene nombre se aprenden las poses nuevas.
    /// </summary>
    public async Task<FaceGroupResult> UnificarAsync(IReadOnlyList<int> grupos, CancellationToken ct = default)
    {
        var ids = grupos.Where(id => id > 0).Distinct().ToList();
        if (ids.Count < 2)
            return new FaceGroupResult(false, "Hacen falta al menos dos grupos para unificarlos.");

        var destino = await _clusters.MergeAsync(ids, ct);
        if (destino is null)
            return new FaceGroupResult(false, "No se pudieron unificar los grupos seleccionados.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ficha = await db.FaceClusters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == destino, ct);

        var aprendidas = 0;
        if (ficha?.PersonId is int personaId)
        {
            aprendidas = await AprenderAsync(db, destino.Value, personaId, ct);
            await MarcarHistoricoAsync(db, destino.Value, ficha.DisplayName, personaId, ct);
            _index.MarkDirty();
            await _index.RefreshAsync(ct);
        }

        var mensaje = $"{ids.Count} grupos unificados en «{ficha?.DisplayName ?? "el grupo elegido"}»" +
                      (aprendidas > 0 ? $", con {aprendidas} pose(s) nuevas aprendidas." : ".") +
                      " La cara promedio se ha recalculado con todas las fotos.";

        return new FaceGroupResult(true, mensaje, destino, ficha?.DisplayName);
    }

    /// <summary>
    /// Pone nombre a un grupo: crea (o reutiliza) la persona del padrón y registra
    /// sus mejores fotos como plantillas, de modo que a partir de ahí se la reconozca
    /// por su nombre en todas las cámaras.
    /// </summary>
    public async Task<FaceGroupResult> NombrarAsync(int grupoId, string nombre, CancellationToken ct = default)
    {
        nombre = (nombre ?? "").Trim();
        if (nombre.Length == 0)
            return new FaceGroupResult(false, "Escriba el nombre antes de guardar el grupo.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var grupo = await db.FaceClusters.FirstOrDefaultAsync(c => c.Id == grupoId, ct);
        if (grupo is null)
            return new FaceGroupResult(false, "El grupo de rostros ya no existe.");

        // Si ya existe una persona con ese nombre se reutiliza: así dos grupos de la
        // misma persona (con gorra y sin ella) acaban en una única ficha.
        var persona = await db.Persons.FirstOrDefaultAsync(p => p.FullName == nombre, ct);
        var creada = persona is null;
        if (persona is null)
        {
            persona = new Person { FullName = nombre, IsAuthorized = false };
            db.Persons.Add(persona);
            await db.SaveChangesAsync(ct);
        }

        var registradas = await AprenderAsync(db, grupoId, persona.Id, ct);

        if (registradas == 0 && creada)
        {
            // Sin ninguna plantilla la persona no serviría para reconocer nada.
            db.Persons.Remove(persona);
            await db.SaveChangesAsync(ct);
            return new FaceGroupResult(false,
                "Ninguna de las fotos del grupo sirve como plantilla facial " +
                "(demasiado pequeñas, borrosas o de perfil). Pruebe con otro grupo.");
        }

        grupo.Label = nombre;
        grupo.PersonId = persona.Id;
        await db.SaveChangesAsync(ct);

        await MarcarHistoricoAsync(db, grupoId, nombre, persona.Id, ct);

        _index.MarkDirty();
        await _index.RefreshAsync(ct);
        _clusters.MarkDirty();

        return new FaceGroupResult(true,
            $"«{nombre}» guardado con {registradas} plantilla(s) de las fotos del grupo. " +
            "A partir de ahora se le reconocerá por su nombre en todas las cámaras.",
            grupoId, nombre);
    }

    /// <summary>
    /// Rehace la cara promedio de un grupo a partir de sus fotos actuales. Se usa al
    /// sacar una foto que no era de esa persona: el vector debe dejar de arrastrarla.
    /// </summary>
    public async Task<bool> RecalcularAsync(int grupoId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var fotos = await db.RecognitionEvents.AsNoTracking()
            .Where(e => e.FaceClusterId == grupoId && (e.CropBase64 != null || e.CropPath != null))
            .OrderByDescending(e => e.DetectionScore)
            .Take(20)
            .Select(e => new { e.CropBase64, e.CropPath })
            .ToListAsync(ct);

        var vectores = new List<float[]>();
        foreach (var foto in fotos)
        {
            var embedding = await CalcularVectorAsync(foto.CropBase64, foto.CropPath, ct);
            if (embedding is not null) vectores.Add(embedding);
        }

        return await _clusters.RecomputeCentroidAsync(grupoId, vectores, ct);
    }

    /// <summary>
    /// Registra como plantillas las fotos más variadas del grupo, repartidas entre las
    /// cámaras que lo vieron: distintas cámaras dan distintos ángulos y luces, que es
    /// la variedad que hace robusto al reconocimiento.
    /// </summary>
    private async Task<int> AprenderAsync(VisionDbContext db, int grupoId, int personaId, CancellationToken ct)
    {
        var candidatas = await db.RecognitionEvents.AsNoTracking()
            .Where(e => e.FaceClusterId == grupoId && e.Kind == RecognitionKind.Face
                        && (e.CropBase64 != null || e.CropPath != null))
            .OrderByDescending(e => e.DetectionScore)
            .Take(40)
            .Select(e => new { e.CameraName, e.DetectionScore, e.CropBase64, e.CropPath })
            .ToListAsync(ct);

        var repartidas = candidatas
            .GroupBy(c => c.CameraName)
            .SelectMany(g => g.Take(3))
            .OrderByDescending(c => c.DetectionScore)
            .Take(8)
            .ToList();

        var aprendidas = 0;
        foreach (var foto in repartidas)
        {
            var imagen = await LeerRecorteAsync(foto.CropBase64, foto.CropPath, ct);
            if (imagen is null) continue;

            // Las borrosas o muy de perfil se descartan solas: el detector no encuentra
            // la cara en ellas y el alta falla sin efecto.
            var alta = await _enrollment.EnrollFromBytesAsync(personaId, imagen, ct);
            if (alta.Success) aprendidas++;
        }

        return aprendidas;
    }

    /// <summary>El histórico del grupo pasa a mostrar el nombre de la persona.</summary>
    private static Task MarcarHistoricoAsync(VisionDbContext db, int grupoId, string nombre, int personaId,
                                             CancellationToken ct)
        => db.RecognitionEvents
             .Where(e => e.FaceClusterId == grupoId)
             .ExecuteUpdateAsync(u => u.SetProperty(e => e.Label, nombre)
                                       .SetProperty(e => e.PersonId, (int?)personaId)
                                       .SetProperty(e => e.IsKnown, true), ct);

    private async Task<float[]?> CalcularVectorAsync(string? base64, string? ruta, CancellationToken ct)
    {
        var bytes = await LeerRecorteAsync(base64, ruta, ct);
        if (bytes is null) return null;

        using var imagen = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (imagen.Empty()) return null;

        var facial = _engine.EnrollFace(imagen);
        return facial.Success ? facial.Embedding : null;
    }

    private async Task<byte[]?> LeerRecorteAsync(string? base64, string? ruta, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(base64))
        {
            try { return Convert.FromBase64String(base64); }
            catch (FormatException)
            {
                _logger.LogDebug("Recorte con base64 ilegible");
                return null;
            }
        }

        if (string.IsNullOrEmpty(ruta)) return null;

        var completa = _paths.Resolve(ruta);
        if (completa is null || !File.Exists(completa)) return null;

        return await File.ReadAllBytesAsync(completa, ct);
    }
}
