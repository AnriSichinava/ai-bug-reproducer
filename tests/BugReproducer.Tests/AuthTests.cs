using System.Net;
using System.Net.Http.Json;
using BugReproducer.Api.Dtos;

namespace BugReproducer.Tests;

public class AuthTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Register_ReturnsToken()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("register-ok@example.com", "Password123!"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        Assert.Equal("register-ok@example.com", body.Email);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsConflict()
    {
        var request = new RegisterRequest("duplicate@example.com", "Password123!");
        await _client.PostAsJsonAsync("/api/auth/register", request);

        var response = await _client.PostAsJsonAsync("/api/auth/register", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email", "Password123!")]
    [InlineData("valid@example.com", "short")]
    public async Task Register_WithInvalidPayload_ReturnsBadRequest(string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("wrong-password@example.com", "Password123!"));

        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("wrong-password@example.com", "NotThePassword1!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("nobody@example.com", "Password123!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithToken_ReturnsCallersEmail()
    {
        var client = await factory.CreateAuthenticatedClientAsync("me@example.com");

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("me@example.com", body!["email"]);
    }
}
