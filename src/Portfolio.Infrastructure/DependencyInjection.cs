using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers <see cref="PortfolioDbContext"/> against the "Portfolio" connection string.
    /// The same registration works for both environments: locally it resolves the Windows-auth
    /// SQLEXPRESS string from <c>appsettings.Development.json</c>; in the Podman container it
    /// resolves the SQL-auth string supplied via the <c>ConnectionStrings__Portfolio</c>
    /// environment variable, which the standard configuration provider chain overrides
    /// appsettings.json with automatically. Never hardcode a connection string here.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Portfolio")
            ?? throw new InvalidOperationException(
                "Connection string 'Portfolio' is not configured. Set it in appsettings.Development.json " +
                "for local runs or via the ConnectionStrings__Portfolio environment variable in containers.");

        services.AddDbContext<PortfolioDbContext>(options => options.UseSqlServer(
            connectionString,
            sql => sql.MigrationsAssembly(typeof(PortfolioDbContext).Assembly.FullName)));

        return services;
    }
}
