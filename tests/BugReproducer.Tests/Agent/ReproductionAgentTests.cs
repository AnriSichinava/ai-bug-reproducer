using BugReproducer.Agent;

namespace BugReproducer.Tests.Agent;

/// Covers the loop's control flow: how it ends, what it records, and how it treats the
/// model's claims. No API key needed -- the model is scripted, so these assertions are
/// about our code's behaviour, not Claude's.
public class ReproductionAgentTests
{
    private static readonly BugReportContext Bug = new(
        "Cart crash after re-login",
        "Removing an item after logging out and back in returns a 500.");

    private static ReproductionAgent BuildAgent(ScriptedMessagesClient model, IBrowserTools tools, int maxIterations = 10)
        => new(model, tools, new AgentOptions { TargetBaseUrl = "http://localhost:4000", MaxIterations = maxIterations });

    [Fact]
    public async Task ConcludingEndsTheRunWithTheModelsVerdict()
    {
        var model = new ScriptedMessagesClient(
            ModelResponses.ToolCall("navigate", new { url = "http://localhost:4000" }),
            ModelResponses.Conclude("REPRODUCED", "The delete endpoint fails after re-login.", (1, "DELETE returned 500")));
        var sink = new CollectingStepSink();

        var outcome = await BuildAgent(model, new FakeBrowserTools()).RunAsync(Bug, sink);

        Assert.Equal(ReproductionVerdict.Reproduced, outcome.Verdict);
        Assert.Equal(StopReason.ModelConcluded, outcome.StopReason);
        Assert.Equal("The delete endpoint fails after re-login.", outcome.Reasoning);
        Assert.Equal("DELETE returned 500", outcome.Evidence.Single().Observation);
    }

    [Fact]
    public async Task ToolCallsAreExecutedAndRecordedWithTheirOutput()
    {
        var tools = new FakeBrowserTools().Returns("get_network_log", true, """[{"status":500}]""");
        var model = new ScriptedMessagesClient(
            ModelResponses.ToolCall("get_network_log", new { }, thinking: "Checking for failed requests."),
            ModelResponses.Conclude("REPRODUCED", "500 observed.", (1, "status 500")));
        var sink = new CollectingStepSink();

        await BuildAgent(model, tools).RunAsync(Bug, sink);

        Assert.Equal("get_network_log", tools.CalledTools.Single());
        var toolStep = sink.Steps.First(s => s.ToolName == "get_network_log");
        Assert.Equal("""[{"status":500}]""", toolStep.ToolOutputJson);
        Assert.Null(toolStep.ToolErrorMessage);
        // The turn's reasoning is attached to the step it produced, keeping the model's
        // words next to the output they led to.
        Assert.Equal("Checking for failed requests.", toolStep.Reasoning);
    }

    [Fact]
    public async Task AFailingToolIsRecordedAsAnErrorRatherThanThrowing()
    {
        var tools = new FakeBrowserTools().Returns("navigate", false, "Could not navigate: connection refused");
        var model = new ScriptedMessagesClient(
            ModelResponses.ToolCall("navigate", new { url = "http://localhost:4000" }),
            ModelResponses.Conclude("INCONCLUSIVE", "The app was unreachable."));
        var sink = new CollectingStepSink();

        var outcome = await BuildAgent(model, tools).RunAsync(Bug, sink);

        var step = sink.Steps.First(s => s.ToolName == "navigate");
        Assert.Equal("Could not navigate: connection refused", step.ToolErrorMessage);
        Assert.Null(step.ToolOutputJson);
        Assert.Equal(ReproductionVerdict.Inconclusive, outcome.Verdict);
    }

    [Fact]
    public async Task RunningOutOfTurnsStopsTheLoopInsteadOfLoopingForever()
    {
        // A model that keeps investigating and never concludes.
        var model = new ScriptedMessagesClient(ModelResponses.ToolCall("navigate", new { url = "http://localhost:4000" }));
        var sink = new CollectingStepSink();

        var outcome = await BuildAgent(model, new FakeBrowserTools(), maxIterations: 3).RunAsync(Bug, sink);

        Assert.Equal(StopReason.StepBudgetExhausted, outcome.StopReason);
        Assert.Equal(ReproductionVerdict.Inconclusive, outcome.Verdict);
        Assert.Equal(3, model.CallCount);
    }

    [Fact]
    public async Task AnswersGivenAsProseAreNotTreatedAsAVerdict()
    {
        var model = new ScriptedMessagesClient(ModelResponses.Text("I think this is probably a session bug."));
        var sink = new CollectingStepSink();

        var outcome = await BuildAgent(model, new FakeBrowserTools()).RunAsync(Bug, sink);

        Assert.Equal(StopReason.ModelStoppedWithoutConcluding, outcome.StopReason);
        Assert.Equal(ReproductionVerdict.Inconclusive, outcome.Verdict);
        Assert.Empty(outcome.Evidence);
    }

    [Fact]
    public async Task EvidenceCitingAStepThatNeverRanIsSeparatedOutRatherThanTrusted()
    {
        var model = new ScriptedMessagesClient(
            ModelResponses.ToolCall("navigate", new { url = "http://localhost:4000" }),
            ModelResponses.Conclude(
                "REPRODUCED",
                "It failed.",
                (1, "navigated to the app"),
                (99, "DELETE /api/cart/items/42 returned 500")));
        var sink = new CollectingStepSink();

        var outcome = await BuildAgent(model, new FakeBrowserTools()).RunAsync(Bug, sink);

        Assert.Equal("navigated to the app", outcome.Evidence.Single().Observation);
        var unverifiable = outcome.UnverifiableEvidence.Single();
        Assert.Equal(99, unverifiable.StepNumber);
        Assert.Contains("500", unverifiable.Observation);
    }

    [Fact]
    public async Task VerdictStringsFromTheModelMapToTheVerdictEnum()
    {
        foreach (var (raw, expected) in new[]
                 {
                     ("REPRODUCED", ReproductionVerdict.Reproduced),
                     ("NOT_REPRODUCED", ReproductionVerdict.NotReproduced),
                     ("INCONCLUSIVE", ReproductionVerdict.Inconclusive),
                     ("something unexpected", ReproductionVerdict.Inconclusive),
                 })
        {
            var model = new ScriptedMessagesClient(ModelResponses.Conclude(raw, "reasoning"));
            var outcome = await BuildAgent(model, new FakeBrowserTools()).RunAsync(Bug, new CollectingStepSink());
            Assert.Equal(expected, outcome.Verdict);
        }
    }
}
