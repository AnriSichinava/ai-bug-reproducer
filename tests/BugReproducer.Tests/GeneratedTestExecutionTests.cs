using System.Text.Json;
using BugReproducer.Agent;

namespace BugReproducer.Tests;

/// The one thing unit tests can't prove: that a file PlaywrightTestGenerator writes is
/// actually valid, runnable TypeScript, and that running it against the real (still
/// buggy) target-app genuinely fails -- not "fails because the file doesn't compile,"
/// but fails on the specific assertion we generated. Gated for the same reason as
/// McpWiringTests: needs Docker and a built mcp-tools server.
[Trait("Category", "RequiresBrowserAndTargetApp")]
public class GeneratedTestExecutionTests
{
    private const string BaseUrl = "http://localhost:4000";

    [Fact]
    public async Task ARealCapturedTrace_ProducesAGeneratedTestThatFailsBecauseTheBugIsStillPresent()
    {
        var mcpToolsDistPath = FindSiblingPath("mcp-tools", "dist", "index.js");
        var e2eDirectory = FindSiblingPath("e2e");

        List<AgentStepRecord> steps = [];
        var email = $"gen-exec-{Guid.NewGuid():N}@example.com";

        await using (var tools = await BrowserToolsClient.ConnectAsync(mcpToolsDistPath))
        {
            var stepNumber = 0;

            async Task<string> Record(string toolName, object arguments)
            {
                var json = JsonSerializer.SerializeToElement(arguments);
                var dict = json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
                var result = await tools.CallToolAsync(toolName, dict);
                Assert.True(result.Success, $"'{toolName}' failed: {result.Text}");
                steps.Add(new AgentStepRecord(++stepNumber, null, toolName, JsonSerializer.Serialize(arguments), result.Text, null));
                return result.Text;
            }

            await Record("navigate", new { url = $"{BaseUrl}/register.html" });
            await Record("fill", new { selector = "#email", value = email });
            await Record("fill", new { selector = "#password", value = "Password123!" });
            await Record("click", new { selector = "#register-btn" });

            await Record("navigate", new { url = $"{BaseUrl}/products.html" });
            await Record("click", new { selector = "#add-to-cart-1" });

            await Record("click", new { selector = "#logout-btn" });

            await Record("navigate", new { url = $"{BaseUrl}/login.html" });
            await Record("fill", new { selector = "#email", value = email });
            await Record("fill", new { selector = "#password", value = "Password123!" });
            await Record("click", new { selector = "#login-btn" });

            await Record("navigate", new { url = $"{BaseUrl}/cart.html" });
            // target-app's cart item ids are a global counter, not per-user, so the
            // remove button's id varies run to run -- discover it rather than assume "1".
            var snapshot = await Record("get_page_snapshot", new { });
            var removeSelector = JsonDocument.Parse(snapshot).RootElement
                .GetProperty("interactive")
                .EnumerateArray()
                .Select(e => e.GetProperty("selector").GetString()!)
                .First(s => s.StartsWith("#remove-item-"));

            await Record("click", new { selector = removeSelector });
            await Record("get_network_log", new { });
        }

        var bug = new BugReportContext(
            "Cart crash after re-login",
            "Removing an item after logging out and back in returns a 500.");
        var generated = PlaywrightTestGenerator.Generate(bug, steps);

        // Prove it before running it: the generator found real evidence in the real
        // trace, not just whatever the last test happened to leave lying around.
        Assert.Contains("waitForResponse", generated.SourceCode);
        Assert.Contains("toBeLessThan(400)", generated.SourceCode);

        var runner = new PlaywrightTestRunner(e2eDirectory);
        var execution = await runner.RunAsync(generated);

        Assert.False(execution.Passed, $"Expected the generated test to fail (the bug is still present). Output:\n{execution.Output}");
        // Playwright's default reporter is terse and doesn't echo the assertion's custom
        // message, so assert on what it reliably does print instead.
        Assert.Contains("1 failed", execution.Output);
        Assert.Contains(generated.FileName, execution.Output);
    }

    private static string FindSiblingPath(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, relativeParts[0])))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException($"Could not locate '{relativeParts[0]}' from the test output path.");
        }

        return Path.Combine([dir.FullName, .. relativeParts]);
    }
}
