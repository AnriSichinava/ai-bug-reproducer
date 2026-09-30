using AgentLib = BugReproducer.Agent;
using BugReproducer.Api.Data;
using BugReproducer.Api.Models;

namespace BugReproducer.Api.Services;

/// Persists each step as it happens, so a run in progress is visible mid-flight rather
/// than only after it finishes -- this is what "watch the agent work" would show.
public class EfAgentStepSink(AppDbContext db, int agentRunId) : AgentLib.IAgentStepSink
{
    public async Task RecordAsync(AgentLib.AgentStepRecord step, CancellationToken cancellationToken = default)
    {
        db.AgentSteps.Add(new AgentStep
        {
            AgentRunId = agentRunId,
            StepNumber = step.StepNumber,
            Reasoning = step.Reasoning,
            ToolName = step.ToolName,
            ToolInputJson = step.ToolInputJson,
            ToolOutputJson = step.ToolOutputJson,
            ToolErrorMessage = step.ToolErrorMessage,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
