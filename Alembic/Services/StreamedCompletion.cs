using System.Text;
using Microsoft.Extensions.AI;

namespace Alembic.Services;

/// <summary>
/// One completion, streamed and resumed until the model stops of its own accord.
/// </summary>
/// <remarks>
/// <para>
/// Everything Alembic emits as an artifact — a tool class, a coherence report — is a whole file and
/// a file's length is a property of what it describes rather than a number choosable in advance. So
/// every round sets Alembic's own output ceiling (<see cref="CodeEmitService.OutputCeiling"/>) rather
/// than the tier's and this keeps asking until the answer is finished.
/// </para>
/// <para>
/// The resume goes back as a <em>user</em> message carrying what has been written so far, not as an
/// assistant prefill: <c>MorganaAnthropicClient</c> guards against a trailing assistant turn and a
/// mechanism that works on one provider and silently not on another is worse than the round trip it
/// saves.
/// </para>
/// </remarks>
public static class StreamedCompletion
{
    /// <summary>
    /// How long a round may go silent before it is abandoned and retried once.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Runs one prompt to completion.
    /// </summary>
    /// <param name="chatClient">The client to run on.</param>
    /// <param name="system">The system prompt.</param>
    /// <param name="request">What to ask for.</param>
    /// <param name="continuationRequest">What is sent to resume an answer the provider cut off mid-write.</param>
    /// <param name="onResume">Called when the answer had to be resumed, with the length so far.</param>
    /// <param name="onStall">Called when a round went silent and is about to be retried, with the length reached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The whole answer, fence stripped. Empty when the model produced no text at all.</returns>
    /// <exception cref="TimeoutException">The round went silent twice — once and again on its retry.</exception>
    public static async Task<string> RunAsync(
        IChatClient chatClient,
        string system,
        string request,
        string continuationRequest,
        Action<int>? onResume = null,
        Action<int>? onStall = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> conversation =
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, request)
        ];

        StringBuilder answer = new StringBuilder();

        while (true)
        {
            // Where this round starts, so a round that adds nothing is recognised below.
            int before = answer.Length;

            // One round: the model writes until it stops or the provider cuts it off.
            ChatFinishReason? finishReason = await RoundAsync(chatClient, conversation, answer, onStall, cancellationToken);

            if (finishReason != ChatFinishReason.Length)
                break;

            // No progress means resuming is not working and another round would only spend money to
            // produce the same nothing. This is the loop's only exit besides completion and it is a
            // fact about the last response rather than a budget.
            if (answer.Length == before)
                break;

            onResume?.Invoke(answer.Length);

            // Rebuilt to exactly three messages every round rather than appended to: the assistant's
            // cut-off reply never re-enters the conversation (that would be the assistant-prefill
            // shape the class-level remarks rule out) and a prior continuation request is replaced
            // rather than accumulated, so the provider always sees system + original request + one
            // "here is everything so far, keep going" turn, however many rounds this takes.
            conversation =
            [
                conversation[0],
                conversation[1],
                new ChatMessage(ChatRole.User, continuationRequest + answer)
            ];
        }

        return Unfenced(answer.ToString());
    }

    /// <summary>
    /// Streams one round into the answer and says why the provider ended it, retrying once when the stream goes silent.
    /// </summary>
    private static async Task<ChatFinishReason?> RoundAsync(
        IChatClient chatClient,
        List<ChatMessage> conversation,
        StringBuilder answer,
        Action<int>? onStall,
        CancellationToken cancellationToken)
    {
        // Where the round starts, so a stalled attempt can be rolled back to it.
        int before = answer.Length;

        for (int attempt = 1; ; attempt++)
        {
            ChatFinishReason? finishReason = null;

            // Linked, so a client pressing Stop still cancels through this; and reset on every
            // update, so the deadline is always "silent since the last token" rather than a
            // budget for the whole answer.
            using CancellationTokenSource stall =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            stall.CancelAfter(StallTimeout);

            try
            {
                await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(
                                   conversation, new ChatOptions { MaxOutputTokens = CodeEmitService.OutputCeiling }, stall.Token))
                {
                    stall.CancelAfter(StallTimeout);

                    finishReason ??= update.FinishReason;

                    // Text only. On a reasoning model the updates also carry the thinking and
                    // appending it would put the model's deliberation inside the artifact.
                    foreach (TextContent text in update.Contents.OfType<TextContent>())
                        answer.Append(text.Text);
                }

                return finishReason;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Rolled back to where the round started: the resume hands the model
                // everything written so far, so a half-round left in place would have the retry
                // continue from text the provider never finished sending.
                answer.Length = before;

                if (attempt > 1)
                    throw new TimeoutException(
                        $"The model stopped sending for {StallTimeout.TotalSeconds:0} seconds, twice. "
                        + "Nothing was written for this artifact.");

                onStall?.Invoke(before);
            }
        }
    }

    /// <summary>
    /// Strips a markdown code fence if the answer arrived wrapped in one.
    /// </summary>
    /// <remarks>
    /// Every prompt here asks for a bare artifact and a model that complies loses nothing. This
    /// exists because the failure it prevents is a file that does not parse over three backticks,
    /// which is a poor thing to hand a client at the end of an interview.
    /// </remarks>
    public static string Unfenced(string answer)
    {
        string text = answer.Trim();

        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        text = text[(firstNewline + 1)..];
        int closing = text.LastIndexOf("```", StringComparison.Ordinal);

        return (closing >= 0 ? text[..closing] : text).TrimEnd() + "\n";
    }
}