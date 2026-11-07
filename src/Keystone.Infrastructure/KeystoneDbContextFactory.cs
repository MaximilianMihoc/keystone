using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Keystone.Infrastructure;

/// <summary>
/// Lets <c>dotnet ef</c> build a context without starting the web host.
/// </summary>
/// <remarks>
/// Reads the connection string from <c>KEYSTONE_DB</c> if present, otherwise from
/// <c>src/Keystone.Web/appsettings.Development.json</c>, otherwise falls back to local
/// SQL Server Express. Design-time only; never used at runtime.
/// </remarks>
public class KeystoneDbContextFactory : IDesignTimeDbContextFactory<KeystoneDbContext>
{
    private const string LocalFallback =
        @"Server=.\SQLEXPRESS;Database=KeystonePortal;Trusted_Connection=True;TrustServerCertificate=True";

    public KeystoneDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("KEYSTONE_DB")
            ?? ReadFromWebProject()
            ?? LocalFallback;

        var options = new DbContextOptionsBuilder<KeystoneDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(KeystoneDbContext).Assembly.FullName))
            .Options;

        return new KeystoneDbContext(options);
    }

    private static string? ReadFromWebProject()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Keystone.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var webPath = Path.Combine(directory.FullName, "src", "Keystone.Web");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(webPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        return configuration.GetConnectionString(DependencyInjection.ConnectionStringName);
    }
}
