using System.Text.Json;

namespace BugReproducer.Agent;

/// What the agent needs from its environment: a list of tools it may call, and a way to
/// call them. That MCP and Playwright sit behind this is deliberately invisible here --
/// the loop reasons about named tools, not about transports.
public interface IBrowserTools
{
    Task<IReadOnlyList<BrowserToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default);

    Task<ToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);
}

/// A tool the agent may call, described by the JSON Schema its provider published.
public record BrowserToolDefinition(string Name, string? Description, JsonElement JsonSchema);

public record ToolCallResult(bool Success, string Text);
