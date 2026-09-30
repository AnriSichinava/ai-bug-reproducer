using System.Net;
using System.Net.Http.Json;
using BugReproducer.Api.Data;
using BugReproducer.Api.Dtos;
using BugReproducer.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BugReproducer.Tests;

public class GenerateTestEndpointTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    [Fact]
    public async Task GeneratingATestForAReproducedRun_PersistsTheTestAndItsResult()
    {
        var client = await factory.CreateAuthenticatedClientAsync("generate-success@example.com");
        var run = await CreateRunAsync(client, "Cart crash after re-login");

        // The API has no public path to a REPRODUCED verdict yet (the reproduce endpoint
        // only creates a Pending run -- running the real agent loop against it is a
        // background-execution concern for a later milestone), so this test drives the
        // run to that state directly, the same way a completed agent run eventually will.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var agentRun = await db.AgentRuns.SingleAsync(r => r.Id == run.Id);
            agentRun.Status = AgentRunStatus.Completed;
            agentRun.Verdict = ReproductionVerdict.Reproduced;
            agentRun.VerdictReason = "Observed a 500 after removing a cart item post re-login.";
            db.AgentSteps.AddRange(
                new AgentStep
                {
                    AgentRunId = run.Id, StepNumber = 1, ToolName = "navigate",
                    ToolInputJson = """{"url":"http://localhost:4000/cart.html"}""",
                    ToolOutputJson = """{"status":200}""",
                },
                new AgentStep
                {
                    AgentRunId = run.Id, StepNumber = 2, ToolName = "click",
                    ToolInputJson = """{"selector":"#remove-item-1"}""",
                    ToolOutputJson = """{"clicked":"#remove-item-1"}""",
                },
                new AgentStep
                {
                    AgentRunId = run.Id, StepNumber = 3, ToolName = "get_network_log",
                    ToolInputJson = "{}",
                    ToolOutputJson = """[{"method":"DELETE","url":"http://localhost:4000/api/cart/items/1","status":500}]""",
                });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/api/agent-runs/{run.Id}/generate-test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<GenerateTestResponse>();
        Assert.True(body!.Passed); // the fake runner always reports success; see FakePlaywrightTestRunner
        Assert.Contains("page.click(\"#remove-item-1\")", body.SourceCode);
        Assert.Contains("toBeLessThan(400)", body.SourceCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await verifyDb.GeneratedTests.AnyAsync(g => g.Id == body.GeneratedTestId));
        Assert.True(await verifyDb.TestRuns.AnyAsync(t => t.Id == body.TestRunId && t.GeneratedTestId == body.GeneratedTestId));
    }

    [Fact]
    public async Task GeneratingATestForAPendingRun_ReturnsConflict()
    {
        var client = await factory.CreateAuthenticatedClientAsync("generate-pending@example.com");
        var run = await CreateRunAsync(client, "Pending run");

        var response = await client.PostAsync($"/api/agent-runs/{run.Id}/generate-test", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GeneratingATestForAnUnknownRun_ReturnsNotFound()
    {
        var client = await factory.CreateAuthenticatedClientAsync("generate-unknown@example.com");

        var response = await client.PostAsync("/api/agent-runs/9999/generate-test", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GeneratingATestForAnotherUsersRun_ReturnsNotFound()
    {
        var alice = await factory.CreateAuthenticatedClientAsync("alice-generate@example.com");
        var bob = await factory.CreateAuthenticatedClientAsync("bob-generate@example.com");
        var run = await CreateRunAsync(alice, "Alice's run");

        var response = await bob.PostAsync($"/api/agent-runs/{run.Id}/generate-test", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<AgentRunResponse> CreateRunAsync(HttpClient client, string title)
    {
        var projectResponse = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest($"Project for {title}"));
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var bugResponse = await client.PostAsJsonAsync("/api/bugs",
            new CreateBugReportRequest(project!.Id, title, "Steps to reproduce go here."));
        bugResponse.EnsureSuccessStatusCode();
        var bug = await bugResponse.Content.ReadFromJsonAsync<BugReportResponse>();

        var runResponse = await client.PostAsync($"/api/bugs/{bug!.Id}/reproduce", null);
        runResponse.EnsureSuccessStatusCode();
        return (await runResponse.Content.ReadFromJsonAsync<AgentRunResponse>())!;
    }
}
