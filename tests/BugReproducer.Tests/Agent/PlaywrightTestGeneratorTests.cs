using BugReproducer.Agent;

namespace BugReproducer.Tests.Agent;

public class PlaywrightTestGeneratorTests
{
    private static readonly BugReportContext Bug = new(
        "Cart crash after re-login",
        "Removing an item after logging out and back in returns a 500.");

    private static AgentStepRecord Navigate(int step, string url)
        => new(step, null, "navigate", Json(new { url }), """{"status":200}""", null);

    private static AgentStepRecord Fill(int step, string selector, string value)
        => new(step, null, "fill", Json(new { selector, value }), "{}", null);

    private static AgentStepRecord Click(int step, string selector)
        => new(step, null, "click", Json(new { selector }), Json(new { clicked = selector }), null);

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static AgentStepRecord NetworkLog(int step, params (string Method, string Url, int Status)[] entries)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            entries.Select(e => new { method = e.Method, url = e.Url, status = e.Status }));
        return new AgentStepRecord(step, null, "get_network_log", "{}", json, null);
    }

    [Fact]
    public void ReplaysNavigateFillAndClickInOrder()
    {
        var steps = new[]
        {
            Navigate(1, "http://localhost:4000/login.html"),
            Fill(2, "#email", "test@example.com"),
            Fill(3, "#password", "Password123!"),
            Click(4, "#login-btn"),
        };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        Assert.Contains("await page.goto(\"http://localhost:4000/login.html\");", result.SourceCode);
        // The email value is regenerated per run rather than replayed literally (see the
        // dedicated tests for that behavior), so assert the fill call references the
        // generated variable instead of the literal recorded address.
        Assert.Contains("await page.fill(\"#email\", email1);", result.SourceCode);
        Assert.Contains("await page.fill(\"#password\", \"Password123!\");", result.SourceCode);
        Assert.Contains("page.click(\"#login-btn\")", result.SourceCode);
    }

    [Fact]
    public void AssertsExpectedBehaviorNotTheObservedBug()
    {
        // This is the key semantic test: even though the recorded evidence is a 500,
        // the generated assertion must check for the ABSENCE of a server error, not
        // assert the 500 itself -- otherwise the test would pass while the bug exists.
        var steps = new[]
        {
            Navigate(1, "http://localhost:4000/cart.html"),
            Click(2, "#remove-item-1"),
            NetworkLog(3, ("DELETE", "http://localhost:4000/api/cart/items/1", 500)),
        };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        Assert.Contains("toBeLessThan(400)", result.SourceCode);
        Assert.DoesNotContain("toBe(500)", result.SourceCode);
        Assert.DoesNotContain("500", result.SourceCode.Replace("status(", "")); // no literal 500 in the assertion
    }

    [Fact]
    public void WrapsTheClickThatCausedTheFailingRequestInAResponseCapture()
    {
        var steps = new[]
        {
            Navigate(1, "http://localhost:4000/cart.html"),
            Click(2, "#remove-item-1"),
            NetworkLog(3, ("DELETE", "http://localhost:4000/api/cart/items/1", 500)),
        };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        Assert.Contains("waitForResponse", result.SourceCode);
        Assert.Contains("/api/cart/items/1", result.SourceCode);
        Assert.Contains("page.click(\"#remove-item-1\")", result.SourceCode);
    }

    [Fact]
    public void IgnoresSuccessfulRequestsWhenLookingForAFailure()
    {
        var steps = new[]
        {
            Navigate(1, "http://localhost:4000/cart.html"),
            Click(2, "#remove-item-1"),
            NetworkLog(3, ("DELETE", "http://localhost:4000/api/cart/items/1", 200)),
        };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        Assert.DoesNotContain("waitForResponse", result.SourceCode);
        Assert.Contains("No network failure was observed", result.SourceCode);
    }

    [Fact]
    public void AlwaysAssertsOnConsoleErrorsRegardlessOfNetworkEvidence()
    {
        var steps = new[] { Navigate(1, "http://localhost:4000/products.html") };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        Assert.Contains("consoleErrors", result.SourceCode);
        Assert.Contains("toEqual([])", result.SourceCode);
    }

    [Fact]
    public void FileNameIsSlugifiedFromTheBugTitle()
    {
        var result = PlaywrightTestGenerator.Generate(Bug, [Navigate(1, "http://localhost:4000")]);

        Assert.Equal("cart-crash-after-re-login.spec.ts", result.FileName);
    }

    [Fact]
    public void ValuesContainingQuotesAreEscapedSafely()
    {
        var steps = new[] { Fill(1, "#note", "he said \"hello\"") };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        // Must compile as valid TS: a literal unescaped quote here would break the
        // generated file rather than just look wrong.
        var expectedValueLiteral = System.Text.Json.JsonSerializer.Serialize("he said \"hello\"");
        Assert.Contains($"await page.fill(\"#note\", {expectedValueLiteral});", result.SourceCode);
    }

    [Fact]
    public void RecordedEmailValuesAreRegeneratedPerRunInsteadOfReplayedLiterally()
    {
        // Replaying the literal recorded email would make "register" try to create an
        // account that already exists (it was created the moment this trace was
        // captured), 409ing and derailing the rest of the test. A fresh value per run
        // is what makes the generated test repeatable.
        var steps = new[]
        {
            Navigate(1, "http://localhost:4000/register.html"),
            Fill(2, "#email", "agent-abc123@example.com"),
            Fill(3, "#password", "Password123!"),
            Click(4, "#register-btn"),
        };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        Assert.DoesNotContain("agent-abc123@example.com", result.SourceCode);
        Assert.Contains("const email1 =", result.SourceCode);
        Assert.Contains("await page.fill(\"#email\", email1);", result.SourceCode);
    }

    [Fact]
    public void TheSameRecordedEmailReusesOneVariableEverywhereItAppears()
    {
        // Register and a later login typically use the same recorded email -- the
        // generated test must keep using ONE regenerated value for both, or login
        // would target an account the regenerated register call never created.
        var steps = new[]
        {
            Fill(1, "#email", "agent-abc123@example.com"),
            Click(2, "#register-btn"),
            Navigate(3, "http://localhost:4000/login.html"),
            Fill(4, "#email", "agent-abc123@example.com"),
            Click(5, "#login-btn"),
        };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        var occurrences = result.SourceCode.Split("email1").Length - 1;
        Assert.True(occurrences >= 3, $"expected the shared variable to be declared once and used twice, got {occurrences} occurrences");
        Assert.DoesNotContain("email2", result.SourceCode);
    }

    [Fact]
    public void NonActionToolCallsAreNotReplayedAsPageActions()
    {
        var steps = new[]
        {
            Navigate(1, "http://localhost:4000"),
            new AgentStepRecord(2, null, "get_page_snapshot", "{}", "{\"title\":\"x\"}", null),
            new AgentStepRecord(3, null, "take_screenshot", """{"name":"x"}""", """{"path":"x.png"}""", null),
        };

        var result = PlaywrightTestGenerator.Generate(Bug, steps);

        Assert.DoesNotContain("get_page_snapshot", result.SourceCode);
        Assert.DoesNotContain("take_screenshot", result.SourceCode);
        Assert.DoesNotContain("screenshot", result.SourceCode.ToLowerInvariant().Replace("// auto-generated", ""));
    }
}
