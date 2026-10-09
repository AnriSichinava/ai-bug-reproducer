using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;

namespace BugReproducer.Agent;

/// Alternative IMessagesClient backed by Google's Gemini API instead of Anthropic's --
/// added so the agent can be tried against Gemini's free tier. ReproductionAgent still
/// builds requests using Anthropic SDK types (MessageCreateParams, ToolUseBlockParam,
/// etc.), since that's the shape the whole loop is written against; this class translates
/// to and from Gemini's wire format via JSON rather than Anthropic's HTTP client.
///
/// The translation works by re-serializing the SDK's own request types with
/// JsonSerializer and reading the result as a JsonDocument. Those types already carry
/// the correct wire-format property names (snake_case, e.g. "tool_use_id") via their own
/// [JsonPropertyName] attributes, so this sidesteps needing to reflect into the SDK's
/// internal C# union types (ContentBlockParam has no public TryPick-style reader, unlike
/// the response-side ContentBlock) -- it's the same JSON either way, just read generically.
public class GeminiMessagesClient(HttpClient httpClient, string apiKey) : IMessagesClient
{
    public async Task<Message> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken = default)
    {
        // MessageCreateParams carries the real, wire-shaped request in RawBodyData;
        // naive reflection-based serialization of the object itself also dumps the raw
        // C# properties alongside it, which are not wire-shaped (Model serializes as
        // "{}", for instance) -- RawBodyData is the one to read.
        using var requestDoc = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var root = requestDoc.RootElement.GetProperty("RawBodyData");
        var modelId = root.GetProperty("model").GetString()!;

        var geminiRequest = new JsonObject();

        if (root.TryGetProperty("system", out var systemElement))
        {
            var systemText = systemElement.ValueKind == JsonValueKind.String
                ? systemElement.GetString()
                : string.Join("\n", systemElement.EnumerateArray()
                    .Select(b => b.TryGetProperty("text", out var t) ? t.GetString() : null)
                    .Where(t => t is not null));

            geminiRequest["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemText }),
            };
        }

        // tool_result blocks only carry the id of the call they answer, but Gemini's
        // functionResponse needs the tool's name -- recover it by scanning every
        // preceding tool_use block in the whole history first.
        var toolNamesById = new Dictionary<string, string>();
        if (root.TryGetProperty("messages", out var scanMessages))
        {
            foreach (var message in scanMessages.EnumerateArray())
            {
                if (!message.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in contentEl.EnumerateArray())
                {
                    if (block.GetProperty("type").GetString() == "tool_use")
                    {
                        toolNamesById[block.GetProperty("id").GetString()!] = block.GetProperty("name").GetString()!;
                    }
                }
            }
        }

        var contents = new JsonArray();
        if (root.TryGetProperty("messages", out var messages))
        {
            foreach (var message in messages.EnumerateArray())
            {
                var role = message.GetProperty("role").GetString() == "assistant" ? "model" : "user";
                var parts = new JsonArray();
                var content = message.GetProperty("content");

                if (content.ValueKind == JsonValueKind.String)
                {
                    parts.Add(new JsonObject { ["text"] = content.GetString() });
                }
                else
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        switch (block.GetProperty("type").GetString())
                        {
                            case "text":
                                parts.Add(new JsonObject { ["text"] = block.GetProperty("text").GetString() });
                                break;

                            case "tool_use":
                                parts.Add(new JsonObject
                                {
                                    ["functionCall"] = new JsonObject
                                    {
                                        ["name"] = block.GetProperty("name").GetString(),
                                        ["args"] = JsonNode.Parse(block.GetProperty("input").GetRawText()),
                                    },
                                });
                                break;

                            case "tool_result":
                                var toolUseId = block.GetProperty("tool_use_id").GetString()!;
                                string? resultText = null;
                                if (block.TryGetProperty("content", out var resultContent))
                                {
                                    resultText = resultContent.ValueKind == JsonValueKind.String
                                        ? resultContent.GetString()
                                        : resultContent.GetRawText();
                                }
                                parts.Add(new JsonObject
                                {
                                    ["functionResponse"] = new JsonObject
                                    {
                                        ["name"] = toolNamesById.GetValueOrDefault(toolUseId, "unknown_tool"),
                                        ["response"] = new JsonObject { ["result"] = resultText },
                                    },
                                });
                                break;

                            // "thinking"/"redacted_thinking" have no Gemini equivalent; dropped.
                        }
                    }
                }

                if (parts.Count > 0)
                {
                    contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
                }
            }
        }
        geminiRequest["contents"] = contents;

        if (root.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array)
        {
            var declarations = new JsonArray();
            foreach (var tool in toolsElement.EnumerateArray())
            {
                if (!tool.TryGetProperty("name", out var nameEl)) continue;

                var declaration = new JsonObject { ["name"] = nameEl.GetString() };
                if (tool.TryGetProperty("description", out var descEl))
                {
                    declaration["description"] = descEl.GetString();
                }
                if (tool.TryGetProperty("input_schema", out var schemaEl))
                {
                    // Anthropic's input_schema ({type, properties, required}) is already
                    // the same shape Gemini's "parameters" expects -- pass it through.
                    declaration["parameters"] = JsonNode.Parse(schemaEl.GetRawText());
                }
                declarations.Add(declaration);
            }
            geminiRequest["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        }

        if (root.TryGetProperty("max_tokens", out var maxTokensEl))
        {
            geminiRequest["generationConfig"] = new JsonObject { ["maxOutputTokens"] = maxTokensEl.GetInt32() };
        }

        var requestUri = $"https://generativelanguage.googleapis.com/v1beta/models/{modelId}:generateContent";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create((JsonNode)geminiRequest),
        };
        httpRequest.Headers.Add("x-goog-api-key", apiKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini API request failed ({(int)response.StatusCode}): {responseBody}");
        }

        return BuildAnthropicShapedMessage(responseBody, modelId);
    }

    /// Reconstructs an Anthropic SDK Message from Gemini's response JSON, by building
    /// equivalent wire JSON and deserializing it -- Message has many required members
    /// (Container, Diagnostics, StopDetails, a fully-populated Usage, ...) meant for
    /// deserializing a real API response, not for a plain object initializer, so this is
    /// the same technique the test fixtures (ModelResponses.Build) use for the same reason.
    private static Message BuildAnthropicShapedMessage(string geminiResponseBody, string modelId)
    {
        using var responseDoc = JsonDocument.Parse(geminiResponseBody);
        var candidate = responseDoc.RootElement.GetProperty("candidates")[0];
        var candidateContent = candidate.GetProperty("content");

        var anthropicBlocks = new JsonArray();
        var hasToolCall = false;
        if (candidateContent.TryGetProperty("parts", out var responseParts))
        {
            foreach (var part in responseParts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textEl))
                {
                    anthropicBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = textEl.GetString(), ["citations"] = null });
                }
                else if (part.TryGetProperty("functionCall", out var functionCallEl))
                {
                    hasToolCall = true;
                    var argsJson = functionCallEl.TryGetProperty("args", out var argsEl) ? argsEl.GetRawText() : "{}";
                    anthropicBlocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = $"toolu_{Guid.NewGuid():N}",
                        ["name"] = functionCallEl.GetProperty("name").GetString(),
                        ["input"] = JsonNode.Parse(argsJson),
                        ["caller"] = null,
                    });
                }
            }
        }

        var inputTokens = 0;
        var outputTokens = 0;
        if (responseDoc.RootElement.TryGetProperty("usageMetadata", out var usageEl))
        {
            inputTokens = usageEl.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            outputTokens = usageEl.TryGetProperty("candidatesTokenCount", out var ct) ? ct.GetInt32() : 0;
        }

        var anthropicMessageJson = new JsonObject
        {
            ["id"] = $"msg_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = modelId,
            ["content"] = anthropicBlocks,
            ["stop_reason"] = hasToolCall ? "tool_use" : "end_turn",
            ["stop_sequence"] = null,
            ["container"] = null,
            ["diagnostics"] = null,
            ["stop_details"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
                ["cache_creation"] = null,
                ["cache_creation_input_tokens"] = null,
                ["cache_read_input_tokens"] = null,
                ["inference_geo"] = null,
                ["output_tokens_details"] = null,
                ["server_tool_use"] = null,
                ["service_tier"] = null,
            },
        };

        return JsonSerializer.Deserialize<Message>(anthropicMessageJson.ToJsonString())!;
    }
}
