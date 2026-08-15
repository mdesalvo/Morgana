using System.Text;
using Microsoft.Extensions.AI;

namespace Alembic.Services;

/// <summary>
/// One completion, streamed and resumed until the model stops of its own accord.
/// </summary>
/// <remarks>
/// <para>
/// Everything Alembic emits as an artifact — a tool class, a harness scenario — is a whole file, and
/// a file's length is a property of what it describes rather than a number choosable in advance. So
/// nothing here declares an output ceiling: what the deployment's tier configures still holds per
/// request, and this keeps asking until the answer is finished.
/// </para>
/// <para>
/// The resume goes back as a <em>user</em> message carrying what has been written so far, not as an
/// assistant prefill: <c>MorganaAnthropicClient</c> guards against a trailing assistant turn, and a
/// mechanism that works on one provider and silently not on another is worse than the round trip it
/// saves.
/// </para>
/// </remarks>
public static class StreamedCompletion
{
    /// <summary>
    /// What is sent to resume an answer the provider cut off mid-write.
    /// </summary>
    private const string ContinuationRequest =
        "That was cut off at the provider's output limit. Here is everything you have written so far.\n\n"
        + "Continue from exactly where it stops — the very next character. Do not repeat a line, do not "
        + "start again, do not explain, and do not open a fence. Just carry on.\n\n";

    /// <summary>
    /// Runs one prompt to completion.
    /// </summary>
    /// <param name="chatClient">The client to run on.</param>
    /// <param name="system">The system prompt.</param>
    /// <param name="request">What to ask for.</param>
    /// <param name="onResume">Called when the answer had to be resumed, with the length so far.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The whole answer, fence stripped. Empty when the model produced no text at all.</returns>
    public static async Task<string> RunAsync(
        IChatClient chatClient,
        string system,
        string request,
        Action<int>? onResume = null,
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
            ChatFinishReason? finishReason = null;
            int before = answer.Length;

            await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(
                               conversation, cancellationToken: cancellationToken))
            {
                finishReason ??= update.FinishReason;

                // Text only. On a reasoning model the updates also carry the thinking, and appending
                // it would put the model's deliberation inside the artifact.
                foreach (TextContent text in update.Contents.OfType<TextContent>())
                    answer.Append(text.Text);
            }

            if (finishReason != ChatFinishReason.Length)
                break;

            // No progress means resuming is not working, and another round would only spend money to
            // produce the same nothing. This is the loop's only exit besides completion, and it is a
            // fact about the last response rather than a budget.
            if (answer.Length == before)
                break;

            onResume?.Invoke(answer.Length);

            // Rebuilt to exactly three messages every round rather than appended to: the assistant's
            // cut-off reply never re-enters the conversation (that would be the assistant-prefill
            // shape the class-level remarks rule out), and a prior continuation request is replaced
            // rather than accumulated, so the provider always sees system + original request + one
            // "here is everything so far, keep going" turn, however many rounds this takes.
            conversation =
            [
                conversation[0],
                conversation[1],
                new ChatMessage(ChatRole.User, ContinuationRequest + answer)
            ];
        }

        return Unfenced(answer.ToString());
    }

    /// <summary>
    /// Strips a markdown code fence if the answer arrived wrapped in one.
    /// </summary>
    /// <remarks>
    /// Every prompt here asks for a bare artifact, and a model that complies loses nothing. This
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
