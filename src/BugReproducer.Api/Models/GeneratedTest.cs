namespace BugReproducer.Api.Models;

public class GeneratedTest
{
    public int Id { get; set; }
    public int AgentRunId { get; set; }
    public AgentRun? AgentRun { get; set; }

    public required string FileName { get; set; }
    public required string SourceCode { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<TestRun> TestRuns { get; set; } = [];
}
