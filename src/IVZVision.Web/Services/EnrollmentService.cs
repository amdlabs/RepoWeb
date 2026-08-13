using IVZVision.Core.Configuration;
using IVZVision.Core.Util;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Vision.Engine;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;

namespace IVZVision.Web.Services;

public sealed record EnrollResult(bool Success, string Message, int TemplateId = 0);

/// <summary>Da de alta rostros: extrae el embedding de una foto y lo asocia a una persona.</summary>
public sealed class EnrollmentService
{
    private const long MaxImageBytes = 12 * 1024 * 1024;

    private readonly RecognitionEngine _engine;
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly KnownSubjectsIndex _index;
    private readonly IConfigStore _config;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(RecognitionEngine engine, IDbContextFactory<VisionDbContext> dbFactory,
                             KnownSubjectsIndex index, IConfigStore config,
                             IWebHostEnvironment environment, ILogger<EnrollmentService> logger)
    {
        _engine = engine;
        _dbFactory = dbFactory;
        _index = index;
        _config = config;
        _environment = environment;
        _logger = logger;
    }

    public async Task<EnrollResult> EnrollAsync(int personId, IFormFile file, CancellationToken ct = default)
    {
        if (file.Length == 0)
            return new EnrollResult(false, "El fichero está vacío.");

        if (file.Length > MaxImageBytes)
            return new EnrollResult(false, "La imagen supera los 12 MB.");

        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct).ConfigureAwait(false);
            bytes = ms.ToArray();
        }

        using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (image.Empty())
            return new EnrollResult(false, "No se ha podido leer la imagen (formatos admitidos: JPG, PNG, BMP).");

        var enrollment = _engine.EnrollFace(image);
        if (!enrollment.Success || enrollment.Embedding is null)
            return new EnrollResult(false, enrollment.Message);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var person = await db.Persons.FirstOrDefaultAsync(p => p.Id == personId, ct).ConfigureAwait(false);
        if (person is null)
            return new EnrollResult(false, "La persona indicada ya no existe.");

        var imagePath = SaveReferenceImage(personId, enrollment.AlignedJpeg);

        var template = new FaceTemplate
        {
            PersonId = personId,
            Embedding = VectorMath.ToBytes(enrollment.Embedding),
            Dimensions = enrollment.Embedding.Length,
            ModelId = enrollment.ModelId ?? "",
            ImagePath = imagePath,
            Quality = enrollment.Score,
        };

        db.FaceTemplates.Add(template);
        person.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _index.MarkDirty();
        await _index.RefreshAsync(ct).ConfigureAwait(false);

        return new EnrollResult(true, $"{enrollment.Message} Plantilla registrada para {person.FullName}.", template.Id);
    }

    private string? SaveReferenceImage(int personId, byte[]? jpeg)
    {
        if (jpeg is null || jpeg.Length == 0) return null;

        try
        {
            var root = _config.Current.Storage.Resolve(_environment.ContentRootPath);
            var relativeDir = Path.Combine("personas", personId.ToString());
            Directory.CreateDirectory(Path.Combine(root, relativeDir));

            var fileName = $"{Guid.NewGuid():N}.jpg";
            File.WriteAllBytes(Path.Combine(root, relativeDir, fileName), jpeg);

            return Path.Combine(relativeDir, fileName).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar la imagen de referencia de la persona {PersonId}", personId);
            return null;
        }
    }
}
