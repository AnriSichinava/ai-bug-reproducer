using System.Text.Json;
using Anthropic.Models.Messages;
using BugReproducer.Agent;

namespace BugReproducer.Tests.Agent;

/// Replays a fixed script of model responses, so the loop's control flow can be tested
/// exactly and without an API key.
public class ScriptedMessagesClient(params Message[] responses) : IMessagesClient
{
    private readonly Queue<Message> _responses = new(responses);

    public int CallCount { get; private set; }

    public Task<Message> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken = default)
    {
        CallCount++;
        // Running dry means the loop asked for more turns than the test scripted, which
        // for the budget test is the point: keep feeding it the last response.
        var response = _responses.Count > 0 ? _responses.Dequeue() : responses[^1];
        return Task.FromResult(response);
    }
}

/// Never completes until cancelled. This is the default IMessagesClient for the whole
/// test host (see TestApiFactory): most tests only care that POST /reproduce enqueues
/// work, not that it finishes, and a background run that actually completed near-
/// instantly would race those tests' assertions about Pending/Running state. Tests that
/// need a real conclusion set TestAgentBackends.MessagesClient to a ScriptedMessagesClient
/// explicitly.
public class NeverRespondingMessagesClient : IMessagesClient
{
    public Task<Message> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }
}

/// Lets individual tests swap in a fast, scripted IMessagesClient/IBrowserTools for the
/// background reproduction flow without touching DI registration per test.
public class TestAgentBackends
{
    public IMessagesClient MessagesClient { get; set; } = new NeverRespondingMessagesClient();
    public IBrowserTools BrowserTools { get; set; } = new FakeBrowserTools();
}

public class FakeBrowserTools : IBrowserTools
{
    private readonly Dictionary<string, ToolCallResult> _results = new();

    public List<string> CalledTools { get; } = [];

    public FakeBrowserTools Returns(string toolName, bool success, string text)
    {
        _results[toolName] = new ToolCallResult(success, text);
        return this;
    }

    public Task<IReadOnlyList<BrowserToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { url = new { type = "string" } },
            required = new[] { "url" },
        });

        IReadOnlyList<BrowserToolDefinition> tools =
        [
            new("navigate", "Navigate to a URL", schema),
            new("get_network_log", "Read the network log", schema),
        ];
        return Task.FromResult(tools);
    }

    public Task<ToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        CalledTools.Add(toolName);
        return Task.FromResult(_results.TryGetValue(toolName, out var result)
            ? result
            : new ToolCallResult(true, "{}"));
    }
}

/// Reports every test as passing without touching Node, Playwright, or the filesystem.
/// Used wherever a test only needs the generate-and-persist flow to work, not a real
/// test execution (that's what the gated McpWiringTests-style tests are for).
public class FakePlaywrightTestRunner : IPlaywrightTestRunner
{
    public Task<TestExecutionResult> RunAsync(GeneratedTestSource test, CancellationToken cancellationToken = default)
        => Task.FromResult(new TestExecutionResult(true, "fake runner: not actually executed"));
}

public class CollectingStepSink : IAgentStepSink
{
    public List<AgentStepRecord> Steps { get; } = [];

    public Task RecordAsync(AgentStepRecord step, CancellationToken cancellationToken = default)
    {
        Steps.Add(step);
        return Task.CompletedTask;
    }
}

/// Builders for the response shapes the loop has to handle.
///
/// These are built from wire JSON rather than object initializers. Response types carry
/// a lot of required members precisely because they're meant to be deserialized from an
/// API payload, so going through the same path keeps the fixtures small and makes them
/// faithful to what the loop really receives.
public static class ModelResponses
{
    public static Message Text(string text)
        => Build([new { type = "text", text, citations = (object?)null }], "end_turn");

    public static Message ToolCall(string toolName, object input, string? thinking = null)
    {
        List<object> content = [];
        if (thinking is not null)
        {
            content.Add(new { type = "thinking", thinking, signature = "test-signature" });
        }

        content.Add(new
        {
            type = "tool_use",
            id = $"toolu_{toolName}",
            name = toolName,
            input,
            caller = (object?)null,
        });

        return Build(content, "tool_use");
    }

    public static Message Conclude(string verdict, string reasoning, params (int Step, string Observation)[] evidence)
        => ToolCall("conclude", new
        {
            verdict,
            reasoning,
            evidence = evidence.Select(e => new { step = e.Step, observation = e.Observation }).ToArray(),
        });

    private static Message Build(List<object> content, string stopReason)
    {
        var payload = new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = "claude-opus-5",
            content,
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            container = (object?)null,
            diagnostics = (object?)null,
            stop_details = (object?)null,
            usage = new
            {
                input_tokens = 1,
                output_tokens = 1,
                cache_creation = (object?)null,
                cache_creation_input_tokens = (int?)null,
                cache_read_input_tokens = (int?)null,
                inference_geo = (string?)null,
                output_tokens_details = (object?)null,
                server_tool_use = (object?)null,
                service_tier = (string?)null,
            },
        };

        return JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(payload))!;
    }
}
