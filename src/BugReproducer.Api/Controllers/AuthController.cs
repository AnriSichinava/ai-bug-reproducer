using BugReproducer.Api.Auth;
using BugReproducer.Api.Data;
using BugReproducer.Api.Dtos;
using BugReproducer.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BugReproducer.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    AppDbContext db,
    PasswordHasher<User> passwordHasher,
    TokenService tokenService) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
    {
        var emailExists = await db.Users.AnyAsync(u => u.Email == request.Email);
        if (emailExists)
        {
            return Conflict("A user with this email already exists.");
        }

        var user = new User
        {
            Email = request.Email,
            PasswordHash = string.Empty,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = tokenService.CreateToken(user);
        return Ok(new AuthResponse(token, user.Email));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == request.Email);
        if (user is null)
        {
            return Unauthorized("Invalid email or password.");
        }

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return Unauthorized("Invalid email or password.");
        }

        var token = tokenService.CreateToken(user);
        return Ok(new AuthResponse(token, user.Email));
    }
}
