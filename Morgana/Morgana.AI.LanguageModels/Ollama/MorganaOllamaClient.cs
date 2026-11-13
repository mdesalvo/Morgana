using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Morgana.AI.LanguageModels.Ollama;

/// <summary>
/// Ends a call that its caller stops at once, whatever OllamaSharp's reader is waiting on.
/// OllamaSharp reads a streamed answer without the caller's token and ends it quietly when it sees it cancelled.
/// </summary>
/// <param name="innerClient">The OllamaSharp client of one tier.</param>
internal sealed class MorganaOllamaClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The provider's stream, enumerated by hand so that each read can be raced with the caller's token.
        IAsyncEnumerator<ChatResponseUpdate> chatResponseUpdates = base.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);

        // The read in flight, which the cleanup must know about when the call is stopped while it waits.
        Task<bool>? pendingRead = null;
        try
        {
            while (true)
            {
                // OllamaSharp's reader waits on the network without the token, so the read is started apart from it.
                pendingRead = chatResponseUpdates.MoveNextAsync().AsTask();

                // The exception reaches the caller the moment the call is stopped.
                bool hasUpdate = await pendingRead.WaitAsync(cancellationToken);

                // The read finished, so there is nothing left in flight to clean up.
                pendingRead = null;

                // The provider has no more updates.
                if (!hasUpdate)
                    break;

                // Each update reaches the caller as the provider sent it.
                yield return chatResponseUpdates.Current;
            }

            // OllamaSharp ends a stopped stream quietly and a stopped call must never read as a completed answer.
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            // No read is pending, so the stream closes at once.
            if (pendingRead is null)
                await chatResponseUpdates.DisposeAsync();
            // A read still pending is left to finish and then the stream is closed, which drops the connection and makes Ollama stop generating.
            // The exception of that abandoned read is observed so that it is never reported as unobserved.
            else
                _ = pendingRead.ContinueWith(read => { _ = read.Exception; return chatResponseUpdates.DisposeAsync().AsTask(); }, TaskScheduler.Default).Unwrap();
        }
    }
}