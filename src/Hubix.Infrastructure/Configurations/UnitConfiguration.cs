using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hubix.Infrastructure.Configurations;

public class UnitTypeConfiguration : IEntityTypeConfiguration<UnitType>
{
    public void Configure(EntityTypeBuilder<UnitType> builder)
    {
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();

        builder.HasOne(t => t.Project)
            .WithMany(p => p.UnitTypes)
            .HasForeignKey(t => t.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.ProjectId, t.Name }).IsUnique();
    }
}

public class FloorConfiguration : IEntityTypeConfiguration<Floor>
{
    public void Configure(EntityTypeBuilder<Floor> builder)
    {
        builder.Property(f => f.Building).HasMaxLength(50);
        builder.Property(f => f.Label).HasMaxLength(100);

        builder.HasOne(f => f.Project)
            .WithMany(p => p.Floors)
            .HasForeignKey(f => f.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // NoAction rather than SetNull: SQL Server refuses multiple cascade paths, and
        // Project already cascades to both Assets and Floors. Detaching a floor plan is
        // therefore an explicit step, which is the safer behaviour anyway.
        builder.HasOne(f => f.FloorPlanAsset)
            .WithMany()
            .HasForeignKey(f => f.FloorPlanAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        // HasFilter(null) removes the "[Building] IS NOT NULL" filter EF adds to any unique
        // index over a nullable column. With that filter, a project with one building — where
        // Building is null on every row — had no uniqueness at all, so two rows could both
        // claim floor 1 and "which floor plan?" would have two answers. SQL Server treats
        // nulls as equal in a unique index, which is exactly the rule wanted here.
        builder.HasIndex(f => new { f.ProjectId, f.Building, f.Level })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("UX_Floors_OnePerLevel");
    }
}

public class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.Property(u => u.Code).HasMaxLength(50).IsRequired();
        builder.Property(u => u.Building).HasMaxLength(50);
        builder.Property(u => u.Stair).HasMaxLength(50);
        builder.Property(u => u.Orientation).HasMaxLength(20);
        builder.Property(u => u.AdminNotes).HasMaxLength(2000);

        builder.Property(u => u.State).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(u => u.AreaBuilt).HasColumnType("decimal(8,2)");
        builder.Property(u => u.AreaUsable).HasColumnType("decimal(8,2)");
        builder.Property(u => u.AreaBalcony).HasColumnType("decimal(8,2)");

        // A persisted computed column. Postgres calls this GENERATED ... STORED; SQL Server
        // computes and stores it, so it can never disagree with its inputs. The spreadsheet
        // equivalent is a formula that some rows have lost.
        builder.Property(u => u.AreaTotal)
            .HasColumnType("decimal(8,2)")
            .HasComputedColumnSql("[AreaUsable] + ISNULL([AreaBalcony], 0)", stored: true);

        builder.HasOne(u => u.Project)
            .WithMany(p => p.Units)
            .HasForeignKey(u => u.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(u => u.UnitType)
            .WithMany(t => t.Units)
            .HasForeignKey(u => u.UnitTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        // The importer keys on this pair, so re-running an import updates rather than duplicates.
        builder.HasIndex(u => new { u.ProjectId, u.Code }).IsUnique();

        // The public inventory filters by project and state on every page load.
        builder.HasIndex(u => new { u.ProjectId, u.State });

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Units_BasePriceNet", "[BasePriceNet] >= 0");
            t.HasCheckConstraint("CK_Units_AreaUsable", "[AreaUsable] > 0");
        });
    }
}
