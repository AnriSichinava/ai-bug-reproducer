using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BugReproducer.Agent;

/// The only way the agent touches a browser. Every call goes through the MCP
/// server's fixed tool set -- there is no path from here to raw Playwright, a
/// shell, or the host machine.
public class BrowserToolsClient : IBrowserTools, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly McpClient _client;

    private BrowserToolsClient(McpClient client)
    {
        _client = client;
    }

    public static async Task<BrowserToolsClient> ConnectAsync(string mcpToolsDistPath)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "bug-reproducer-browser-tools",
            Command = "node",
            Arguments = [mcpToolsDistPath],
        });

        var client = await McpClient.CreateAsync(transport);
        return new BrowserToolsClient(client);
    }

    public async Task<IReadOnlyList<BrowserToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
        return [.. tools.Select(tool => new BrowserToolDefinition(tool.Name, tool.Description, tool.JsonSchema))];
    }

    /// Dispatches a tool by name with whatever arguments the model chose. The agent loop
    /// uses this rather than the typed helpers below, since it picks tools at runtime.
    public async Task<ToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.CallToolAsync(
                toolName,
                arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                cancellationToken: cancellationToken);

            var text = string.Join(
                "\n",
                result.Content.OfType<TextContentBlock>().Select(block => block.Text));

            return new ToolCallResult(result.IsError != true, text);
        }
        catch (Exception ex)
        {
            // A transport-level failure is still an observation the agent should see and
            // reason about, rather than an exception that kills the whole run.
            return new ToolCallResult(false, $"Tool '{toolName}' threw: {ex.Message}");
        }
    }

    public async Task<NavigateResult> NavigateAsync(string url)
    {
        var result = await RequireSuccessAsync("navigate", new Dictionary<string, object?> { ["url"] = url });
        return JsonSerializer.Deserialize<NavigateResult>(result, JsonOptions)!;
    }

    public async Task<bool> ClickAsync(string selector)
    {
        var result = await CallToolAsync("click", new Dictionary<string, object?> { ["selector"] = selector });
        return result.Success;
    }

    public async Task<IReadOnlyList<string>> GetConsoleErrorsAsync()
    {
        var result = await RequireSuccessAsync("get_console_errors", new Dictionary<string, object?>());
        return JsonSerializer.Deserialize<List<string>>(result, JsonOptions)!;
    }

    private async Task<string> RequireSuccessAsync(string toolName, Dictionary<string, object?> arguments)
    {
        var result = await CallToolAsync(toolName, arguments);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Tool '{toolName}' failed: {result.Text}");
        }

        return result.Text;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

public record NavigateResult(int? Status, string Title, string Url);
