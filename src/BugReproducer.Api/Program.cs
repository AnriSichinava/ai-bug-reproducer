using Anthropic;
using BugReproducer.Agent;
using BugReproducer.Api.Auth;
using BugReproducer.Api.Data;
using BugReproducer.Api.Models;
using BugReproducer.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .ValidateOnStart();

builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter the JWT token returned from /api/auth/login.",
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<PasswordHasher<User>>();
builder.Services.AddSingleton<TokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.ConfigureOptions<ConfigureJwtBearerOptions>();
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IPlaywrightTestRunner>(_ =>
{
    var configuredPath = builder.Configuration["E2eDirectory"];
    var e2eDirectory = string.IsNullOrWhiteSpace(configuredPath)
        ? FindE2eDirectory(builder.Environment.ContentRootPath)
        : configuredPath;
    return new PlaywrightTestRunner(e2eDirectory);
});

// --- Agent wiring ---
// Background queue: the endpoint returns 202 immediately and the actual reproduction
// runs afterwards, off the request thread. See Services/QueuedHostedService.cs.
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<QueuedHostedService>();

builder.Services.AddSingleton(new AgentOptions
{
    TargetBaseUrl = builder.Configuration["TargetApp:BaseUrl"] ?? "http://localhost:4000",
});

// AnthropicClient reads ANTHROPIC_API_KEY from the environment itself; nothing to wire
// here beyond constructing it. IMessagesClient is the seam the tests replace.
builder.Services.AddSingleton<AnthropicClient>();
builder.Services.AddScoped<IMessagesClient, AnthropicMessagesClient>();

builder.Services.AddScoped<IBrowserTools>(_ =>
{
    var configuredPath = builder.Configuration["McpToolsDistPath"];
    var distPath = string.IsNullOrWhiteSpace(configuredPath)
        ? FindMcpToolsDistPath(builder.Environment.ContentRootPath)
        : configuredPath;

    // This runs inside a background work item, never on a request thread, so blocking
    // here to start the MCP server subprocess doesn't risk starving request handling.
    return BrowserToolsClient.ConnectAsync(distPath).GetAwaiter().GetResult();
});

builder.Services.AddScoped<ReproductionAgent>();
builder.Services.AddScoped<AgentRunExecutor>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Top-level statements compile into an internal Program class; WebApplicationFactory<T>
// needs it to be accessible, so expose it explicitly for the integration tests.
public partial class Program
{
    /// Only works when the API runs with the repo checked out on disk (e.g. `dotnet
    /// run` from source) -- inside the api container this directory isn't mounted, so
    /// test generation is a host-only dev feature for now, not something the
    /// containerized deployment supports.
    private static string FindE2eDirectory(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "e2e")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Could not locate the e2e directory from the content root.")
            : Path.Combine(dir.FullName, "e2e");
    }

    /// Same host-only caveat as FindE2eDirectory: the containerized api service has no
    /// mcp-tools directory mounted, so actually running a reproduction is a host-only
    /// dev feature for now.
    private static string FindMcpToolsDistPath(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "mcp-tools")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Could not locate the mcp-tools directory from the content root.")
            : Path.Combine(dir.FullName, "mcp-tools", "dist", "index.js");
    }
}
