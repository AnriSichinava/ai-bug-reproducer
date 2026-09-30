namespace BugReproducer.Api.Models;

public class TestRun
{
    public int Id { get; set; }
    public int GeneratedTestId { get; set; }
    public GeneratedTest? GeneratedTest { get; set; }

    public bool Passed { get; set; }
    public required string Output { get; set; }
    public DateTime RanAtUtc { get; set; } = DateTime.UtcNow;
}
