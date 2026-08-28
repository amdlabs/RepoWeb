using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>
/// Grupo de rostros que pertenecen a la misma persona, aunque todavía no se sepa
/// quién es. Se forma solo: cada rostro detectado se compara con los grupos ya
/// conocidos y, si se parece lo suficiente, se suma a ese grupo en lugar de crear
/// uno nuevo — da igual la cámara en la que aparezca.
/// Al ponerle nombre, el grupo se convierte en una persona del padrón y sus fotos
/// pasan a ser sus plantillas de reconocimiento.
/// </summary>
public class FaceCluster
{
    public int Id { get; set; }

    /// <summary>Número correlativo para nombrarlo mientras es desconocido («Rostro desconocido 3»).</summary>
    public int Numero { get; set; }

    /// <summary>Nombre puesto por el usuario; null mientras no se sepa quién es.</summary>
    [MaxLength(200)]
    public string? Label { get; set; }

    /// <summary>Persona del padrón a la que se promovió este grupo, si ya se hizo.</summary>
    public int? PersonId { get; set; }

    /// <summary>Media de los embeddings del grupo, normalizada: es su "cara promedio".</summary>
    public byte[] Centroid { get; set; } = Array.Empty<byte>();

    public int Dimensions { get; set; }

    /// <summary>Rostros que se han sumado al grupo (cuanto mayor, más estable es su centroide).</summary>
    public int SampleCount { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Nombre visible: el que puso el usuario o el correlativo de desconocido.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"Rostro desconocido {Numero}" : Label!;
}
