using BugReproducer.Agent;
using BugReproducer.Api.Auth;
using BugReproducer.Api.Data;
using BugReproducer.Api.Models;
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
}
