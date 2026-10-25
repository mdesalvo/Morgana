using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Morgana.AI.LanguageModels.OpenAI;

// The OpenAI SDK marks its whole Responses surface as evaluation-only: this class is the one place that uses it.
#pragma warning disable OPENAI001

/// <summary>
/// Decorator over the OpenAI Responses <see cref="IChatClient"/> that keeps the history with Morgana and reports the cache writes.
/// </summary>
/// <remarks>
/// Microsoft.Extensions.AI maps neither the tokens written into the prompt cache nor the absence of storage, so this client states both.
/// </remarks>
/// <param name="innerClient">The Responses chat client of one tier.</param>
internal sealed class MorganaOpenAIClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        // The model answers on options that keep the history with Morgana.
        ChatResponse chatResponse = await base.GetResponseAsync(chatMessages, ForResponses(chatOptions), cancellationToken);

        // The cache writes are in the raw response only, so they are copied where the dust reads them.
        if (chatResponse.RawRepresentation is ResponseResult responseResult)
            ReportCacheWrites(chatResponse.Usage, responseResult);

        return chatResponse;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate chatResponseUpdate in base.GetStreamingResponseAsync(chatMessages, ForResponses(chatOptions), cancellationToken))
        {
            // A stream reports its usage in the update that completes the response: its cache writes are added before it travels on.
            if (chatResponseUpdate.RawRepresentation is StreamingResponseCompletedUpdate completedUpdate)
            {
                foreach (UsageContent usageContent in chatResponseUpdate.Contents.OfType<UsageContent>())
                    ReportCacheWrites(usageContent.Details, completedUpdate.Response);
            }

            // Every chunk reaches the user untouched.
            yield return chatResponseUpdate;
        }
    }

    /// <summary>Builds the options that one call forwards: the history stays with Morgana and the reasoning can be handed back.</summary>
    /// <param name="chatOptions">The caller's options; <c>null</c> when it sets none. They are never modified.</param>
    private static ChatOptions ForResponses(ChatOptions? chatOptions)
    {
        // The caller's options stay untouched: they are reused above this client.
        ChatOptions forwardedOptions = chatOptions?.Clone() ?? new ChatOptions();

        // Morgana's conversation id serves the dust accounting above the provider: sent on, OpenAI would read it as a previous response and reject it.
        forwardedOptions.ConversationId = null;

        // A factory that the caller set keeps contributing its own options.
        Func<IChatClient, object?>? callerFactory = chatOptions?.RawRepresentationFactory;
        forwardedOptions.RawRepresentationFactory = chatClient =>
        {
            CreateResponseOptions responseOptions = callerFactory?.Invoke(chatClient) as CreateResponseOptions ?? new CreateResponseOptions();

            // Left at the server default, the service would keep the history and Microsoft.Agents.AI would take it for the conversation's own.
            responseOptions.StoredOutputEnabled = false;

            // With nothing stored, a reasoning item can be handed back on the next call only with its encrypted content.
            if (!responseOptions.IncludedProperties.Contains(IncludedResponseProperty.ReasoningEncryptedContent))
                responseOptions.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);

            // The request goes out on the caller's options plus these two settings.
            return responseOptions;
        };

        // The options that the Responses call is made on.
        return forwardedOptions;
    }

    /// <summary>Writes the prompt tokens that a response wrote into the cache among the usage's extra counts.</summary>
    /// <param name="usageDetails">The usage that the client reported; <c>null</c> when the response carries none.</param>
    /// <param name="responseResult">The raw response that holds the count Microsoft.Extensions.AI does not map.</param>
    private static void ReportCacheWrites(UsageDetails? usageDetails, ResponseResult responseResult)
    {
        // A response without usage or one that wrote nothing has no cache write to report.
        int cacheWriteTokens = responseResult.Usage?.InputTokenDetails?.CacheWriteTokenCount ?? 0;
        if (usageDetails is null || cacheWriteTokens <= 0)
            return;

        // The dust ledger prices these tokens apart from the fresh and the cached ones.
        usageDetails.AdditionalCounts ??= new AdditionalPropertiesDictionary<long>();
        usageDetails.AdditionalCounts[Constants.UsageCounts.CacheCreationInputTokens] = cacheWriteTokens;
    }
}
#pragma warning restore OPENAI001
