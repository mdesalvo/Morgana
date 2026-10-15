using System.Text;
using Microsoft.Extensions.AI;

namespace Alembic.Services;

/// <summary>
/// Runs one completion to its natural end, resuming it whenever the provider cuts it at its output limit.
/// </summary>
public static class StreamedCompletion
{
    /// <summary>
    /// How long a round may go silent before it is abandoned and retried once.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Obtains the whole text of one artifact, however many rounds the provider needs to finish writing it.
    /// </summary>
    /// <param name="system">The prompt that composes the model for the artifact.</param>
    /// <param name="request">The artifact to write.</param>
    /// <param name="continuationRequest">The message that precedes the text written so far when a cut-off answer is resumed.</param>
    /// <param name="onResume">Reports the length written so far each time the answer is resumed.</param>
    /// <param name="onStall">Reports the length reached each time a silent round is retried.</param>
    /// <param name="cancellationToken">Stops the completion when the client leaves the page.</param>
    /// <param name="responseFormat">The schema that the provider holds the answer to; null leaves the answer free text.</param>
    /// <returns>The whole answer, fence stripped; empty when the model produced no text at all.</returns>
    /// <exception cref="TimeoutException">The round went silent twice.</exception>
    public static async Task<string> RunAsync(
        IChatClient chatClient,
        string system,
        string request,
        string continuationRequest,
        Action<int>? onResume = null,
        Action<int>? onStall = null,
        CancellationToken cancellationToken = default,
        ChatResponseFormat? responseFormat = null)
    {
        // The conversation opens with the artifact's composed prompt and the request to write it.
        List<ChatMessage> conversation =
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, request)
        ];

        // The answer accumulates across rounds so the artifact is returned whole.
        StringBuilder answer = new StringBuilder();

        // Rounds repeat until the model finishes on its own or a resume cannot continue.
        while (true)
        {
            // Where this round starts, so a round that adds nothing is recognised below.
            int start = answer.Length;

            // One round: the model writes until it stops or the provider cuts it off.
            ChatFinishReason? finishReason = await RoundAsync(chatClient, conversation, answer, onStall, responseFormat, cancellationToken);

            // Any end other than the output limit means the artifact is finished.
            if (finishReason != ChatFinishReason.Length)
                break;

            // A cut-off round that wrote nothing means resuming does not work: another round would pay for the same nothing.
            if (answer.Length == start)
                break;

            // The caller learns how much of the artifact stands, so the client can see the resume happen.
            onResume?.Invoke(answer.Length);

            // The resume goes back as the client's message, since a trailing assistant turn is refused by the Anthropic client.
            // The conversation is rebuilt every round so the provider sees the prompt, the request and one resume turn however long the artifact is.
            conversation =
            [
                conversation[0],
                conversation[1],
                new ChatMessage(ChatRole.User, continuationRequest + answer)
            ];
        }

        // The artifact is handed over without the markdown fence a model may wrap it in.
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
        ChatResponseFormat? responseFormat,
        CancellationToken cancellationToken)
    {
        // Where the round starts, so a stalled attempt can be rolled back to it.
        int start = answer.Length;

        // A silent stream is retried once from the same point.
        for (int attempt = 1; ; attempt++)
        {
            // The provider's reason for ending the stream, taken from the first update that carries one.
            ChatFinishReason? finishReason = null;

            // The deadline is linked to the client's own cancellation and restarts on every update, so it measures silence since the last token.
            using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // The first token gets the same allowance as every later one: a stream that never starts is a stall too.
            stall.CancelAfter(StallTimeout);

            try
            {
                // The output ceiling is Alembic's own since a source file's length is a property of the toolkit it describes.
                await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(
                                   conversation, new ChatOptions { MaxOutputTokens = CodeEmitService.OutputCeiling, ResponseFormat = responseFormat }, stall.Token))
                {
                    // A token arrived: the silence deadline starts again.
                    stall.CancelAfter(StallTimeout);

                    // The first finish reason reported is the one that says why the round ended.
                    finishReason ??= update.FinishReason;

                    // Only text enters the artifact: a reasoning model's updates also carry its thinking.
                    foreach (TextContent text in update.Contents.OfType<TextContent>())
                        answer.Append(text.Text);
                }

                // The stream ended in time: the caller learns whether the provider finished or cut the answer.
                return finishReason;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The half-written round is discarded, so the retry does not continue from text the provider never finished sending.
                answer.Length = start;

                // The second silence in a row ends the attempt.
                if (attempt > 1)
                    throw new TimeoutException(
                        $"The model stopped sending for {StallTimeout.TotalSeconds:0} seconds, twice. "
                        + "Nothing was written for this artifact.");

                // The first silence is reported and the loop retries the round.
                onStall?.Invoke(start);
            }
        }
    }

    /// <summary>
    /// Strips a markdown code fence if the answer arrived wrapped in one.
    /// </summary>
    public static string Unfenced(string answer)
    {
        // Surrounding whitespace is never part of the artifact.
        string text = answer.Trim();

        // An answer that opens without a fence is already the bare artifact.
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        // A fence with no line after it holds no artifact to unwrap.
        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        // The opening fence line, with its language tag, is dropped.
        text = text[(firstNewline + 1)..];

        // The closing fence is cut when the model wrote one.
        int fence = text.LastIndexOf("```", StringComparison.Ordinal);

        // The unwrapped file ends with a single newline.
        return (fence >= 0 ? text[..fence] : text).TrimEnd() + "\n";
    }
}