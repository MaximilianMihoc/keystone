using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hubix.Infrastructure.Configurations;

public class ParkingSpaceConfiguration : IEntityTypeConfiguration<ParkingSpace>
{
    public void Configure(EntityTypeBuilder<ParkingSpace> builder)
    {
        builder.Property(s => s.Code).HasMaxLength(32).IsRequired();
        builder.Property(s => s.AdminNotes).HasMaxLength(2000);

        builder.Property(s => s.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasOne(s => s.Project)
            .WithMany(p => p.ParkingSpaces)
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // NoAction on both, for two reasons. The space outlives the hold and the sale — it goes
        // back on the market rather than being deleted with them. And SQL Server refuses the
        // cascade cycle these would otherwise close: Project cascades to Units, Units to Holds,
        // Holds would reach ParkingSpaces, which Project already cascades to directly.
        //
        // It follows that anything deleting a hold or a reservation — GDPR erasure is the only
        // thing that does — has to free the spaces first. PrivacyService does.
        builder.HasOne(s => s.Hold)
            .WithMany(h => h.ParkingSpaces)
            .HasForeignKey(s => s.HoldId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(s => s.Reservation)
            .WithMany(r => r.ParkingSpaces)
            .HasForeignKey(s => s.ReservationId)
            .OnDelete(DeleteBehavior.NoAction);

        // The numbering system, enforced rather than hoped for: "A1" means one space.
        builder.HasIndex(s => new { s.ProjectId, s.Code })
            .IsUnique()
            .HasDatabaseName("UX_ParkingSpaces_ProjectCode");

        // The shape of the claim query: free spaces of one kind on one project.
        builder.HasIndex(s => new { s.ProjectId, s.State, s.Kind });

        builder.HasIndex(s => s.HoldId);
        builder.HasIndex(s => s.ReservationId);
    }
}

public class ParkingPriceConfiguration : IEntityTypeConfiguration<ParkingPrice>
{
    public void Configure(EntityTypeBuilder<ParkingPrice> builder)
    {
        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasOne(p => p.Project)
            .WithMany(p => p.ParkingPrices)
            .HasForeignKey(p => p.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // One price per kind per project. Two rows for underground is a disagreement about what
        // a space costs, and there is no honest way to pick between them.
        builder.HasIndex(p => new { p.ProjectId, p.Kind })
            .IsUnique()
            .HasDatabaseName("UX_ParkingPrices_ProjectKind");
    }
}
