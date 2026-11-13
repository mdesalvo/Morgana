using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Morgana.AI.Interfaces;

namespace Morgana.AI.ChatClients;

/// <summary>
/// Charges every model call passing through it to the conversation's dust budget, under the role it was built for.
/// Each instance carries its own role because it is built where the role is known: per call for the framework actors,
/// per agent for a domain agent and its reducer, per run for <c>/compact</c>. No call is therefore charged twice.
/// </summary>
internal sealed class DustAccountingChatClient : DelegatingChatClient
{
    /// <summary>The ledger the charge is written to, which fails open on its own.</summary>
    private readonly IDustLimitService dustLimitService;

    /// <summary>The tier's tokens per dust unit and cache weights, which turn a usage report into dust.</summary>
    private readonly Records.MagicDustPricing dustPricing;

    /// <summary>The role the charge is filed under, such as <c>Morgana</c> or <c>Morgana (Billing/Efficiency)</c>.</summary>
    private readonly string llmRole;

    /// <summary>
    /// The conversation charged when the call itself names none.
    /// An agent's runs reach the client through Microsoft.Agents.AI, which sets no conversation id on the options.
    /// </summary>
    private readonly string? conversationId;

    /// <summary>Wraps <paramref name="innerClient"/>, charging every call priced by <paramref name="dustPricing"/> to <paramref name="llmRole"/>.</summary>
    public DustAccountingChatClient(
        IChatClient innerClient,
        IDustLimitService dustLimitService,
        Records.MagicDustPricing dustPricing,
        string llmRole,
        string? conversationId = null) : base(innerClient)
    {
        this.dustLimitService = dustLimitService;
        this.dustPricing = dustPricing;
        this.llmRole = llmRole;
        this.conversationId = conversationId;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        // Only a completed call reports its usage, so the charge waits for the provider's answer.
        ChatResponse chatResponse = await base.GetResponseAsync(chatMessages, chatOptions, cancellationToken);

        // The usage of the whole call, charged before the answer goes back to whoever asked.
        await ChargeAsync(ResolveConversationId(chatOptions), chatResponse.Usage);
        return chatResponse;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Providers deliver streaming usage as a cumulative total, not per-chunk deltas: the last
        // one seen is the call's usage and summing them would double-count.
        UsageDetails? usageDetails = null;

        try
        {
            // Each update goes on as it arrives, so metering never delays the stream the user watches.
            await foreach (ChatResponseUpdate chatResponseUpdate in
                base.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken))
            {
                // Only the usage is noted: the text and tool calls the update carries pass untouched.
                foreach (UsageContent usageContent in chatResponseUpdate.Contents.OfType<UsageContent>())
                    usageDetails = usageContent.Details;

                yield return chatResponseUpdate;
            }
        }
        finally
        {
            // The charge is made however the stream ends, since a turn being stopped may stop reading right
            // after the usage arrived and the provider billed that call all the same.
            await ChargeAsync(ResolveConversationId(chatOptions), usageDetails);
        }
    }

    /// <summary>
    /// The conversation a call is charged to: the one its options name, otherwise the one this client was built for.
    /// The framework actors name it on every call while an agent's runs name none.
    /// </summary>
    private string? ResolveConversationId(ChatOptions? chatOptions) =>
        string.IsNullOrEmpty(chatOptions?.ConversationId) ? conversationId : chatOptions.ConversationId;

    /// <summary>
    /// Converts a usage report into dust and charges it, doing nothing without a conversation, a report or a cost; never throws.
    /// A provider reports the whole prompt as input with the cache's share inside it, so each share is weighed at its own price.
    /// </summary>
    private async Task ChargeAsync(string? chargedConversationId, UsageDetails? usageDetails)
    {
        // A call nobody can be billed for has nothing to charge, nor has one that reported no usage.
        if (string.IsNullOrEmpty(chargedConversationId) || usageDetails is null)
            return;

        // The prompt as the provider counts it, with the tokens its cache served inside that total.
        long totalInput = usageDetails.InputTokenCount ?? 0;
        long cacheRead = usageDetails.CachedInputTokenCount ?? 0;

        // The tokens written to the cache, reported under Morgana's own key by the clients that know them.
        long cacheWrite = 0;
        if (usageDetails.AdditionalCounts is not null &&
            usageDetails.AdditionalCounts.TryGetValue(Constants.UsageCounts.CacheCreationInputTokens, out long cacheCreationTokens))
            cacheWrite = cacheCreationTokens;

        // Fresh tokens are what remains of the total once both cache shares are taken out, never below zero
        // for an adapter that reports the shares overlapping.
        long freshInput = Math.Max(0, totalInput - cacheRead - cacheWrite);

        // The prompt in fresh-token equivalents: each cache share at its weight against a fresh token.
        double effectiveInput =
            freshInput +
            cacheRead * dustPricing.CachedInputWeight +
            cacheWrite * dustPricing.CacheCreationWeight;

        // The generated tokens, which every vendor prices apart from the prompt.
        long outputTokens = usageDetails.OutputTokenCount ?? 0;

        // Each axis converted at its own rate, an axis priced at zero tokens per unit being free.
        double dust =
            (dustPricing.InputTokensPerDustUnit > 0
                ? effectiveInput / dustPricing.InputTokensPerDustUnit
                : 0.0) +
            (dustPricing.OutputTokensPerDustUnit > 0
                ? (double)outputTokens / dustPricing.OutputTokensPerDustUnit
                : 0.0);

        // A free call, such as one to a local model priced at zero, writes no row to the ledger.
        if (dust <= 0.0)
            return;

        try
        {
            // Filed under this client's role, so the ledger tells a framework actor's spend from an agent's.
            await dustLimitService.ChargeAsync(chargedConversationId, dust, llmRole);
        }
        catch
        {
            // Accounting never breaks a turn: the ledger already fails open and anything it still lets through stops here.
        }
    }
}