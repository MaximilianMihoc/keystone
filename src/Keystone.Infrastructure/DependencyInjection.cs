using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Keystone.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "KeystoneDb";

    public static IServiceCollection AddKeystoneInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. " +
                "Copy appsettings.Development.example.json to appsettings.Development.json.");

        services.AddDbContext<KeystoneDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(KeystoneDbContext).Assembly.FullName);
                sql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(5), null);
            }));

        return services;
    }
}
