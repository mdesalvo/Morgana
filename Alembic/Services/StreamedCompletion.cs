using System.Text;
using Microsoft.Extensions.AI;

namespace Alembic.Services;

/// <summary>
/// One completion, streamed and resumed until the model stops of its own accord.
/// Every artifact Alembic emits is a whole file whose length belongs to what it describes, so nothing here sets MaxOutputTokens: the tier's still holds
/// per request and this keeps asking until the answer is finished. The resume goes back as a user message carrying what is written so far, never as an
/// assistant prefill that the Anthropic client would rewrite, since a mechanism that works on one provider and silently not on another costs more than a round trip.
/// </summary>
public static class StreamedCompletion
{
    /// <summary>What is sent to resume an answer the provider cut off mid-write, followed by everything written so far.</summary>
    private const string ContinuationRequest =
        "That was cut off at the provider's output limit. Here is everything you have written so far.\n\n"
        + "Continue from exactly where it stops — the very next character. Do not repeat a line, do not "
        + "start again, do not explain and do not open a fence. Just carry on.\n\n";

    /// <summary>
    /// How long a round may go silent before it is abandoned and retried once.
    /// It only has to outlast any pause a healthy model takes mid-artifact, so it guards against a stream that stopped rather than budgeting a long one.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Runs one prompt to completion, throwing <see cref="TimeoutException"/> when a round goes silent on its retry too.</summary>
    /// <param name="chatClient">The client to run on.</param>
    /// <param name="system">The system prompt.</param>
    /// <param name="request">What to ask for.</param>
    /// <param name="onResume">Called when the answer had to be resumed, with the length so far.</param>
    /// <param name="onStall">Called when a round went silent and is about to be retried, with the length reached.</param>
    /// <param name="cancellationToken">The client's Stop, which cancels every round.</param>
    /// <returns>The whole answer, fence stripped; empty when the model produced no text at all.</returns>
    public static async Task<string> RunAsync(
        IChatClient chatClient,
        string system,
        string request,
        Action<int>? onResume = null,
        Action<int>? onStall = null,
        CancellationToken cancellationToken = default)
    {
        // The pass's prompt and the one request the artifact answers.
        List<ChatMessage> conversation =
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, request)
        ];

        // The artifact as it grows across rounds.
        StringBuilder answer = new StringBuilder();

        // Each round either finishes the answer or is cut off at the provider's limit and resumed.
        while (true)
        {
            // Why this round ended and how long the answer was when it began, which a stall rolls back to.
            ChatFinishReason? finishReason = null;
            int before = answer.Length;

            // A round that goes silent is tried once more before the artifact is given up.
            for (int attempt = 1; ; attempt++)
            {
                // A retried round reports its own ending, never the stalled one's.
                finishReason = null;

                // Linked, so a client pressing Stop still cancels through this; and reset on every
                // update, so the deadline is always "silent since the last token" rather than a
                // budget for the whole answer.
                using CancellationTokenSource stall =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                stall.CancelAfter(StallTimeout);

                try
                {
                    // The answer arrives as a stream, so a long artifact keeps the deadline alive as it is written.
                    await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(
                                       conversation, cancellationToken: stall.Token))
                    {
                        // Every update proves the stream is alive.
                        stall.CancelAfter(StallTimeout);

                        // The first reason reported is the round's: a cut-off at the provider's limit is what calls for a resume.
                        finishReason ??= update.FinishReason;

                        // Text only. On a reasoning model the updates also carry the thinking and
                        // appending it would put the model's deliberation inside the artifact.
                        foreach (TextContent text in update.Contents.OfType<TextContent>())
                            answer.Append(text.Text);
                    }

                    // The round ended on the provider's side, finished or cut off.
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Rolled back to where the round started: the resume below hands the model
                    // everything written so far, so a half-round left in place would have the retry
                    // continue from text the provider never finished sending.
                    answer.Length = before;

                    // A second silence means the provider is not answering: the artifact is given up rather than waited on forever.
                    if (attempt > 1)
                        throw new TimeoutException(
                            $"The model stopped sending for {StallTimeout.TotalSeconds:0} seconds, twice. "
                            + "Nothing was written for this artifact.");

                    // The page says the step is being retried, so a long silence is not read as a hang.
                    onStall?.Invoke(before);
                }
            }

            // Any ending but the provider's length limit is the model's own: the artifact is complete.
            if (finishReason != ChatFinishReason.Length)
                break;

            // No progress means resuming is not working and another round would only spend money to
            // produce the same nothing. This is the loop's only exit besides completion and it is a
            // fact about the last response rather than a budget.
            if (answer.Length == before)
                break;

            // The page says the artifact is being continued and how far it has got.
            onResume?.Invoke(answer.Length);

            // The provider always sees the system prompt, the original request and one continuation carrying everything so far:
            // the cut-off reply never re-enters as an assistant message and an earlier continuation is replaced, never piled up.
            conversation =
            [
                conversation[0],
                conversation[1],
                new ChatMessage(ChatRole.User, ContinuationRequest + answer)
            ];
        }

        // The artifact as a file, free of the fence a model sometimes wraps it in.
        return Unfenced(answer.ToString());
    }

    /// <summary>
    /// Strips a markdown code fence when the answer arrived wrapped in one.
    /// Every prompt asks for a bare artifact, so a compliant model loses nothing, while a fenced one would hand the client a file that does not parse.
    /// </summary>
    public static string Unfenced(string answer)
    {
        // The blank lines a model leaves around its answer belong to no artifact.
        string text = answer.Trim();

        // No fence: the answer is already the artifact.
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        // A fence with nothing after its opening line holds no artifact to unwrap.
        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        // Past the opening line, which carries the fence and its language tag.
        text = text[(firstNewline + 1)..];
        int closing = text.LastIndexOf("```", StringComparison.Ordinal);

        // Up to the closing fence (to the end when the model never closed it) with the one final newline a source file ends on.
        return (closing >= 0 ? text[..closing] : text).TrimEnd() + "\n";
    }
}