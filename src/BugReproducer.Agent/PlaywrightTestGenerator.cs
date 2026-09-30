using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BugReproducer.Agent;

public record GeneratedTestSource(string FileName, string SourceCode);

/// Turns a recorded agent trace into a Playwright regression test -- deterministically,
/// by walking the recorded tool calls, not by asking an LLM to author one. An LLM
/// writing the test from scratch could invent selectors or assertions that don't match
/// what was actually observed; templating from the trace means the test can't drift
/// from what really happened.
///
/// Important semantic choice: the generated assertion encodes the *expected correct*
/// behavior (no server error, no new console errors), not the observed bug. Asserting
/// the bug itself (e.g. "expect status to be 500") would make the test pass forever,
/// including after a real fix -- the opposite of a regression test. Asserting the
/// expected behavior means the test fails now (bug present) and starts passing the
/// moment someone actually fixes it, which is what "regression protection" means.
public static class PlaywrightTestGenerator
{
    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);

    public static GeneratedTestSource Generate(BugReportContext bug, IReadOnlyList<AgentStepRecord> steps)
    {
        // Recorded fill values that look like an email are treated as an ephemeral test
        // identity rather than replayed literally. This is not a hypothetical concern:
        // replaying a literal captured email means "register" tries to create an
        // account that already exists (it was created the moment this trace was
        // recorded), which 409s and derails everything after it. Regenerating it per
        // run -- while keeping every occurrence of the *same* recorded value in sync,
        // so register and a later login still agree with each other -- is what makes
        // the generated test runnable more than once.
        var emailVariableNames = steps
            .Where(s => s.ToolName == "fill")
            .Select(s => TryGetString(s.ToolInputJson, "value", out var v) ? v : null)
            .Where(v => v is not null && EmailPattern.IsMatch(v))
            .Distinct()
            .Select((email, index) => (email: email!, variable: $"email{index + 1}"))
            .ToDictionary(x => x.email, x => x.variable);

        List<string> actionLines = [];
        string? lastClickSelector = null;
        FailingRequest? failingRequestAfterLastClick = null;

        foreach (var step in steps.OrderBy(s => s.StepNumber))
        {
            switch (step.ToolName)
            {
                case "navigate":
                    if (TryGetString(step.ToolInputJson, "url", out var url))
                    {
                        actionLines.Add($"  await page.goto({TsString(url)});");
                        failingRequestAfterLastClick = null; // a new page resets what we're tracking
                    }
                    break;

                case "fill":
                    if (TryGetString(step.ToolInputJson, "selector", out var fillSelector)
                        && TryGetString(step.ToolInputJson, "value", out var value))
                    {
                        var valueExpression = emailVariableNames.TryGetValue(value, out var variable)
                            ? variable
                            : TsString(value);
                        actionLines.Add($"  await page.fill({TsString(fillSelector)}, {valueExpression});");
                    }
                    break;

                case "click":
                    if (TryGetString(step.ToolInputJson, "selector", out var clickSelector))
                    {
                        // Recorded as plain lines for now; if a failing request turns up
                        // after this click before the trace ends, these are replaced
                        // below with a version that captures and asserts on that response.
                        //
                        // The wait mirrors the MCP click tool's own post-click pause during
                        // recording. Without it, a click that triggers an async
                        // fetch-then-redirect (a common pattern) races the next action
                        // against the app's own in-flight navigation and Playwright aborts
                        // one of them -- a real failure mode hit while building this
                        // generator, not a hypothetical one.
                        actionLines.Add($"  await page.click({TsString(clickSelector)});");
                        actionLines.Add("  await page.waitForTimeout(500);");
                        lastClickSelector = clickSelector;
                        failingRequestAfterLastClick = null;
                    }
                    break;

                case "get_network_log":
                    // Heuristic: attribute the most recent failure seen in any network-log
                    // read to the most recent click. This fits a single-action reproduction
                    // (our target scenario) and is a known simplification for traces with
                    // several independent interactions late in the run.
                    if (lastClickSelector is not null && step.ToolOutputJson is not null)
                    {
                        var failing = FindLastFailingEntry(step.ToolOutputJson);
                        if (failing is not null)
                        {
                            failingRequestAfterLastClick = failing;
                        }
                    }
                    break;
            }
        }

        if (failingRequestAfterLastClick is not null && lastClickSelector is not null)
        {
            // Replace the plain click + wait with a version that captures the response
            // it triggers, so the assertion is tied to a real, previously-observed
            // request. waitForResponse is itself a precise wait for the async effect, so
            // the generic post-click pause isn't needed here -- drop that line too.
            actionLines.RemoveAt(actionLines.Count - 1);
            actionLines[^1] =
                $"""
                  const [response] = await Promise.all([
                    page.waitForResponse((r) => r.url().includes({TsString(failingRequestAfterLastClick.Pathname)})),
                    page.click({TsString(lastClickSelector)}),
                  ]);
                """;
        }

        var body = new StringBuilder();
        body.AppendLine("import { test, expect } from '@playwright/test';");
        body.AppendLine();
        body.AppendLine("// Auto-generated from an AgentRun trace. Do not edit by hand --");
        body.AppendLine("// re-running the reproduction for this bug will overwrite this file.");
        body.AppendLine($"test({TsString(bug.Title)}, async ({{ page }}) => {{");
        body.AppendLine("  const consoleErrors: string[] = [];");
        body.AppendLine("  page.on('console', (msg) => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });");
        body.AppendLine("  page.on('pageerror', (err) => consoleErrors.push(err.message));");
        body.AppendLine();

        foreach (var variable in emailVariableNames.Values)
        {
            body.AppendLine($"  const {variable} = `agent-test-${{Date.now()}}-{variable}@example.com`;");
        }

        if (emailVariableNames.Count > 0)
        {
            body.AppendLine();
        }

        foreach (var line in actionLines)
        {
            body.AppendLine(line);
        }

        body.AppendLine();
        if (failingRequestAfterLastClick is not null)
        {
            body.AppendLine(
                "  expect(response.status(), 'action should not result in a server error').toBeLessThan(400);");
        }
        else
        {
            body.AppendLine("  // No network failure was observed in the recorded trace to assert against;");
            body.AppendLine("  // this test only replays the recorded actions and checks the console.");
        }

        body.AppendLine(
            "  expect(consoleErrors, 'action should not produce new console errors').toEqual([]);");
        body.AppendLine("});");

        return new GeneratedTestSource(Slugify(bug.Title) + ".spec.ts", body.ToString());
    }

    private static bool TryGetString(string? json, string property, out string value)
    {
        value = "";
        if (json is null) return false;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }

        return false;
    }

    private static FailingRequest? FindLastFailingEntry(string networkLogJson)
    {
        using var document = JsonDocument.Parse(networkLogJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        FailingRequest? last = null;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("status", out var statusElement) || statusElement.GetInt32() < 400) continue;
            if (!entry.TryGetProperty("url", out var urlElement)) continue;

            var pathname = Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri)
                ? uri.PathAndQuery
                : urlElement.GetString() ?? "";
            last = new FailingRequest(pathname);
        }

        return last;
    }

    /// Re-uses JSON string escaping to produce a valid TS/JS string literal -- JSON
    /// string syntax is a subset of what JS accepts, so this is exact, not approximate.
    private static string TsString(string value) => JsonSerializer.Serialize(value);

    private static string Slugify(string title)
    {
        var slug = new string(title.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private record FailingRequest(string Pathname);
}
