namespace BugReproducer.Api.Models;

/// One iteration of the agent loop: what it intended, which tool it called, what came back.
/// Reasoning is the model's own words; ToolOutputJson is observed fact from the environment.
/// Keeping them in separate columns is what lets the UI show evidence without dressing up
/// inference as fact.
public class AgentStep
{
    public int Id { get; set; }
    public int AgentRunId { get; set; }
    public AgentRun? AgentRun { get; set; }

    public int StepNumber { get; set; }

    /// The model's stated intent for this step. Null for steps the orchestrator took itself.
    public string? Reasoning { get; set; }

    /// Null when the step was pure reasoning with no tool call.
    public string? ToolName { get; set; }
    public string? ToolInputJson { get; set; }
    public string? ToolOutputJson { get; set; }

    /// Set when the tool call itself errored, as opposed to returning an unexpected result.
    public string? ToolErrorMessage { get; set; }

    /// Paths to artifacts produced by this step (screenshots, console logs, HAR files).
    public string? ArtifactPathsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
