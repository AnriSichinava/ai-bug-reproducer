using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using BugReproducer.Agent;

namespace BugReproducer.Tests.Agent;

/// Verifies the Gemini translation in both directions without a real API key or network
/// call -- a fake HttpMessageHandler captures the outgoing request and returns a canned
/// response, so this tests GeminiMessagesClient's actual JSON-building logic rather than
/// just "does it compile."
public class GeminiMessagesClientTests
{
    [Fact]
    public async Task TranslatesToolDefinitionsAndHistoryIntoGeminisRequestShape()
    {
        var handler = new CapturingHandler("""
            {
              "candidates": [{ "content": { "parts": [{ "text": "ok" }] } }],
              "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 2 }
            }
            """);
        var client = new GeminiMessagesClient(new HttpClient(handler), "fake-key");

        var parameters = new MessageCreateParams
        {
            Model = "gemini-3.8-flash",
            MaxTokens = 1024,
            System = "You are a bug reproduction agent.",
            Tools = [new Tool
            {
                Name = "navigate",
                Description = "Navigate to a URL",
                InputSchema = new InputSchema
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["url"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                    },
                    Required = ["url"],
                },
            }],
            Messages = [new() { Role = Role.User, Content = "Reproduce this bug." }],
        };

        await client.CreateAsync(parameters);

        Assert.NotNull(handler.CapturedRequestBody);
        using var sentRequest = JsonDocument.Parse(handler.CapturedRequestBody);
        var root = sentRequest.RootElement;

        Assert.Equal(
            "You are a bug reproduction agent.",
            root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());

        var declaration = root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
        Assert.Equal("navigate", declaration.GetProperty("name").GetString());
        Assert.Equal("object", declaration.GetProperty("parameters").GetProperty("type").GetString());

        var content = root.GetProperty("contents")[0];
        Assert.Equal("user", content.GetProperty("role").GetString());
        Assert.Equal("Reproduce this bug.", content.GetProperty("parts")[0].GetProperty("text").GetString());

        Assert.Equal(
            $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent",
            handler.CapturedRequestUri);
    }

    [Fact]
    public async Task TranslatesAGeminiFunctionCallIntoAnAnthropicShapedToolUseBlock()
    {
        var handler = new CapturingHandler("""
            {
              "candidates": [{
                "content": {
                  "parts": [{ "functionCall": { "name": "click", "args": { "selector": "#remove-item-1" } } }]
                }
              }],
              "usageMetadata": { "promptTokenCount": 50, "candidatesTokenCount": 8 }
            }
            """);
        var client = new GeminiMessagesClient(new HttpClient(handler), "fake-key");

        var response = await client.CreateAsync(new MessageCreateParams
        {
            Model = "gemini-3.8-flash",
            MaxTokens = 1024,
            Messages = [new() { Role = Role.User, Content = "go" }],
        });

        Assert.Equal("tool_use", (string?)response.StopReason);
        var toolUse = Assert.Single(response.Content.Select(b => b.Value).OfType<ToolUseBlock>());
        Assert.Equal("click", toolUse.Name);
        Assert.Equal("#remove-item-1", toolUse.Input["selector"].GetString());
        Assert.Equal(50, response.Usage.InputTokens);
        Assert.Equal(8, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task ToolResultsAreTranslatedToFunctionResponsesUsingTheRecordedToolName()
    {
        // A tool_result block only carries the id of the call it answers -- this
        // confirms the adapter correctly recovers the tool's name from the preceding
        // tool_use block earlier in the same history, since Gemini's functionResponse
        // needs a name, not an id.
        var handler = new CapturingHandler("""
            { "candidates": [{ "content": { "parts": [{ "text": "ok" }] } }] }
            """);
        var client = new GeminiMessagesClient(new HttpClient(handler), "fake-key");

        await client.CreateAsync(new MessageCreateParams
        {
            Model = "gemini-3.8-flash",
            MaxTokens = 1024,
            Messages =
            [
                new() { Role = Role.User, Content = "go" },
                new()
                {
                    Role = Role.Assistant,
                    Content = new List<ContentBlockParam>
                    {
                        new ToolUseBlockParam { ID = "toolu_abc", Name = "navigate", Input = new Dictionary<string, JsonElement>() },
                    },
                },
                new()
                {
                    Role = Role.User,
                    Content = new List<ContentBlockParam>
                    {
                        new ToolResultBlockParam { ToolUseID = "toolu_abc", Content = "{\"status\":200}" },
                    },
                },
            ],
        });

        using var sentRequest = JsonDocument.Parse(handler.CapturedRequestBody!);
        var contentsArray = sentRequest.RootElement.GetProperty("contents");
        var lastContent = contentsArray[contentsArray.GetArrayLength() - 1];
        var functionResponse = lastContent.GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("navigate", functionResponse.GetProperty("name").GetString());
        Assert.Equal("{\"status\":200}", functionResponse.GetProperty("response").GetProperty("result").GetString());
    }

    [Fact]
    public async Task ThoughtSignaturesAreCapturedAndEchoedBackOnTheNextTurn()
    {
        // Gemini's thinking mode attaches an opaque thoughtSignature to a functionCall
        // part and rejects the next request if it isn't echoed back unmodified -- found
        // by actually running a real reproduction, not a hypothetical. The id this class
        // generates for the tool_use block is the only thing available to key the
        // signature by, since Anthropic's ToolUseBlock has no field for it.
        var handler = new CapturingHandler(
            """
            {
              "candidates": [{
                "content": {
                  "parts": [{
                    "functionCall": { "name": "navigate", "args": { "url": "http://localhost:4000" } },
                    "thoughtSignature": "opaque-signature-abc"
                  }]
                }
              }]
            }
            """,
            """{ "candidates": [{ "content": { "parts": [{ "text": "done" }] } }] }""");
        var client = new GeminiMessagesClient(new HttpClient(handler), "fake-key");

        var firstResponse = await client.CreateAsync(new MessageCreateParams
        {
            Model = "gemini-3.8-flash",
            MaxTokens = 1024,
            Messages = [new() { Role = Role.User, Content = "go" }],
        });
        var toolUse = Assert.Single(firstResponse.Content.Select(b => b.Value).OfType<ToolUseBlock>());

        // Simulate what ReproductionAgent does on the next turn: echo the tool_use back
        // in the assistant message, using the SAME client that produced the signature --
        // the mapping only survives within one client's lifetime (one agent run).
        await client.CreateAsync(new MessageCreateParams
        {
            Model = "gemini-3.8-flash",
            MaxTokens = 1024,
            Messages =
            [
                new() { Role = Role.User, Content = "go" },
                new()
                {
                    Role = Role.Assistant,
                    Content = new List<ContentBlockParam>
                    {
                        new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input },
                    },
                },
            ],
        });

        using var secondRequest = JsonDocument.Parse(handler.CapturedRequestBodies[1]);
        var echoedPart = secondRequest.RootElement.GetProperty("contents")[1].GetProperty("parts")[0];
        Assert.Equal("opaque-signature-abc", echoedPart.GetProperty("thoughtSignature").GetString());
    }

    /// Returns each given response in order (the last repeats once exhausted), and
    /// records every outgoing request -- so one client/handler pair can be reused across
    /// several CreateAsync calls in a test, which matters whenever behaviour depends on
    /// state held across turns (like the thought-signature mapping).
    private class CapturingHandler(params string[] responseJsonSequence) : HttpMessageHandler
    {
        private int _callCount;
        public List<string> CapturedRequestBodies { get; } = [];
        public string? CapturedRequestUri { get; private set; }
        public string? CapturedRequestBody => CapturedRequestBodies.Count > 0 ? CapturedRequestBodies[^1] : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestUri = request.RequestUri!.ToString();
            CapturedRequestBodies.Add(
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

            var responseJson = responseJsonSequence[Math.Min(_callCount, responseJsonSequence.Length - 1)];
            _callCount++;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
