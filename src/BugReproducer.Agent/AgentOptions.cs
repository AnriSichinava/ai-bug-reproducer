namespace BugReproducer.Agent;

public class AgentOptions
{
    public string Model { get; set; } = "claude-opus-5";

    public int MaxTokens { get; set; } = 16000;

    /// Hard cap on model turns. Without this an agent that keeps finding "one more thing
    /// to check" runs until the API bill says stop.
    public int MaxIterations { get; set; } = 15;

    /// Base URL of the application under test, handed to the model so it never has to
    /// guess where the app lives.
    public required string TargetBaseUrl { get; set; }
}

public record BugReportContext(string Title, string Description);
