using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hubix.Infrastructure.Configurations;

public class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.Property(a => a.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(a => a.FileName).HasMaxLength(300).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(150);
        builder.Property(a => a.Caption).HasMaxLength(500);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasOne(a => a.Project)
            .WithMany(p => p.Assets)
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.ProjectId, a.Kind });
        builder.HasIndex(a => a.StorageKey).IsUnique();
    }
}

public class UnitAssetConfiguration : IEntityTypeConfiguration<UnitAsset>
{
    public void Configure(EntityTypeBuilder<UnitAsset> builder)
    {
        builder.HasOne(ua => ua.Unit)
            .WithMany(u => u.Assets)
            .HasForeignKey(ua => ua.UnitId)
            .OnDelete(DeleteBehavior.Cascade);

        // NoAction for the same reason as Floors: Project cascades to both Units and Assets,
        // which would give UnitAssets two cascade paths. Deleting an asset that a unit still
        // references is blocked, so the link has to be removed first.
        builder.HasOne(ua => ua.Asset)
            .WithMany(a => a.Units)
            .HasForeignKey(ua => ua.AssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(ua => new { ua.UnitId, ua.AssetId }).IsUnique();
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.Property(a => a.ActorType).HasMaxLength(20).IsRequired();
        builder.Property(a => a.ActorLabel).HasMaxLength(200);
        builder.Property(a => a.EntityType).HasMaxLength(50).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.IpAddress).HasMaxLength(45); // IPv6 maximum
        builder.Property(a => a.Before).HasColumnType("nvarchar(max)");
        builder.Property(a => a.After).HasColumnType("nvarchar(max)");

        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.CreatedAt })
            .HasDatabaseName("IX_AuditLog_Entity");

        builder.HasIndex(a => a.CreatedAt);
    }
}
