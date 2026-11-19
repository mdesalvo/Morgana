using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Morgana.AI.ChatClients;

/// <summary>Turns an answer that a provider ended on its content filter into a <see cref="ContentFilterRefusalException"/>, so callers recognise it like a refusal the provider throws.</summary>
internal sealed class ContentFilterChatClient : DelegatingChatClient
{
    /// <summary>Wraps <paramref name="innerClient"/>, raising every answer that its provider ended on the content filter.</summary>
    public ContentFilterChatClient(IChatClient innerClient) : base(innerClient)
    {
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        // The finish reason is known only once the provider has answered in full.
        ChatResponse chatResponse = await base.GetResponseAsync(chatMessages, chatOptions, cancellationToken);

        // The empty or truncated text of a filtered answer would be read as a failure of the model, so the refusal is raised in its place.
        if (chatResponse.FinishReason == ChatFinishReason.ContentFilter)
            throw new ContentFilterRefusalException();

        // Any other finish reason is the caller's to read: the answer goes back untouched.
        return chatResponse;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Each update goes on as it arrives, so the check never delays the stream the user watches.
        await foreach (ChatResponseUpdate chatResponseUpdate in
            base.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken))
        {
            // The update that carries the content filter ends the stream: it is raised instead of being handed on as a last chunk.
            if (chatResponseUpdate.FinishReason == ChatFinishReason.ContentFilter)
                throw new ContentFilterRefusalException();

            // An update the provider did not end on its filter reaches the caller as it came.
            yield return chatResponseUpdate;
        }
    }
}

/// <summary>Raised when a provider ends its answer on its own content filter, so that a refusal reported as a finish reason reaches the callers like one the provider throws.</summary>
public sealed class ContentFilterRefusalException : Exception
{
    /// <summary>Builds the refusal with the message that the logs and the callers read.</summary>
    public ContentFilterRefusalException()
        : base("The provider refused the content: it ended the answer on its content filter.")
    {
    }
}