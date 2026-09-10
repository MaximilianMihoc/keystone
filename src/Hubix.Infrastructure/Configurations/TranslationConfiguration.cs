using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hubix.Infrastructure.Configurations;

public class ProjectTranslationConfiguration : IEntityTypeConfiguration<ProjectTranslation>
{
    public void Configure(EntityTypeBuilder<ProjectTranslation> builder)
    {
        builder.Property(t => t.Culture).HasMaxLength(10).IsRequired();
        builder.Property(t => t.City).HasMaxLength(120);
        builder.Property(t => t.Summary).HasMaxLength(2000);
        builder.Property(t => t.Body).HasColumnType("nvarchar(max)");
        builder.Property(t => t.PenaltyClause).HasMaxLength(2000);
        builder.Property(t => t.Parking).HasMaxLength(400);
        builder.Property(t => t.ReservationNotice).HasMaxLength(2000);

        builder.HasOne(t => t.Project)
            .WithMany(p => p.Translations)
            .HasForeignKey(t => t.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per project per culture.
        builder.HasIndex(t => new { t.ProjectId, t.Culture }).IsUnique();
    }
}

public class UnitTypeTranslationConfiguration : IEntityTypeConfiguration<UnitTypeTranslation>
{
    public void Configure(EntityTypeBuilder<UnitTypeTranslation> builder)
    {
        builder.Property(t => t.Culture).HasMaxLength(10).IsRequired();
        builder.Property(t => t.DisplayName).HasMaxLength(100).IsRequired();

        builder.HasOne(t => t.UnitType)
            .WithMany(u => u.Translations)
            .HasForeignKey(t => t.UnitTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.UnitTypeId, t.Culture }).IsUnique();
    }
}
