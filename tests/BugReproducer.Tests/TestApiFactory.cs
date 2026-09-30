using BugReproducer.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BugReproducer.Tests;

/// Boots the real API in-memory, backed by a private SQLite database per factory instance.
public class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Keep tests hermetic: "Testing" avoids loading appsettings.Development.json,
        // which is gitignored and therefore absent in CI.
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "unused-by-tests",
                ["Jwt:Secret"] = "test-secret-key-that-is-long-enough-for-hmac-sha256",
                ["Jwt:Issuer"] = "BugReproducer.Api",
                ["Jwt:Audience"] = "BugReproducer.Client",
                ["Jwt:ExpiryMinutes"] = "60",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<AppDbContext>();

            // EF Core 10 registers the provider through IDbContextOptionsConfiguration<T>
            // descriptors that accumulate, so the SQL Server one must be removed or both
            // providers end up configured on the same context.
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();

            // SQLite in-memory lives only as long as its connection is open, so the
            // factory owns a single connection shared by every scoped DbContext.
            _connection.Open();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
