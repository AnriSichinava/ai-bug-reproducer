using System.Text.Json.Serialization;

namespace BugReproducer.Api.Models;

/// Lifecycle of the run itself — did the agent finish its work?
[JsonConverter(typeof(JsonStringEnumConverter<AgentRunStatus>))]
public enum AgentRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// What the agent concluded about the bug. Only meaningful once the run completed.
[JsonConverter(typeof(JsonStringEnumConverter<ReproductionVerdict>))]
public enum ReproductionVerdict
{
    Reproduced,
    NotReproduced,
    Inconclusive,
}

public class AgentRun
{
    public int Id { get; set; }
    public int BugReportId { get; set; }
    public BugReport? BugReport { get; set; }

    public AgentRunStatus Status { get; set; } = AgentRunStatus.Pending;

    /// Null until the run completes. A completed run always has one; a failed run never does.
    public ReproductionVerdict? Verdict { get; set; }

    /// The agent's stated justification for the verdict. Inference, not observed fact.
    public string? VerdictReason { get; set; }

    /// Set when the run fails for operational reasons (crash, timeout, tool unavailable),
    /// which is distinct from concluding the bug could not be reproduced.
    public string? FailureReason { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public List<AgentStep> Steps { get; set; } = [];
}
