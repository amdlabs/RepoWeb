using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Data;

public class VisionDbContext : DbContext
{
    public VisionDbContext(DbContextOptions<VisionDbContext> options) : base(options) { }

    public DbSet<Person> Persons => Set<Person>();
    public DbSet<FaceTemplate> FaceTemplates => Set<FaceTemplate>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<KnownObject> KnownObjects => Set<KnownObject>();
    public DbSet<ObjectTemplate> ObjectTemplates => Set<ObjectTemplate>();
    public DbSet<RecognitionEvent> RecognitionEvents => Set<RecognitionEvent>();
    public DbSet<PendingSubject> PendingSubjects => Set<PendingSubject>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Person>(e =>
        {
            e.ToTable("Persons");
            e.HasIndex(p => p.FullName);
            e.HasIndex(p => p.DocumentId);
        });

        b.Entity<FaceTemplate>(e =>
        {
            e.ToTable("FaceTemplates");
            e.Property(f => f.Embedding).HasColumnType("varbinary(max)");
            e.HasIndex(f => f.PersonId);
            e.HasOne(f => f.Person)
             .WithMany(p => p.FaceTemplates)
             .HasForeignKey(f => f.PersonId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Vehicle>(e =>
        {
            e.ToTable("Vehicles");
            e.HasIndex(v => v.Plate).IsUnique();
            e.HasOne(v => v.OwnerPerson)
             .WithMany(p => p.Vehicles)
             .HasForeignKey(v => v.OwnerPersonId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<KnownObject>(e =>
        {
            e.ToTable("KnownObjects");
            e.HasIndex(o => o.Name);
            e.HasIndex(o => o.ObjectClass);
            e.HasOne(o => o.OwnerPerson)
             .WithMany()
             .HasForeignKey(o => o.OwnerPersonId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ObjectTemplate>(e =>
        {
            e.ToTable("ObjectTemplates");
            e.Property(t => t.Embedding).HasColumnType("varbinary(max)");
            e.HasIndex(t => t.KnownObjectId);
            e.HasOne(t => t.KnownObject)
             .WithMany(o => o.Templates)
             .HasForeignKey(t => t.KnownObjectId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecognitionEvent>(e =>
        {
            e.ToTable("RecognitionEvents");
            e.HasIndex(r => r.OccurredAt);
            e.HasIndex(r => new { r.CameraId, r.OccurredAt });
            e.HasIndex(r => r.PlateText);
            e.HasIndex(r => r.ObjectClass);
            e.HasIndex(r => new { r.Kind, r.OccurredAt });

            e.HasOne(r => r.Person).WithMany().HasForeignKey(r => r.PersonId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.Vehicle).WithMany().HasForeignKey(r => r.VehicleId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.KnownObject).WithMany().HasForeignKey(r => r.KnownObjectId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<PendingSubject>(e =>
        {
            e.ToTable("PendingSubjects");
            e.Property(p => p.Embedding).HasColumnType("varbinary(max)");
            e.HasIndex(p => new { p.Status, p.Kind, p.LastSeenAt });
            e.HasIndex(p => p.PlateText);

            e.HasOne(p => p.AssignedPerson).WithMany().HasForeignKey(p => p.AssignedPersonId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(p => p.AssignedVehicle).WithMany().HasForeignKey(p => p.AssignedVehicleId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(p => p.AssignedObject).WithMany().HasForeignKey(p => p.AssignedObjectId).OnDelete(DeleteBehavior.SetNull);
        });

        base.OnModelCreating(b);
    }
}
