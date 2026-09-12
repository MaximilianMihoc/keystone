using System.Text.Json;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hubix.Infrastructure.Configurations;

public class DeveloperConfiguration : IEntityTypeConfiguration<Developer>
{
    public void Configure(EntityTypeBuilder<Developer> builder)
    {
        builder.Property(d => d.Name).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Website).HasMaxLength(500);
        builder.HasIndex(d => d.Name).IsUnique();
    }
}

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.Property(p => p.Slug).HasMaxLength(120).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.City).HasMaxLength(120).IsRequired();
        builder.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        builder.Property(p => p.MapsUrl).HasMaxLength(1000);
        builder.Property(p => p.SalesInbox).HasMaxLength(320);

        // Enums are stored as strings. A few bytes more than an int, and the database stays
        // readable without a lookup join when someone inspects it directly.
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(p => p.RentalProfile).HasConversion<string>().HasMaxLength(32);

        builder.HasIndex(p => p.Slug).IsUnique();
        builder.HasIndex(p => p.Status);

        builder.HasOne(p => p.Developer)
            .WithMany(d => d.Projects)
            .HasForeignKey(p => p.DeveloperId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Projects_HoldDurationHours",
            "[HoldDurationHours] > 0 AND [HoldDurationHours] <= 720"));
    }
}

public class PricingRuleConfiguration : IEntityTypeConfiguration<PricingRule>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<PricingRule> builder)
    {
        builder.HasOne(r => r.Project)
            .WithMany(p => p.PricingRules)
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // The tranche schedule is a list, not columns. That is what lets
        // a project paying in two instalments and one paying in three share a schema.
        builder.Property(r => r.Tranches)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<List<TrancheDefinition>>(v, JsonOptions)
                     ?? new List<TrancheDefinition>())
            .HasColumnType("nvarchar(max)")
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<TrancheDefinition>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                v => v.Aggregate(0, (acc, t) => HashCode.Combine(acc, t.GetHashCode())),
                v => v.ToList()));

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_PricingRules_Json",
            "ISJSON([Tranches]) = 1"));

        builder.HasIndex(r => new { r.ProjectId, r.EffectiveFrom });
    }
}
