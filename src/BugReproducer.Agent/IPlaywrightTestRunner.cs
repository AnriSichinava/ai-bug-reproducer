using System.Diagnostics;

namespace BugReproducer.Agent;

public record TestExecutionResult(bool Passed, string Output);

/// Executes a generated test. Behind an interface for the same reason as IMessagesClient
/// and IBrowserTools: the real implementation shells out to `npx playwright test`, which
/// the default (fast, no-external-deps) test suite must not depend on.
public interface IPlaywrightTestRunner
{
    Task<TestExecutionResult> RunAsync(GeneratedTestSource test, CancellationToken cancellationToken = default);
}

/// Writes the generated file into e2e/generated/ and runs it with the real Playwright
/// CLI. This is the one place in the whole system that shells out to a process -- and
/// it runs a fixed, non-negotiable command against a file this code just wrote, not
/// anything the LLM chose, so it doesn't reopen the "no shell access for the agent"
/// boundary the rest of the project is built around.
public class PlaywrightTestRunner(string e2eDirectory) : IPlaywrightTestRunner
{
    public async Task<TestExecutionResult> RunAsync(
        GeneratedTestSource test, CancellationToken cancellationToken = default)
    {
        var generatedDir = Path.Combine(e2eDirectory, "generated");
        Directory.CreateDirectory(generatedDir);
        var filePath = Path.Combine(generatedDir, test.FileName);
        await File.WriteAllTextAsync(filePath, test.SourceCode, cancellationToken);

        var relativePath = Path.Combine("generated", test.FileName);
        var startInfo = new ProcessStartInfo("npx", $"playwright test \"{relativePath}\"")
        {
            WorkingDirectory = e2eDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Playwright test process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new TestExecutionResult(process.ExitCode == 0, stdout + stderr);
    }
}
