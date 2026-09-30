using BugReproducer.Agent;
using BugReproducer.Api.Data;
using BugReproducer.Tests.Agent;
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
    // A literal shared SqliteConnection object works for sequential access, but the
    // background task queue now runs real work on its own thread concurrently with the
    // test's HTTP calls, and two threads initializing the same connection object at once
    // hits a native SQLite conflict ("unable to delete/modify user-function due to
    // active statements"). A named shared-cache in-memory database lets every DbContext
    // open its own connection while still pointing at the same in-memory data, which is
    // the standard way to make EF Core SQLite in-memory tests safe under concurrency.
    private readonly string _connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAliveConnection;

    public TestApiFactory()
    {
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();
    }

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

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connectionString));

            // The real runner shells out to `npx playwright test`; default tests must
            // not depend on that (or on the e2e/ directory even existing at test time).
            services.RemoveAll<IPlaywrightTestRunner>();
            services.AddSingleton<IPlaywrightTestRunner>(new FakePlaywrightTestRunner());

            // The real IMessagesClient/IBrowserTools would call the actual Anthropic API
            // and spawn a real MCP/Playwright process. Route both through a holder tests
            // can reconfigure (see TestAgentBackends); defaults to a client that never
            // resolves, so unrelated tests' Pending/Running assertions aren't racing a
            // background run that happens to finish first.
            services.AddSingleton<TestAgentBackends>();
            services.RemoveAll<IMessagesClient>();
            services.AddScoped<IMessagesClient>(sp => sp.GetRequiredService<TestAgentBackends>().MessagesClient);
            services.RemoveAll<IBrowserTools>();
            services.AddScoped<IBrowserTools>(sp => sp.GetRequiredService<TestAgentBackends>().BrowserTools);
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
            _keepAliveConnection.Dispose();
        }
    }
}
