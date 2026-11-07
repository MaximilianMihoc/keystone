using System.Text.Json;
using Keystone.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Keystone.Infrastructure.Configurations;

public class PortalSettingsConfiguration : IEntityTypeConfiguration<PortalSettings>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<PortalSettings> builder)
    {
        builder.Property(s => s.SalesInbox).HasMaxLength(320);
        builder.Property(s => s.RedirectAllTo).HasMaxLength(320);
        builder.Property(s => s.PublicBaseUrl).HasMaxLength(500);

        // Same shape as the consent purposes on Prospect: a short list that is only ever read
        // and written whole, so a table of its own would buy nothing.
        Patterns(builder.Property(s => s.AllowedRecipients));
        Patterns(builder.Property(s => s.BlockedRecipients));
    }

    private static void Patterns(PropertyBuilder<List<string>> property) =>
        property
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>())
            .HasColumnType("nvarchar(max)")
            .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
                v => v.ToList()));
}
