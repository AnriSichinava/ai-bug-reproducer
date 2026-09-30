using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;

namespace BugReproducer.Agent;

/// The agent loop: ask the model what to do, run the tool it picked through MCP, feed
/// the result back, repeat until it concludes or runs out of budget.
///
/// The loop is written out rather than delegated to an automatic tool-invocation helper
/// on purpose. Owning it is what makes it possible to record every step as it happens,
/// enforce a step budget, and force the run to end through one explicit exit (the
/// conclude tool) instead of ending whenever the model happens to stop asking for tools.
public class ReproductionAgent(
    IMessagesClient messages,
    IBrowserTools browserTools,
    AgentOptions options)
{
    private const string ConcludeToolName = "conclude";

    public async Task<ReproductionOutcome> RunAsync(
        BugReportContext bug,
        IAgentStepSink sink,
        CancellationToken cancellationToken = default)
    {
        var tools = await BuildToolDefinitionsAsync();

        List<MessageParam> history =
        [
            new() { Role = Role.User, Content = BuildTaskMessage(bug) },
        ];

        var stepNumber = 0;

        // Step numbers that actually produced tool output. Evidence may only cite these:
        // a citation pointing anywhere else is a claim about something that never happened.
        var citableSteps = new HashSet<int>();

        for (var iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            var response = await messages.CreateAsync(
                new MessageCreateParams
                {
                    Model = options.Model,
                    MaxTokens = options.MaxTokens,
                    System = BuildSystemPrompt(),
                    Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },
                    Tools = tools,
                    Messages = history,
                },
                cancellationToken);

            if (response.StopReason == "refusal")
            {
                var explanation = response.StopDetails?.Explanation ?? "The model declined the request.";
                await sink.RecordAsync(new AgentStepRecord(++stepNumber, explanation, null, null, null, "refusal"), cancellationToken);
                return new ReproductionOutcome(
                    ReproductionVerdict.Inconclusive, explanation, [], stepNumber, StopReason.Refused, []);
            }

            var (assistantContent, toolCalls, reasoning) = UnpackResponse(response);

            if (toolCalls.Count == 0)
            {
                // The model answered in prose instead of calling conclude. That is not a
                // verdict, so the run ends inconclusive rather than us parsing its text.
                await sink.RecordAsync(
                    new AgentStepRecord(++stepNumber, reasoning, null, null, null, null), cancellationToken);
                return new ReproductionOutcome(
                    ReproductionVerdict.Inconclusive,
                    reasoning ?? "The model stopped without reaching a conclusion.",
                    [], stepNumber, StopReason.ModelStoppedWithoutConcluding, []);
            }

            history.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

            List<ContentBlockParam> toolResults = [];
            var reasoningForFirstStep = reasoning;

            foreach (var toolCall in toolCalls)
            {
                stepNumber++;
                var inputJson = JsonSerializer.Serialize(toolCall.Input);

                if (toolCall.Name == ConcludeToolName)
                {
                    await sink.RecordAsync(
                        new AgentStepRecord(stepNumber, reasoningForFirstStep, ConcludeToolName, inputJson, null, null),
                        cancellationToken);

                    return BuildOutcome(toolCall.Input, stepNumber, citableSteps);
                }

                var result = await browserTools.CallToolAsync(
                    toolCall.Name,
                    toolCall.Input.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
                    cancellationToken);

                await sink.RecordAsync(
                    new AgentStepRecord(
                        stepNumber,
                        reasoningForFirstStep,
                        toolCall.Name,
                        inputJson,
                        result.Success ? result.Text : null,
                        result.Success ? null : result.Text),
                    cancellationToken);

                citableSteps.Add(stepNumber);
                reasoningForFirstStep = null; // The turn's reasoning belongs to its first step only.

                // A failed tool call is fed back as a normal result, not thrown: the model
                // should see the failure and adapt, the same way a person would.
                toolResults.Add(new ToolResultBlockParam
                {
                    ToolUseID = toolCall.ID,
                    Content = result.Text,
                });
            }

            history.Add(new MessageParam { Role = Role.User, Content = toolResults });
        }

        return new ReproductionOutcome(
            ReproductionVerdict.Inconclusive,
            $"Ran out of step budget after {options.MaxIterations} turns without reaching a conclusion.",
            [], stepNumber, StopReason.StepBudgetExhausted, []);
    }

    private static (List<ContentBlockParam> AssistantContent, List<ToolUseBlock> ToolCalls, string? Reasoning)
        UnpackResponse(Message response)
    {
        List<ContentBlockParam> assistantContent = [];
        List<ToolUseBlock> toolCalls = [];
        var reasoning = new StringBuilder();

        foreach (var block in response.Content)
        {
            if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                // The signature has to survive the round trip unmodified or the API
                // rejects the next request.
                assistantContent.Add(new ThinkingBlockParam
                {
                    Thinking = thinking.Thinking,
                    Signature = thinking.Signature,
                });
                Append(reasoning, thinking.Thinking);
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                assistantContent.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickText(out TextBlock? text))
            {
                assistantContent.Add(new TextBlockParam { Text = text.Text });
                Append(reasoning, text.Text);
            }
            else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
            {
                assistantContent.Add(new ToolUseBlockParam
                {
                    ID = toolUse.ID,
                    Name = toolUse.Name,
                    Input = toolUse.Input,
                });
                toolCalls.Add(toolUse);
            }
        }

        return (assistantContent, toolCalls, reasoning.Length == 0 ? null : reasoning.ToString());

        static void Append(StringBuilder builder, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(value);
        }
    }

    private static ReproductionOutcome BuildOutcome(
        IReadOnlyDictionary<string, JsonElement> input,
        int stepsUsed,
        HashSet<int> citableSteps)
    {
        var verdict = input.TryGetValue("verdict", out var verdictElement)
            ? ParseVerdict(verdictElement.GetString())
            : ReproductionVerdict.Inconclusive;

        var reasoning = input.TryGetValue("reasoning", out var reasoningElement)
            ? reasoningElement.GetString() ?? string.Empty
            : string.Empty;

        List<EvidenceItem> evidence = [];
        List<EvidenceItem> unverifiable = [];

        if (input.TryGetValue("evidence", out var evidenceElement)
            && evidenceElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in evidenceElement.EnumerateArray())
            {
                var step = entry.TryGetProperty("step", out var stepElement) && stepElement.TryGetInt32(out var parsed)
                    ? parsed
                    : -1;
                var observation = entry.TryGetProperty("observation", out var obsElement)
                    ? obsElement.GetString() ?? string.Empty
                    : string.Empty;

                var item = new EvidenceItem(step, observation);
                // Cheap integrity check: the model can describe a step's output however it
                // likes, but it cannot cite a step that never ran.
                (citableSteps.Contains(step) ? evidence : unverifiable).Add(item);
            }
        }

        return new ReproductionOutcome(
            verdict, reasoning, evidence, stepsUsed, StopReason.ModelConcluded, unverifiable);
    }

    private static ReproductionVerdict ParseVerdict(string? value) => value switch
    {
        "REPRODUCED" => ReproductionVerdict.Reproduced,
        "NOT_REPRODUCED" => ReproductionVerdict.NotReproduced,
        _ => ReproductionVerdict.Inconclusive,
    };

    private async Task<ToolUnion[]> BuildToolDefinitionsAsync()
    {
        var mcpTools = await browserTools.ListToolsAsync();

        var definitions = mcpTools
            .Select(tool => new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = ToInputSchema(tool.JsonSchema),
            })
            .ToList();

        definitions.Add(BuildConcludeTool());

        return [.. definitions.Select(tool => new ToolUnion(tool))];
    }

    /// MCP publishes a full JSON Schema per tool; the Messages API wants the properties and
    /// required list from it. Reusing the server's own schema means the two can't drift.
    private static InputSchema ToInputSchema(JsonElement schema)
    {
        var properties = new Dictionary<string, JsonElement>();
        if (schema.TryGetProperty("properties", out var propertiesElement))
        {
            foreach (var property in propertiesElement.EnumerateObject())
            {
                properties[property.Name] = property.Value;
            }
        }

        List<string> required = [];
        if (schema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            required.AddRange(requiredElement.EnumerateArray()
                .Select(item => item.GetString())
                .Where(name => name is not null)!);
        }

        return new InputSchema { Properties = properties, Required = required };
    }

    /// The only way a run ends cleanly. Making the conclusion a tool call rather than
    /// free text means the verdict arrives already structured, and the loop terminates on
    /// an explicit signal instead of on the model merely going quiet.
    private static Tool BuildConcludeTool() => new()
    {
        Name = ConcludeToolName,
        Description =
            "Finish the investigation and report your verdict. Call this exactly once, when you have "
            + "either observed the reported failure, confirmed it does not occur, or established that "
            + "you cannot complete the steps.",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["verdict"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    @enum = new[] { "REPRODUCED", "NOT_REPRODUCED", "INCONCLUSIVE" },
                    description = "REPRODUCED: you performed the steps and directly observed the reported failure. "
                        + "NOT_REPRODUCED: you performed every step successfully and the failure did not occur. "
                        + "INCONCLUSIVE: you could not complete the steps, so neither of the above is established.",
                }),
                ["reasoning"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "Your interpretation of what happened and, if you can tell, why. "
                        + "This is understood to be inference, not fact.",
                }),
                ["evidence"] = JsonSerializer.SerializeToElement(new
                {
                    type = "array",
                    description = "The concrete observations backing your verdict. Every entry must cite the "
                        + "step number whose tool output you are describing. Do not put inference here.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            step = new { type = "integer", description = "The step number whose output you observed" },
                            observation = new { type = "string", description = "What that step's output actually showed" },
                        },
                        required = new[] { "step", "observation" },
                    },
                }),
            },
            Required = ["verdict", "reasoning", "evidence"],
        },
    };

    private string BuildSystemPrompt() =>
        $"""
        You are a bug reproduction agent. You are given a bug report about a web application
        and you must determine, by actually driving a browser, whether the reported problem
        really happens.

        The application under test is at {options.TargetBaseUrl}.

        How to work:
        - Investigate by using the tools. Never assume what a page contains: call
          get_page_snapshot to see the page and to get selectors you can click or fill.
        - Work through the reported steps in order, verifying each one took effect before
          moving on. If a step needs an account, register one.
        - When you reach the step that is supposed to fail, gather hard evidence: check
          get_network_log for failing requests and get_console_errors for script errors,
          and take a screenshot.
        - You have a limited number of turns. Do not keep exploring once you can answer.

        Two things are kept strictly apart, and you must respect the difference:
        - Observed facts: what the tools actually returned. A 500 in the network log is a fact.
        - Inference: your explanation of why. A guess about the root cause is not a fact.
        Put facts in the evidence list, citing the step that produced them. Put your
        interpretation in reasoning. Never state inference as though you observed it.

        Finish by calling the conclude tool. Be careful with the difference between
        NOT_REPRODUCED and INCONCLUSIVE: only report NOT_REPRODUCED if you actually carried
        out the steps and the failure did not happen. If you could not get far enough to
        find out, that is INCONCLUSIVE. Reporting a bug as not reproduced when you simply
        could not test it is the worst outcome here.
        """;

    private static string BuildTaskMessage(BugReportContext bug) =>
        $"""
        Reproduce this bug report.

        Title: {bug.Title}

        Description:
        {bug.Description}
        """;
}
