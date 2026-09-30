using System.Net.Http.Headers;
using System.Net.Http.Json;
using BugReproducer.Api.Dtos;

namespace BugReproducer.Tests;

public static class TestApiFactoryExtensions
{
    public static async Task<HttpClient> CreateAuthenticatedClientAsync(
        this TestApiFactory factory,
        string email,
        string password = "Password123!")
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, password));
        response.EnsureSuccessStatusCode();

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        return client;
    }
}
