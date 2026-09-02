using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Hubix.Infrastructure;

/// <summary>
/// Lets <c>dotnet ef</c> build a context without starting the web host.
/// </summary>
/// <remarks>
/// Reads the connection string from <c>HUBIX_DB</c> if present, otherwise from
/// <c>src/Hubix.Web/appsettings.Development.json</c>, otherwise falls back to local
/// SQL Server Express. Design-time only; never used at runtime.
/// </remarks>
public class HubixDbContextFactory : IDesignTimeDbContextFactory<HubixDbContext>
{
    private const string LocalFallback =
        @"Server=.\SQLEXPRESS;Database=HubixPortal;Trusted_Connection=True;TrustServerCertificate=True";

    public HubixDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("HUBIX_DB")
            ?? ReadFromWebProject()
            ?? LocalFallback;

        var options = new DbContextOptionsBuilder<HubixDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(HubixDbContext).Assembly.FullName))
            .Options;

        return new HubixDbContext(options);
    }

    private static string? ReadFromWebProject()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hubix.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var webPath = Path.Combine(directory.FullName, "src", "Hubix.Web");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(webPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        return configuration.GetConnectionString(DependencyInjection.ConnectionStringName);
    }
}
