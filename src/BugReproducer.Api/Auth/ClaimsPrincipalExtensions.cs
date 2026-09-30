using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BugReproducer.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Token is missing a sub claim.");
        return int.Parse(sub);
    }
}
