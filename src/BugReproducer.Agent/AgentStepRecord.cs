namespace BugReproducer.Agent;

/// One iteration of the loop, emitted as it happens. Reasoning is what the model said
/// it was doing; ToolOutputJson is what the environment actually returned.
public record AgentStepRecord(
    int StepNumber,
    string? Reasoning,
    string? ToolName,
    string? ToolInputJson,
    string? ToolOutputJson,
    string? ToolErrorMessage);

/// Where steps go. The agent library deliberately knows nothing about EF Core or the
/// database -- the API layer supplies a sink that persists AgentStep rows, and tests
/// supply one that collects them in memory.
public interface IAgentStepSink
{
    Task RecordAsync(AgentStepRecord step, CancellationToken cancellationToken = default);
}
