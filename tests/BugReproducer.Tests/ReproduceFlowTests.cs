using System.Net.Http.Json;
using BugReproducer.Api.Dtos;
using BugReproducer.Api.Models;
using BugReproducer.Tests.Agent;
using Microsoft.Extensions.DependencyInjection;
using AgentLib = BugReproducer.Agent;

namespace BugReproducer.Tests;

/// Proves the background wiring end to end: POST /reproduce enqueues real work, a
/// hosted service actually executes ReproductionAgent against a scripted model, steps
/// land in the database as they happen, and the run's final state matches what the
/// model concluded. This is the one thing the unit-level ReproductionAgentTests can't
/// show, because those never go through the queue, the DbContext, or EfAgentStepSink.
public class ReproduceFlowTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    [Fact]
    public async Task AReproducedVerdict_FlowsThroughToTheStoredAgentRunAndItsSteps()
    {
        var backends = factory.Services.GetRequiredService<TestAgentBackends>();
        backends.MessagesClient = new ScriptedMessagesClient(
            ModelResponses.ToolCall("navigate", new { url = "http://localhost:4000" }),
            ModelResponses.Conclude("REPRODUCED", "Observed a 500 on removal.", (1, "DELETE returned 500")));
        backends.BrowserTools = new FakeBrowserTools();

        var client = await factory.CreateAuthenticatedClientAsync("reproduce-flow@example.com");
        var run = await CreateRunAsync(client, "Cart crash after re-login");

        var completed = await PollUntilFinishedAsync(client, run.Id);

        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal(ReproductionVerdict.Reproduced, completed.Verdict);
        Assert.Equal("Observed a 500 on removal.", completed.VerdictReason);

        var detail = await client.GetFromJsonAsync<AgentRunDetailResponse>($"/api/agent-runs/{run.Id}");
        Assert.Equal(2, detail!.Steps.Count); // the navigate call, then the conclude call
        Assert.Equal("navigate", detail.Steps[0].ToolName);
        Assert.Equal("conclude", detail.Steps[1].ToolName);
    }

    [Fact]
    public async Task AFailedModelCall_MarksTheRunFailedRatherThanLeavingItStuck()
    {
        var backends = factory.Services.GetRequiredService<TestAgentBackends>();
        backends.MessagesClient = new ThrowingMessagesClient();
        backends.BrowserTools = new FakeBrowserTools();

        var client = await factory.CreateAuthenticatedClientAsync("reproduce-flow-failure@example.com");
        var run = await CreateRunAsync(client, "Some other bug");

        var completed = await PollUntilFinishedAsync(client, run.Id);

        Assert.Equal(AgentRunStatus.Failed, completed.Status);
        Assert.Null(completed.Verdict);
        Assert.Contains("simulated outage", completed.FailureReason);
    }

    private static async Task<AgentRunResponse> PollUntilFinishedAsync(HttpClient client, int runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var run = await client.GetFromJsonAsync<AgentRunDetailResponse>($"/api/agent-runs/{runId}");
            if (run!.Status is AgentRunStatus.Completed or AgentRunStatus.Failed)
            {
                return new AgentRunResponse(
                    run.Id, run.BugReportId, run.Status, run.Verdict, run.VerdictReason,
                    run.FailureReason, run.StartedAtUtc, run.CompletedAtUtc);
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Agent run {runId} did not finish within the test timeout.");
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

    private class ThrowingMessagesClient : AgentLib.IMessagesClient
    {
        public Task<Anthropic.Models.Messages.Message> CreateAsync(
            Anthropic.Models.Messages.MessageCreateParams parameters, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated outage");
    }
}
