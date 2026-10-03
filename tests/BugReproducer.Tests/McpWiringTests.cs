using System.Text.Json;
using BugReproducer.Agent;

namespace BugReproducer.Tests;

/// Proves the browser tool set works against a real app and is sufficient to reach the
/// bug -- without involving an LLM. Excluded from the default `dotnet test` run (see
/// README) because it needs target-app (docker compose up) and a built mcp-tools server
/// (npm run build); the rest of the suite needs no external process.
[Trait("Category", "RequiresBrowserAndTargetApp")]
public class McpWiringTests
{
    private const string BaseUrl = "http://localhost:4000";
    private static readonly string McpToolsDistPath = FindMcpToolsDistPath();

    [Fact]
    public async Task AllExpectedToolsArePublished()
    {
        await using var browserTools = await BrowserToolsClient.ConnectAsync(McpToolsDistPath);

        var names = (await browserTools.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Equal(
            ["click", "execute_api_request", "fill", "get_console_errors", "get_network_log",
             "get_page_snapshot", "navigate", "read_source_file", "search_code", "take_screenshot"],
            names.Order());
    }

    [Fact]
    public async Task ReadSourceFile_ReturnsTheTargetAppsActualSourceCode()
    {
        await using var tools = await BrowserToolsClient.ConnectAsync(McpToolsDistPath);

        var result = await Call(tools, "read_source_file", new { path = "server.js" });

        var content = JsonDocument.Parse(result).RootElement.GetProperty("content").GetString()!;
        // This is the real bug's own comment, not a string we're asserting into existence --
        // confirms the tool is reading the actual file, not fabricating a plausible answer.
        Assert.Contains("THE BUG", content);
        Assert.Contains("req.session.cart.items", content);
    }

    [Fact]
    public async Task SearchCode_FindsTheHandlerForARoute()
    {
        await using var tools = await BrowserToolsClient.ConnectAsync(McpToolsDistPath);

        var result = await Call(tools, "search_code", new { query = "/api/cart/items/:id" });

        var matches = JsonDocument.Parse(result).RootElement.EnumerateArray().ToList();
        Assert.Contains(matches, m => m.GetProperty("path").GetString() == "server.js");
    }

    [Fact]
    public async Task ReadSourceFile_RejectsPathsThatEscapeTheSourceRoot()
    {
        await using var tools = await BrowserToolsClient.ConnectAsync(McpToolsDistPath);

        var result = await tools.CallToolAsync("read_source_file", new Dictionary<string, object?>
        {
            ["path"] = "../../../../etc/passwd",
        });

        Assert.False(result.Success);
        Assert.Contains("escapes the source root", result.Text);
    }

    [Fact]
    public async Task HardcodedSequence_NavigatesAndReadsConsole()
    {
        await using var browserTools = await BrowserToolsClient.ConnectAsync(McpToolsDistPath);

        var navigation = await browserTools.NavigateAsync($"{BaseUrl}/products.html");
        Assert.Equal(200, navigation.Status);
        Assert.Contains("Products", navigation.Title);

        // Deliberately click a plain nav link, not "Add to cart": that button calls a
        // protected API, and clicking it while logged out would 401 and pollute the
        // console-error assertion below with an unrelated, expected failure.
        Assert.True(await browserTools.ClickAsync("a[href='/products.html']"));
        Assert.Empty(await browserTools.GetConsoleErrorsAsync());
    }

    /// The question this answers is not "does the agent work" but "could it possibly
    /// work": can the reported bug actually be reached using only the published tools?
    /// Worth knowing before spending API tokens letting a model try.
    [Fact]
    public async Task TheReportedBugIsReachableUsingOnlyThePublishedTools()
    {
        await using var tools = await BrowserToolsClient.ConnectAsync(McpToolsDistPath);
        var email = $"agent-check-{Guid.NewGuid():N}@example.com";

        await Call(tools, "navigate", new { url = $"{BaseUrl}/register.html" });
        await Call(tools, "fill", new { selector = "#email", value = email });
        await Call(tools, "fill", new { selector = "#password", value = "Password123!" });
        await Call(tools, "click", new { selector = "#register-btn" });

        await Call(tools, "navigate", new { url = $"{BaseUrl}/products.html" });
        await Call(tools, "click", new { selector = "#add-to-cart-1" });

        // The cart page must show the item, otherwise the later failure would just be
        // "there was nothing to remove" rather than the reported bug.
        var snapshotBeforeLogout = await Call(tools, "navigate", new { url = $"{BaseUrl}/cart.html" });
        Assert.Equal(200, JsonDocument.Parse(snapshotBeforeLogout).RootElement.GetProperty("status").GetInt32());

        await Call(tools, "click", new { selector = "#logout-btn" });

        await Call(tools, "navigate", new { url = $"{BaseUrl}/login.html" });
        await Call(tools, "fill", new { selector = "#email", value = email });
        await Call(tools, "fill", new { selector = "#password", value = "Password123!" });
        await Call(tools, "click", new { selector = "#login-btn" });

        await Call(tools, "navigate", new { url = $"{BaseUrl}/cart.html" });
        var snapshot = await Call(tools, "get_page_snapshot", new { });
        // The item survived the logout/login, so it is genuinely there to be removed.
        Assert.Contains("Wireless Mouse", snapshot);

        var removeSelector = JsonDocument.Parse(snapshot).RootElement
            .GetProperty("interactive")
            .EnumerateArray()
            .Select(e => e.GetProperty("selector").GetString()!)
            .First(s => s.StartsWith("#remove-item-"));

        await Call(tools, "click", new { selector = removeSelector });

        var networkLog = await Call(tools, "get_network_log", new { });
        var failure = JsonDocument.Parse(networkLog).RootElement.EnumerateArray()
            .Where(e => e.GetProperty("status").GetInt32() >= 400)
            .Select(e => new
            {
                Method = e.GetProperty("method").GetString(),
                Url = e.GetProperty("url").GetString(),
                Status = e.GetProperty("status").GetInt32(),
            })
            .LastOrDefault();

        Assert.NotNull(failure);
        Assert.Equal("DELETE", failure.Method);
        Assert.Equal(500, failure.Status);
        Assert.Contains("/api/cart/items/", failure.Url);
    }

    private static async Task<string> Call(BrowserToolsClient tools, string name, object arguments)
    {
        var json = JsonSerializer.SerializeToElement(arguments);
        var dictionary = json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);

        var result = await tools.CallToolAsync(name, dictionary);
        Assert.True(result.Success, $"Tool '{name}' failed: {result.Text}");
        return result.Text;
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
