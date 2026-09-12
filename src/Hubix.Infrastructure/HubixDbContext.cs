using Hubix.Domain.Common;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Infrastructure;

public class HubixDbContext(DbContextOptions<HubixDbContext> options) : DbContext(options)
{
    public DbSet<Developer> Developers => Set<Developer>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<PricingRule> PricingRules => Set<PricingRule>();

    public DbSet<UnitType> UnitTypes => Set<UnitType>();

    public DbSet<ProjectTranslation> ProjectTranslations => Set<ProjectTranslation>();

    public DbSet<UnitTypeTranslation> UnitTypeTranslations => Set<UnitTypeTranslation>();

    public DbSet<Floor> Floors => Set<Floor>();

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<ParkingSpace> ParkingSpaces => Set<ParkingSpace>();

    public DbSet<ParkingPrice> ParkingPrices => Set<ParkingPrice>();

    public DbSet<Hold> Holds => Set<Hold>();

    public DbSet<Prospect> Prospects => Set<Prospect>();

    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();

    public DbSet<StaffLoginToken> StaffLoginTokens => Set<StaffLoginToken>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<UnitAsset> UnitAssets => Set<UnitAsset>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Enquiry> Enquiries => Set<Enquiry>();

    public DbSet<PrivacyRequest> PrivacyRequests => Set<PrivacyRequest>();

    public DbSet<ImportProfile> ImportProfiles => Set<ImportProfile>();

    public DbSet<PortalSettings> PortalSettings => Set<PortalSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfigurationsFromAssembly(typeof(HubixDbContext).Assembly);

        // Money is decimal(12,2) everywhere. Setting it centrally means a new decimal
        // property cannot silently default to decimal(18,2) and drift.
        foreach (var property in builder.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            // Rates are fractions (0.1820) and need more places than money does.
            var isRate = property.Name.EndsWith("Pct", StringComparison.Ordinal)
                         || property.Name.Contains("VatRate", StringComparison.Ordinal)
                         || property.Name is "VatStandard" or "VatReduced";

            property.SetColumnType(isRate ? "decimal(6,4)" : "decimal(12,2)");
        }

        base.OnModelCreating(builder);
    }

    public override int SaveChanges()
    {
        StampTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimestamps()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;
            }
        }
    }
}
