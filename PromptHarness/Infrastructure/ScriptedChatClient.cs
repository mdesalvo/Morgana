using Microsoft.Extensions.AI;

namespace PromptHarness.Infrastructure;

/// <summary>
/// Stands where the model would in the deterministic groups: answers each request with the next scripted
/// response and keeps every request it received.
/// </summary>
/// <remarks>A model called more often than scripted is a test fault, so an exhausted script throws rather than answering; a silent model is a client with no script.</remarks>
/// <param name="responses">The answers to give, one per request, in order.</param>
public sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
{
    /// <summary>The scripted answers still to give.</summary>
    private readonly Queue<ChatResponse> pending = new(responses);

    /// <summary>Every request received, in order.</summary>
    public List<ModelRequest> Requests { get; } = [];

    /// <summary>Adds the answer to give to a later request.</summary>
    public void Enqueue(ChatResponse response) => pending.Enqueue(response);

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // The request is kept before the script is consulted, so a call past the end of the script is still on record.
        // Its options are copied: the caller may narrow the same tool list for the next call and each request must
        // keep the tools it was actually offered.
        Requests.Add(new ModelRequest([.. messages], options?.Clone()));

        // A call the script does not cover means the code under test asked the model more often than the case foresaw.
        if (pending.Count == 0)
            throw new InvalidOperationException("The script has no answer left for this request");

        return Task.FromResult(pending.Dequeue());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The same scripted answer as a stream: each of its contents arrives as an update of its own.
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            yield return update;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
