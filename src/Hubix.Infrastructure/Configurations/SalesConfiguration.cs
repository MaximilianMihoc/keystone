using System.Text.Json;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hubix.Infrastructure.Configurations;

public class ProspectConfiguration : IEntityTypeConfiguration<Prospect>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Prospect> builder)
    {
        builder.Property(p => p.Email).HasMaxLength(320).IsRequired();
        builder.Property(p => p.FullName).HasMaxLength(200);
        builder.Property(p => p.Phone).HasMaxLength(50);
        builder.Property(p => p.Locale).HasMaxLength(10).IsRequired();
        builder.Property(p => p.Source).HasMaxLength(50);
        builder.Property(p => p.GdprConsentSource).HasMaxLength(200);
        builder.Property(p => p.BlockReason).HasMaxLength(500);

        // SQL Server's default collation is case-insensitive, so this unique index already
        // treats Ion@example.ro and ion@example.ro as the same person. Postgres would need
        // citext here; on SQL Server it comes free.
        builder.HasIndex(p => p.Email).IsUnique();

        builder.Property(p => p.GdprConsentPurpose)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>())
            .HasColumnType("nvarchar(max)")
            .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
                v => v.ToList()));
    }
}

public class StaffUserConfiguration : IEntityTypeConfiguration<StaffUser>
{
    public void Configure(EntityTypeBuilder<StaffUser> builder)
    {
        builder.Property(u => u.Email).HasMaxLength(320).IsRequired();
        builder.Property(u => u.Name).HasMaxLength(200).IsRequired();
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();
    }
}

public class HoldConfiguration : IEntityTypeConfiguration<Hold>
{
    public void Configure(EntityTypeBuilder<Hold> builder)
    {
        builder.Property(h => h.ReleaseReason).HasConversion<string>().HasMaxLength(32);

        builder.HasOne(h => h.Unit)
            .WithMany(u => u.Holds)
            .HasForeignKey(h => h.UnitId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(h => h.Prospect)
            .WithMany(p => p.Holds)
            .HasForeignKey(h => h.ProspectId)
            .OnDelete(DeleteBehavior.Restrict);

        // A unit may have many historical holds but only one live one. A filtered unique
        // index makes that a database guarantee rather than application discipline — belt
        // and braces alongside the guarded UPDATE in the hold service.
        builder.HasIndex(h => h.UnitId)
            .IsUnique()
            .HasFilter("[ReleasedAt] IS NULL")
            .HasDatabaseName("UX_Holds_OneActivePerUnit");

        // The expiry job sweeps on this every minute.
        builder.HasIndex(h => new { h.ExpiresAt, h.ReleasedAt })
            .HasDatabaseName("IX_Holds_Expiry");
    }
}

public class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.Property(r => r.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.BuyerType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.ContractNumber).HasMaxLength(100);
        builder.Property(r => r.NotaryLocation).HasMaxLength(300);
        builder.Property(r => r.InternalNotes).HasMaxLength(4000);
        builder.Property(r => r.CancellationReason).HasMaxLength(1000);

        builder.HasOne(r => r.Unit)
            .WithMany(u => u.Reservations)
            .HasForeignKey(r => r.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Prospect)
            .WithMany(p => p.Reservations)
            .HasForeignKey(r => r.ProspectId)
            .OnDelete(DeleteBehavior.Restrict);

        // The agreed price, frozen. Never recomputed: Hubix raises prices mid-launch and an
        // agreed reservation must not move with them.
        builder.Property(r => r.SnapTranches)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<List<TrancheAmount>>(v, JsonOptions)
                     ?? new List<TrancheAmount>())
            .HasColumnType("nvarchar(max)")
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<TrancheAmount>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                v => v.Aggregate(0, (acc, t) => HashCode.Combine(acc, t.GetHashCode())),
                v => v.ToList()));

        builder.HasIndex(r => r.State);
        builder.HasIndex(r => r.ContractNumber);

        // One open reservation per unit. A cancelled one may sit alongside a live one.
        builder.HasIndex(r => r.UnitId)
            .IsUnique()
            .HasFilter("[CancelledAt] IS NULL AND [State] <> 'Cancelled'")
            .HasDatabaseName("UX_Reservations_OneOpenPerUnit");
    }
}

public class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.HasOne(w => w.Project)
            .WithMany()
            .HasForeignKey(w => w.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(w => w.UnitType)
            .WithMany()
            .HasForeignKey(w => w.UnitTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(w => w.Prospect)
            .WithMany(p => p.WaitlistEntries)
            .HasForeignKey(w => w.ProspectId)
            .OnDelete(DeleteBehavior.Restrict);

        // Somebody may only queue once per type, but may rejoin after being removed.
        builder.HasIndex(w => new { w.ProjectId, w.UnitTypeId, w.ProspectId })
            .IsUnique()
            .HasFilter("[RemovedAt] IS NULL")
            .HasDatabaseName("UX_Waitlist_OneActivePerProspect");

        builder.HasIndex(w => new { w.ProjectId, w.UnitTypeId, w.Position });
    }
}

public class EnquiryConfiguration : IEntityTypeConfiguration<Enquiry>
{
    public void Configure(EntityTypeBuilder<Enquiry> builder)
    {
        builder.Property(e => e.Message).HasMaxLength(4000).IsRequired();
        builder.Property(e => e.AnsweredBy).HasMaxLength(200);

        builder.HasOne(e => e.Project).WithMany()
            .HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.Prospect).WithMany()
            .HasForeignKey(e => e.ProspectId).OnDelete(DeleteBehavior.SetNull);
    }
}
