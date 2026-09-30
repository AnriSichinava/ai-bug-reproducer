using Anthropic;
using Anthropic.Models.Messages;

namespace BugReproducer.Agent;

/// The single seam between the agent loop and the Claude API. Everything else in the
/// loop is deterministic, so putting the one non-deterministic call behind an interface
/// makes the loop's control flow (budgets, stop conditions, step recording, evidence
/// checking) testable with a scripted model and no API key.
public interface IMessagesClient
{
    Task<Message> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken = default);
}

public sealed class AnthropicMessagesClient(AnthropicClient client) : IMessagesClient
{
    public Task<Message> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken = default)
        => client.Messages.Create(parameters, cancellationToken);
}
