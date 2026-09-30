using BugReproducer.Agent;

namespace BugReproducer.Tests;

/// Milestone 2 checkpoint: proves the C# agent can drive a real browser through the
/// MCP server before any LLM is involved. Excluded from the default `dotnet test` run
/// (see README) because it needs target-app (docker compose up) and a built mcp-tools
/// server (npm run build) running locally -- unlike the rest of the suite, which needs
/// no external process.
[Trait("Category", "RequiresBrowserAndTargetApp")]
public class McpWiringTests
{
    private static readonly string McpToolsDistPath = FindMcpToolsDistPath();

    [Fact]
    public async Task HardcodedSequence_NavigatesAddsToCartAndReadsConsole()
    {
        await using var browserTools = await BrowserToolsClient.ConnectAsync(McpToolsDistPath);

        var tools = await browserTools.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "navigate");
        Assert.Contains(tools, t => t.Name == "click");
        Assert.Contains(tools, t => t.Name == "get_console_errors");

        var navigation = await browserTools.NavigateAsync("http://localhost:4000/products.html");
        Assert.Equal(200, navigation.Status);
        Assert.Contains("Products", navigation.Title);

        // Deliberately click a plain nav link, not "Add to cart": that button calls a
        // protected API and this milestone's tool set has no `fill` yet to log in
        // first, so clicking it would 401 and pollute the console-error assertion
        // below with an unrelated, expected failure. Logging in is Milestone 3.
        var clicked = await browserTools.ClickAsync("a[href='/products.html']");
        Assert.True(clicked);

        var errors = await browserTools.GetConsoleErrorsAsync();
        Assert.Empty(errors);
    }

    private static string FindMcpToolsDistPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "mcp-tools")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate the mcp-tools directory from the test output path.");
        }

        return Path.Combine(dir.FullName, "mcp-tools", "dist", "index.js");
    }
}
