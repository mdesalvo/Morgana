using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Morgana.AI.LanguageModels.Google;

/// <summary>
/// Decorator over the Gemini <see cref="IChatClient"/> that reports usage as Microsoft.Extensions.AI defines it.
/// </summary>
/// <remarks>
/// Gemini counts the model's thinking apart from its answer and the SDK copies that split as it is, while
/// <see cref="UsageDetails.OutputTokenCount"/> must include the reasoning tokens: Gemini bills them as output.
/// </remarks>
/// <param name="innerClient">The SDK's chat client of one tier.</param>
internal sealed class MorganaGoogleClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        // The model answers as the SDK reports it.
        ChatResponse chatResponse = await base.GetResponseAsync(chatMessages, chatOptions, cancellationToken);

        // The whole response's usage reaches the dust with its thinking counted as output.
        FoldReasoningIntoOutput(chatResponse.Usage);
        return chatResponse;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate chatResponseUpdate in base.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken))
        {
            // A stream reports its usage in the update that closes it, which is corrected before it travels on.
            foreach (UsageContent usageContent in chatResponseUpdate.Contents.OfType<UsageContent>())
                FoldReasoningIntoOutput(usageContent.Details);

            // Every chunk reaches the user untouched.
            yield return chatResponseUpdate;
        }
    }

    /// <summary>Counts the reasoning tokens of a usage report inside its output tokens.</summary>
    /// <param name="usageDetails">The usage that the SDK reported; <c>null</c> when the response carries none.</param>
    private static void FoldReasoningIntoOutput(UsageDetails? usageDetails)
    {
        // A response with no thinking is already reported as Microsoft.Extensions.AI defines it.
        if (usageDetails?.ReasoningTokenCount is not > 0)
            return;

        // The thinking is billed as output, so it joins the answer's tokens on the output axis.
        usageDetails.OutputTokenCount = (usageDetails.OutputTokenCount ?? 0) + usageDetails.ReasoningTokenCount;
    }
}