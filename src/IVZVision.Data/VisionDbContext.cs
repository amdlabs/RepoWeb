using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Data;

public class VisionDbContext : DbContext
{
    public VisionDbContext(DbContextOptions<VisionDbContext> options) : base(options) { }

    public DbSet<Person> Persons => Set<Person>();
    public DbSet<FaceTemplate> FaceTemplates => Set<FaceTemplate>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<RecognitionEvent> RecognitionEvents => Set<RecognitionEvent>();
    public DbSet<ObjectLabel> ObjectLabels => Set<ObjectLabel>();
    public DbSet<SystemUser> SystemUsers => Set<SystemUser>();
    public DbSet<ConfigSnapshot> ConfigSnapshots => Set<ConfigSnapshot>();

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

        b.Entity<ObjectLabel>(e =>
        {
            e.ToTable("ObjectLabels");
            e.HasIndex(o => o.ClassName).IsUnique();
        });

        b.Entity<SystemUser>(e =>
        {
            e.ToTable("SystemUsers");
            e.HasIndex(u => u.Username).IsUnique();
        });

        b.Entity<ConfigSnapshot>(e =>
        {
            e.ToTable("ConfigSnapshots");
            e.HasIndex(s => s.SavedAt);
        });

        b.Entity<RecognitionEvent>(e =>
        {
            e.ToTable("RecognitionEvents");
            e.HasIndex(r => r.OccurredAt);
            e.HasIndex(r => new { r.CameraId, r.OccurredAt });
            e.HasIndex(r => r.PlateText);

            e.HasOne(r => r.Person)
             .WithMany()
             .HasForeignKey(r => r.PersonId)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(r => r.Vehicle)
             .WithMany()
             .HasForeignKey(r => r.VehicleId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        base.OnModelCreating(b);
    }
}
