using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BugReproducer.Agent;

/// The only way the agent touches a browser. Every call goes through the MCP
/// server's fixed tool set (navigate/click/get_console_errors for now) -- there is
/// no path from here to raw Playwright, a shell, or the host machine.
public class BrowserToolsClient : IAsyncDisposable
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

    public ValueTask<IList<McpClientTool>> ListToolsAsync() => _client.ListToolsAsync();

    public async Task<NavigateResult> NavigateAsync(string url)
    {
        var result = await CallAsync("navigate", new Dictionary<string, object?> { ["url"] = url });
        return JsonSerializer.Deserialize<NavigateResult>(result, JsonOptions)!;
    }

    public async Task<bool> ClickAsync(string selector)
    {
        var result = await _client.CallToolAsync(
            "click",
            new Dictionary<string, object?> { ["selector"] = selector });
        return result.IsError != true;
    }

    public async Task<IReadOnlyList<string>> GetConsoleErrorsAsync()
    {
        var result = await CallAsync("get_console_errors", new Dictionary<string, object?>());
        return JsonSerializer.Deserialize<List<string>>(result, JsonOptions)!;
    }

    private async Task<string> CallAsync(string toolName, Dictionary<string, object?> arguments)
    {
        var result = await _client.CallToolAsync(toolName, arguments);
        if (result.IsError == true)
        {
            var message = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "unknown error";
            throw new InvalidOperationException($"Tool '{toolName}' failed: {message}");
        }

        return result.Content.OfType<TextContentBlock>().First().Text;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

public record NavigateResult(int? Status, string Title, string Url);
