using BugReproducer.Api.Models;

namespace BugReproducer.Api.Dtos;

public record AgentRunResponse(
    int Id,
    int BugReportId,
    AgentRunStatus Status,
    ReproductionVerdict? Verdict,
    string? VerdictReason,
    string? FailureReason,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc);

public record AgentStepResponse(
    int Id,
    int StepNumber,
    string? Reasoning,
    string? ToolName,
    string? ToolInputJson,
    string? ToolOutputJson,
    string? ToolErrorMessage,
    DateTime CreatedAtUtc);

public record AgentRunDetailResponse(
    int Id,
    int BugReportId,
    AgentRunStatus Status,
    ReproductionVerdict? Verdict,
    string? VerdictReason,
    string? FailureReason,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<AgentStepResponse> Steps);
