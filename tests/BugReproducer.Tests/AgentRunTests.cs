using System.Net;
using System.Net.Http.Json;
using BugReproducer.Api.Dtos;
using BugReproducer.Api.Models;

namespace BugReproducer.Tests;

public class AgentRunTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    [Fact]
    public async Task Reproduce_CreatesPendingRun()
    {
        var client = await factory.CreateAuthenticatedClientAsync("run-create@example.com");
        var bug = await CreateBugAsync(client, "Run create");

        var response = await client.PostAsync($"/api/bugs/{bug.Id}/reproduce", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var run = await response.Content.ReadFromJsonAsync<AgentRunResponse>();
        Assert.Equal(AgentRunStatus.Pending, run!.Status);
        Assert.Null(run.Verdict);
        Assert.Equal(bug.Id, run.BugReportId);
    }

    [Fact]
    public async Task Reproduce_WhileRunInProgress_ReturnsConflict()
    {
        var client = await factory.CreateAuthenticatedClientAsync("run-conflict@example.com");
        var bug = await CreateBugAsync(client, "Run conflict");

        var first = await client.PostAsync($"/api/bugs/{bug.Id}/reproduce", null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsync($"/api/bugs/{bug.Id}/reproduce", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Reproduce_ForUnknownBug_ReturnsNotFound()
    {
        var client = await factory.CreateAuthenticatedClientAsync("run-unknown-bug@example.com");

        var response = await client.PostAsync("/api/bugs/9999/reproduce", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAgentRun_ReturnsRunWithEmptyStepList()
    {
        var client = await factory.CreateAuthenticatedClientAsync("run-detail@example.com");
        var bug = await CreateBugAsync(client, "Run detail");
        var created = await client.PostAsync($"/api/bugs/{bug.Id}/reproduce", null);
        var run = await created.Content.ReadFromJsonAsync<AgentRunResponse>();

        var detail = await client.GetFromJsonAsync<AgentRunDetailResponse>($"/api/agent-runs/{run!.Id}");

        Assert.Equal(run.Id, detail!.Id);
        Assert.Empty(detail.Steps);
    }

    [Fact]
    public async Task AgentRuns_CanBeFilteredByBug()
    {
        var client = await factory.CreateAuthenticatedClientAsync("run-filter@example.com");
        var bugA = await CreateBugAsync(client, "Bug A");
        var bugB = await CreateBugAsync(client, "Bug B");

        await client.PostAsync($"/api/bugs/{bugA.Id}/reproduce", null);
        await client.PostAsync($"/api/bugs/{bugB.Id}/reproduce", null);

        var page = await client.GetFromJsonAsync<PagedResult<AgentRunResponse>>($"/api/agent-runs?bugId={bugA.Id}");

        Assert.Equal(1, page!.TotalCount);
        Assert.Equal(bugA.Id, page.Items.Single().BugReportId);
    }

    [Fact]
    public async Task AgentRuns_AreInvisibleToOtherUsers()
    {
        var alice = await factory.CreateAuthenticatedClientAsync("alice-run@example.com");
        var bob = await factory.CreateAuthenticatedClientAsync("bob-run@example.com");
        var bug = await CreateBugAsync(alice, "Alice's bug");

        var created = await alice.PostAsync($"/api/bugs/{bug.Id}/reproduce", null);
        var run = await created.Content.ReadFromJsonAsync<AgentRunResponse>();

        var bobsRead = await bob.GetAsync($"/api/agent-runs/{run!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, bobsRead.StatusCode);

        var bobsTrigger = await bob.PostAsync($"/api/bugs/{bug.Id}/reproduce", null);
        Assert.Equal(HttpStatusCode.NotFound, bobsTrigger.StatusCode);
    }

    private static async Task<BugReportResponse> CreateBugAsync(HttpClient client, string title)
    {
        var projectResponse = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest($"Project for {title}"));
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var bugResponse = await client.PostAsJsonAsync("/api/bugs",
            new CreateBugReportRequest(project!.Id, title, "Steps to reproduce go here."));
        bugResponse.EnsureSuccessStatusCode();

        return (await bugResponse.Content.ReadFromJsonAsync<BugReportResponse>())!;
    }
}
