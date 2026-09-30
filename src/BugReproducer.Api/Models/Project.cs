namespace BugReproducer.Api.Models;

public class Project
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int OwnerId { get; set; }
    public User? Owner { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<BugReport> BugReports { get; set; } = [];
}
