using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BugReproducer.Api.Auth;

/// Builds JwtBearerOptions from the bound JwtOptions rather than reading configuration
/// eagerly at startup, so token signing and token validation always agree on the key.
public class ConfigureJwtBearerOptions(IOptions<JwtOptions> jwtOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public void Configure(JwtBearerOptions options)
    {
        // Without this, the handler remaps short claim names ("sub", "email") to long
        // ClaimTypes URIs, so JwtRegisteredClaimNames.Sub lookups fail after validation.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = _jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }

    public void Configure(string? name, JwtBearerOptions options) => Configure(options);
}
