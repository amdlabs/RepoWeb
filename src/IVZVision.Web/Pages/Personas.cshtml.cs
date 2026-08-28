using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Vision.Engine;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

public class PersonasModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly KnownSubjectsIndex _index;
    private readonly FaceClusterIndex _clusters;

    public PersonasModel(IDbContextFactory<VisionDbContext> dbFactory, KnownSubjectsIndex index,
                         FaceClusterIndex clusters)
    {
        _dbFactory = dbFactory;
        _index = index;
        _clusters = clusters;
    }

    public sealed record PersonRow(int Id, string FullName, string? DocumentId, string? Department,
                                   bool IsAuthorized, bool IsActive, int Templates, DateTime CreatedAt);

    public IReadOnlyList<PersonRow> People { get; private set; } = Array.Empty<PersonRow>();

    [BindProperty] public Person NewPerson { get; set; } = new();

    [BindProperty(SupportsGet = true)] public string? Buscar { get; set; }

    public string? DatabaseError { get; private set; }

    public async Task OnGetAsync([FromServices] FaceClusterBackfill backfill, CancellationToken ct)
    {
        await LoadAsync(ct);
        EstadoReproceso = backfill.Status with { Pendientes = await backfill.PendingCountAsync(ct) };
    }

    /// <summary>Persona detectada por las cámaras (rostro u objeto persona), con su recorte.</summary>
    public sealed record UnknownFace(long EventId, DateTime OccurredAt, string CameraName,
                                     string Label, bool IsKnown, string? CropBase64, string? CropPath,
                                     int Repeticiones, string? FullFramePath);

    public IReadOnlyList<UnknownFace> UnknownFaces { get; private set; } = Array.Empty<UnknownFace>();

    /// <summary>Total de detecciones de personas en el histórico.</summary>
    public int UnknownFacesTotal { get; private set; }

    private const int DetectionsPageSize = 20;

    [BindProperty(SupportsGet = true)] public int Pagina { get; set; } = 1;

    /// <summary>Filtro por cámara de origen de la detección.</summary>
    [BindProperty(SupportsGet = true)] public string? Camara { get; set; }

    public int TotalPaginas { get; private set; } = 1;

    public IReadOnlyList<string> CamerasDisponibles { get; private set; } = Array.Empty<string>();

    private async Task LoadUnknownFacesAsync(VisionDbContext db, CancellationToken ct)
    {
        // Rostros (reconocidos o no) + personas vistas por el detector de objetos
        // (cuando la cara queda demasiado pequeña para el detector facial, la persona
        // entra igualmente por aquí con el recorte de su figura).
        var query = db.RecognitionEvents
            .AsNoTracking()
            .Where(e => (e.CropBase64 != null || e.CropPath != null)
                        && (e.Kind == RecognitionKind.Face
                            || (e.Kind == RecognitionKind.Object
                                && (e.ObjectClass == "persona" || e.ObjectClass == "person"))));

        CamerasDisponibles = await query.Select(e => e.CameraName).Distinct().OrderBy(n => n).ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(Camara))
            query = query.Where(e => e.CameraName == Camara);

        // Se agrupan las repeticiones (misma cámara e identificación dentro de una ventana
        // de 10 minutos) en una sola fila con contador, para no llenar la tabla con la
        // misma persona vista una y otra vez.
        var raw = await query
            .OrderByDescending(e => e.OccurredAt)
            .Take(600)
            .Select(e => new { e.Id, e.OccurredAt, e.CameraName, e.Label, e.IsKnown, e.CropBase64, e.CropPath,
                               e.FullFramePath })
            .ToListAsync(ct);

        var grouped = new List<UnknownFace>();
        foreach (var e in raw)
        {
            var previous = grouped.FindIndex(g =>
                g.CameraName == e.CameraName
                && g.IsKnown == e.IsKnown
                && (!e.IsKnown || g.Label == e.Label)
                && (g.OccurredAt - e.OccurredAt) < TimeSpan.FromMinutes(10));

            if (previous >= 0)
            {
                grouped[previous] = grouped[previous] with { Repeticiones = grouped[previous].Repeticiones + 1 };
                continue;
            }

            grouped.Add(new UnknownFace(e.Id, e.OccurredAt, e.CameraName, e.Label, e.IsKnown,
                                        e.CropBase64, e.CropPath, 1, e.FullFramePath));
        }

        UnknownFacesTotal = grouped.Count;
        TotalPaginas = Math.Max(1, (int)Math.Ceiling(UnknownFacesTotal / (double)DetectionsPageSize));
        Pagina = Math.Clamp(Pagina, 1, TotalPaginas);

        UnknownFaces = grouped
            .Skip((Pagina - 1) * DetectionsPageSize)
            .Take(DetectionsPageSize)
            .ToList();
    }

    // ---- Rostros agrupados --------------------------------------------------
    // El motor va metiendo cada cara que ve en el grupo al que se parece, sea la
    // cámara que sea. Aquí se muestra un grupo por persona con todas sus fotos, y
    // al ponerle nombre el grupo pasa al padrón y sus fotos se vuelven plantillas.

    public sealed record GroupPhoto(long EventId, DateTime OccurredAt, string CameraName,
                                    string? CropBase64, string? CropPath, string? FullFramePath);

    public sealed record FaceGroup(int ClusterId, int Numero, string DisplayName, int? PersonId,
                                   DateTime PrimeraVez, DateTime UltimaVez, int TotalFotos,
                                   IReadOnlyList<string> Camaras, IReadOnlyList<GroupPhoto> Fotos,
                                   float SimilitudPrevia);

    /// <summary>Criterio de orden de la lista de grupos.</summary>
    [BindProperty(SupportsGet = true)] public string? Orden { get; set; }

    /// <summary>Deja sólo los grupos que alguna vez pasaron por esta cámara.</summary>
    [BindProperty(SupportsGet = true)] public string? CamaraGrupo { get; set; }

    /// <summary>Cámaras que han aportado alguna foto a algún grupo.</summary>
    public IReadOnlyList<string> CamarasGrupos { get; private set; } = Array.Empty<string>();

    public bool OrdenPorParecido => !string.Equals(Orden, "fecha", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<FaceGroup> FaceGroups { get; private set; } = Array.Empty<FaceGroup>();

    /// <summary>Grupos de rostros formados en total (con o sin nombre).</summary>
    public int GruposTotal { get; private set; }

    public int TotalPaginasGrupos { get; private set; } = 1;

    [BindProperty(SupportsGet = true)] public int PaginaGrupos { get; set; } = 1;

    private const int GroupsPageSize = 8;
    private const int PhotosPerGroup = 12;

    private async Task LoadFaceGroupsAsync(VisionDbContext db, FaceClusterIndex clusters, CancellationToken ct)
    {
        // Un grupo sólo interesa si conserva alguna foto que enseñar.
        var conFoto = db.RecognitionEvents.AsNoTracking()
            .Where(e => e.FaceClusterId > 0 && (e.CropBase64 != null || e.CropPath != null));

        CamarasGrupos = await conFoto.Select(e => e.CameraName).Distinct().OrderBy(n => n).ToListAsync(ct);

        // Al filtrar por cámara se conservan los grupos completos, con las fotos de
        // todas las cámaras: lo que se acota es qué personas se listan, que son las
        // que en algún momento pasaron por la cámara elegida.
        if (!string.IsNullOrWhiteSpace(CamaraGrupo))
        {
            var vistosAhi = conFoto.Where(e => e.CameraName == CamaraGrupo)
                                   .Select(e => e.FaceClusterId!.Value);
            conFoto = conFoto.Where(e => vistosAhi.Contains(e.FaceClusterId!.Value));
        }

        var resumen = await conFoto
            .GroupBy(e => e.FaceClusterId!.Value)
            .Select(g => new { ClusterId = g.Key, Fotos = g.Count(), Ultima = g.Max(e => e.OccurredAt) })
            .ToListAsync(ct);

        GruposTotal = resumen.Count;
        TotalPaginasGrupos = Math.Max(1, (int)Math.Ceiling(GruposTotal / (double)GroupsPageSize));
        PaginaGrupos = Math.Clamp(PaginaGrupos, 1, TotalPaginasGrupos);

        // Por defecto los grupos salen encadenados por parecido, de modo que las
        // caras candidatas a ser la misma persona queden contiguas y unificarlas
        // sea cuestión de mirar dos fichas seguidas.
        var parecidos = new Dictionary<int, float>();
        List<int> secuencia;

        if (OrdenPorParecido)
        {
            var conVida = resumen.Select(r => r.ClusterId).ToHashSet();
            var cadena = await clusters.OrderBySimilarityAsync(ct);

            secuencia = cadena.Where(c => conVida.Contains(c.Id)).Select(c => c.Id).ToList();
            foreach (var c in cadena) parecidos[c.Id] = c.SimilitudPrevia;

            // Cualquiera que el encadenado no haya alcanzado va detrás, por fecha.
            secuencia.AddRange(resumen.Where(r => !secuencia.Contains(r.ClusterId))
                                      .OrderByDescending(r => r.Ultima)
                                      .Select(r => r.ClusterId));
        }
        else
        {
            secuencia = resumen.OrderByDescending(r => r.Ultima).Select(r => r.ClusterId).ToList();
        }

        var porId = resumen.ToDictionary(r => r.ClusterId);
        var pagina = secuencia
            .Skip((PaginaGrupos - 1) * GroupsPageSize)
            .Take(GroupsPageSize)
            .Select(id => porId[id])
            .ToList();

        if (pagina.Count == 0)
        {
            FaceGroups = Array.Empty<FaceGroup>();
            return;
        }

        var ids = pagina.Select(p => p.ClusterId).ToList();

        var fichas = await db.FaceClusters.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        // Las fotos de todos los grupos de la página, en una sola consulta.
        var fotos = await db.RecognitionEvents.AsNoTracking()
            .Where(e => e.FaceClusterId > 0 && ids.Contains(e.FaceClusterId!.Value)
                        && (e.CropBase64 != null || e.CropPath != null))
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.Id, e.FaceClusterId, e.OccurredAt, e.CameraName,
                               e.CropBase64, e.CropPath, e.FullFramePath })
            .Take(GroupsPageSize * 60)
            .ToListAsync(ct);

        var grupos = new List<FaceGroup>();
        foreach (var r in pagina)
        {
            if (!fichas.TryGetValue(r.ClusterId, out var ficha)) continue;

            var suyas = fotos.Where(f => f.FaceClusterId == r.ClusterId).ToList();

            grupos.Add(new FaceGroup(
                ficha.Id, ficha.Numero, ficha.DisplayName, ficha.PersonId,
                ficha.FirstSeenAt, r.Ultima, r.Fotos,
                suyas.Select(f => f.CameraName).Distinct().OrderBy(n => n).ToList(),
                suyas.Take(PhotosPerGroup)
                     .Select(f => new GroupPhoto(f.Id, f.OccurredAt, f.CameraName,
                                                 f.CropBase64, f.CropPath, f.FullFramePath))
                     .ToList(),
                parecidos.TryGetValue(ficha.Id, out var parecido) ? parecido : 0));
        }

        FaceGroups = grupos;
    }

    /// <summary>
    /// Pone nombre a un grupo de rostros: crea la persona en el padrón y registra
    /// sus mejores fotos como plantillas, de forma que a partir de ese momento el
    /// sistema la reconozca por su nombre en cualquier cámara.
    /// </summary>
    public async Task<IActionResult> OnPostNombrarGrupoAsync(int grupoId, string nombre,
                                                             [FromServices] FaceGroupService servicio,
                                                             CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        var resultado = await servicio.NombrarAsync(grupoId, nombre, ct);
        TempData[resultado.Ok ? "Ok" : "Error"] = resultado.Mensaje;
        return RedirectToPage(new { PaginaGrupos, Orden, CamaraGrupo });
    }

    /// <summary>Cómo va el reproceso automático de las fotos anteriores.</summary>
    public BackfillStatus? EstadoReproceso { get; private set; }

    /// <summary>
    /// Rehace todos los grupos desde cero. Se usa después de tocar el umbral de
    /// agrupamiento, para que el criterio nuevo se aplique también a lo ya visto;
    /// el trabajo lo va haciendo solo el servicio en segundo plano.
    /// </summary>
    public async Task<IActionResult> OnPostReagruparAsync([FromServices] FaceClusterBackfill backfill,
                                                          CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        var afectadas = await backfill.ReagruparTodoAsync(ct);
        TempData["Ok"] = $"Se han deshecho los grupos: {afectadas} foto(s) volverán a agruparse " +
                          "con el criterio actual. El sistema lo hace solo en segundo plano; " +
                          "recargue esta página para ver cómo avanza.";
        return RedirectToPage(new { CamaraGrupo });
    }

    /// <summary>
    /// Une los grupos marcados en uno solo: son la misma persona vista de frente,
    /// de lado o desde otra cámara. La cara promedio resultante pondera cada grupo
    /// por las fotos que aporta, así que el sistema queda conociendo esa cara en
    /// todas esas poses en vez de en una sola. Si el conjunto ya tiene nombre, se
    /// añaden plantillas de las poses recién incorporadas.
    /// </summary>
    public async Task<IActionResult> OnPostUnificarAsync(int[] grupos,
                                                         [FromServices] FaceGroupService servicio,
                                                         CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        var resultado = await servicio.UnificarAsync(grupos ?? Array.Empty<int>(), ct);
        TempData[resultado.Ok ? "Ok" : "Error"] = resultado.Mensaje;
        return RedirectToPage(new { PaginaGrupos, Orden, CamaraGrupo });
    }

    /// <summary>
    /// Saca una foto de su grupo porque no es esa persona. Además de apartarla, se
    /// rehace la cara promedio del grupo con las que quedan: si no, esa cara ajena
    /// seguiría pesando en el promedio y el grupo seguiría atrayendo caras que no son.
    /// </summary>
    public async Task<IActionResult> OnPostQuitarFotoAsync(long eventoId, int grupoId,
                                                           [FromServices] FaceClusterIndex clusters,
                                                           [FromServices] FaceGroupService servicio,
                                                           CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // -2 marca «apartada a mano»: ni sale en el grupo ni vuelve a la cola de reproceso.
        var afectadas = await db.RecognitionEvents
            .Where(e => e.Id == eventoId && e.FaceClusterId == grupoId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.FaceClusterId, (int?)(-2)), ct);

        if (afectadas == 0)
        {
            TempData["Error"] = "Esa foto ya no está en el grupo.";
            return RedirectToPage(new { PaginaGrupos, Orden, CamaraGrupo });
        }

        var quedan = await db.RecognitionEvents
            .CountAsync(e => e.FaceClusterId == grupoId && (e.CropBase64 != null || e.CropPath != null), ct);

        if (quedan == 0)
        {
            // El grupo se queda vacío: no tiene sentido conservarlo.
            await db.FaceClusters.Where(c => c.Id == grupoId).ExecuteDeleteAsync(ct);
            clusters.MarkDirty();
            TempData["Ok"] = "Foto apartada. El grupo se ha quedado sin fotos y se ha eliminado.";
            return RedirectToPage(new { PaginaGrupos, Orden, CamaraGrupo });
        }

        var rehecho = await servicio.RecalcularAsync(grupoId, ct);

        TempData["Ok"] = rehecho
            ? "Foto apartada del grupo y cara promedio recalculada con las restantes."
            : "Foto apartada del grupo.";
        return RedirectToPage(new { PaginaGrupos, Orden, CamaraGrupo });
    }

    /// <summary>Deshace una agrupación mal formada (dos personas mezcladas en un grupo).</summary>
    public async Task<IActionResult> OnPostDeshacerGrupoAsync(int grupoId,
                                                              [FromServices] FaceClusterIndex clusters,
                                                              CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        await db.RecognitionEvents
            .Where(e => e.FaceClusterId == grupoId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.FaceClusterId, (int?)null), ct);

        await db.FaceClusters.Where(c => c.Id == grupoId).ExecuteDeleteAsync(ct);

        clusters.MarkDirty();
        TempData["Ok"] = "Grupo deshecho. Las caras que lleguen a partir de ahora se volverán a agrupar.";
        return RedirectToPage(new { PaginaGrupos, Orden, CamaraGrupo });
    }

    /// <summary>Da de alta una persona nueva usando el rostro de un evento no identificado.</summary>
    public async Task<IActionResult> OnPostAltaDesdeEventoAsync(long eventoId, string nombre,
                                                                [FromServices] EnrollmentService enrollment,
                                                                CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        nombre = (nombre ?? "").Trim();
        if (nombre.Length == 0)
        {
            TempData["Error"] = "Escriba el nombre de la persona antes de darla de alta.";
            return RedirectToPage();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var evento = await db.RecognitionEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventoId
                                      && (e.Kind == RecognitionKind.Face || e.Kind == RecognitionKind.Object), ct);

        byte[]? imagen = null;
        if (evento?.CropBase64 is not null)
        {
            imagen = Convert.FromBase64String(evento.CropBase64);
        }
        else if (evento?.CropPath is not null)
        {
            var resolved = HttpContext.RequestServices.GetRequiredService<SnapshotPathResolver>().Resolve(evento.CropPath);
            if (resolved is not null && System.IO.File.Exists(resolved))
                imagen = await System.IO.File.ReadAllBytesAsync(resolved, ct);
        }

        if (imagen is null)
        {
            TempData["Error"] = "El evento ya no existe o no conserva el recorte del rostro.";
            return RedirectToPage();
        }

        var person = new Person { FullName = nombre, IsAuthorized = false };
        db.Persons.Add(person);
        await db.SaveChangesAsync(ct);

        var result = await enrollment.EnrollFromBytesAsync(person.Id, imagen, ct);
        if (!result.Success)
        {
            // Sin plantilla la persona no aporta nada: se revierte el alta.
            db.Persons.Remove(person);
            await db.SaveChangesAsync(ct);
            TempData["Error"] = $"No se pudo registrar el rostro: {result.Message}";
            return RedirectToPage();
        }

        _index.MarkDirty();
        TempData["Ok"] = $"Persona «{nombre}» creada a partir del rostro detectado (marcada como no autorizada; " +
                          "revísela y autorícela si procede).";
        return RedirectToPage("/Persona", new { id = person.Id });
    }

    /// <summary>Borra las detecciones de personas (no toca el padrón de personas dadas de alta).</summary>
    public async Task<IActionResult> OnPostLimpiarDeteccionesAsync([FromServices] DetectionCleanup cleanup,
                                                                   int? dias, bool soloNoIdentificadas,
                                                                   string? camara, CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        var (eventos, imagenes) = await cleanup.DeleteAsync(
            kinds: new[] { RecognitionKind.Face, RecognitionKind.Object },
            objectClasses: new[] { "persona", "person" },
            camera: camara,
            days: dias,
            onlyUnknown: soloNoIdentificadas,
            ct: ct);

        TempData["Ok"] = $"Se han borrado {eventos} detección(es) de personas y {imagenes} imagen(es). " +
                          "El padrón de personas se mantiene intacto.";
        return RedirectToPage(new { Camara = camara });
    }

    public async Task<IActionResult> OnPostCrearAsync(CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        if (string.IsNullOrWhiteSpace(NewPerson.FullName))
        {
            TempData["Error"] = "El nombre es obligatorio.";
            return RedirectToPage();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var person = new Person
        {
            FullName = NewPerson.FullName.Trim(),
            DocumentId = string.IsNullOrWhiteSpace(NewPerson.DocumentId) ? null : NewPerson.DocumentId.Trim(),
            Department = string.IsNullOrWhiteSpace(NewPerson.Department) ? null : NewPerson.Department.Trim(),
            IsAuthorized = NewPerson.IsAuthorized,
            Notes = NewPerson.Notes,
        };

        db.Persons.Add(person);
        await db.SaveChangesAsync(ct);
        _index.MarkDirty();

        TempData["Ok"] = $"Persona «{person.FullName}» creada. Añada ahora una o varias fotos para registrar su rostro.";
        return RedirectToPage("/Persona", new { id = person.Id });
    }

    public async Task<IActionResult> OnPostEliminarAsync(int id, CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var person = await db.Persons.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (person is null)
        {
            TempData["Error"] = "La persona ya no existe.";
            return RedirectToPage();
        }

        db.Persons.Remove(person);
        await db.SaveChangesAsync(ct);
        _index.MarkDirty();
        await _index.RefreshAsync(ct);

        TempData["Ok"] = $"Persona «{person.FullName}» eliminada junto con sus plantillas faciales.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var query = db.Persons.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(Buscar))
            {
                var term = Buscar.Trim();
                query = query.Where(p => p.FullName.Contains(term)
                                      || (p.DocumentId != null && p.DocumentId.Contains(term))
                                      || (p.Department != null && p.Department.Contains(term)));
            }

            People = await query
                .OrderBy(p => p.FullName)
                .Select(p => new PersonRow(p.Id, p.FullName, p.DocumentId, p.Department,
                                           p.IsAuthorized, p.IsActive, p.FaceTemplates.Count, p.CreatedAt))
                .Take(500)
                .ToListAsync(ct);

            await LoadFaceGroupsAsync(db, _clusters, ct);

            await LoadUnknownFacesAsync(db, ct);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }
}
