using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that meters token consumption ("magic dust") for
/// every LLM call passing through it and charges the conversation's lifetime budget.
/// </summary>
/// <remarks>
/// <para><strong>Why an instance carries its own llmRole.</strong> There is no singleton dust
/// wrapper on <see cref="MorganaLLM"/>'s chat client. Instead this client is constructed at
/// exactly two points, each stamping its own <c>llmRole</c> at construction time — so we never
/// need a separate llmRole-stamping decorator, and the same call is never charged twice:</para>
/// <list type="bullet">
///   <item><see cref="MorganaLLM.CompleteWithSystemPromptAsync"/> wraps per call with llmRole
///   <c>"Morgana"</c> (framework actors: guard, classifier, presenter, channel adapter).</item>
///   <item><see cref="Adapters.MorganaAgentAdapter"/> wraps per agent with llmRole
///   <c>"Morgana (Intent)"</c> (domain agents and their history reducer).</item>
/// </list>
/// <para>The conversation id is read from <see cref="ChatOptions.ConversationId"/>, which
/// every caller already sets. Charging is best-effort: <see cref="IDustLimitService"/> itself
/// fails open, and any exception here is swallowed so dust accounting can never break a turn.</para>
/// </remarks>
public sealed class DustAccountingChatClient : DelegatingChatClient
{
    private readonly IDustLimitService dustLimitService;
    private readonly Records.MagicDustPricing dustPricing;
    private readonly string llmRole;
    private readonly string? conversationId;

    /// <summary>
    /// Wraps <paramref name="innerClient"/>, metering every call against
    /// <paramref name="dustLimitService"/> using <paramref name="dustPricing"/> and attributing
    /// the cost to <paramref name="llmRole"/>.
    /// </summary>
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
        ChatResponse chatResponse = await base.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
        await ChargeAsync(ResolveConversationId(chatOptions), chatResponse.Usage);
        return chatResponse;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Providers deliver streaming usage as a cumulative total (typically one final
        // UsageContent), not per-chunk deltas — so we keep the LAST one seen and charge once
        // when the stream completes. Summing would double-count.
        UsageDetails? usageDetails = null;

        await foreach (ChatResponseUpdate chatResponseUpdate in
            base.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken))
        {
            foreach (UsageContent usageContent in chatResponseUpdate.Contents.OfType<UsageContent>())
                usageDetails = usageContent.Details;

            yield return chatResponseUpdate;
        }

        await ChargeAsync(ResolveConversationId(chatOptions), usageDetails);
    }

    /// <summary>
    /// Prefers the conversation id carried on the call's <see cref="ChatOptions"/> (the
    /// framework-actor path sets it); falls back to the id baked in at construction (the
    /// agent path, where Microsoft.Agents.AI does not flow a conversation id).
    /// </summary>
    private string? ResolveConversationId(ChatOptions? chatOptions) =>
        string.IsNullOrEmpty(chatOptions?.ConversationId) ? conversationId : chatOptions.ConversationId;

    /// <summary>
    /// Converts a usageDetails report into dust via the per-provider dustPricing and charges it.
    /// No-op when the conversation id or usageDetails is absent, or when the computed dust is zero
    /// (e.g. Ollama priced at 0 tokens-per-unit on both axes). Never throws.
    /// </summary>
    /// <remarks>
    /// The Anthropic MEAI adapter reports <see cref="UsageDetails.InputTokenCount"/> as the
    /// total prompt (fresh + cache-read + cache-write), with cache-read in
    /// <see cref="UsageDetails.CachedInputTokenCount"/> and cache-write in
    /// <c>AdditionalCounts["CacheCreationInputTokens"]</c>. We decompose it and apply the
    /// per-provider cache weights so the charge tracks real cache economics rather than
    /// over-counting cheap cache reads at full price.
    /// </remarks>
    private async Task ChargeAsync(string? convId, UsageDetails? usageDetails)
    {
        if (string.IsNullOrEmpty(convId) || usageDetails is null)
            return;

        long totalInput = usageDetails.InputTokenCount ?? 0;
        long cacheRead = usageDetails.CachedInputTokenCount ?? 0;

        long cacheWrite = 0;
        if (usageDetails.AdditionalCounts is not null &&
            usageDetails.AdditionalCounts.TryGetValue("CacheCreationInputTokens", out long w))
            cacheWrite = w;

        // Fresh = total minus the two cache components. Clamp at 0: defends against any
        // adapter that might report the components non-disjointly.
        long freshInput = Math.Max(0, totalInput - cacheRead - cacheWrite);

        double effectiveInput =
            freshInput +
            cacheRead * dustPricing.CachedInputWeight +
            cacheWrite * dustPricing.CacheCreationWeight;

        long outputTokens = usageDetails.OutputTokenCount ?? 0;

        double dust =
            (dustPricing.InputTokensPerDustUnit > 0
                ? effectiveInput / dustPricing.InputTokensPerDustUnit
                : 0.0) +
            (dustPricing.OutputTokensPerDustUnit > 0
                ? (double)outputTokens / dustPricing.OutputTokensPerDustUnit
                : 0.0);

        if (dust <= 0.0)
            return;

        try
        {
            await dustLimitService.ChargeAsync(convId, dust, llmRole);
        }
        catch
        {
            // Dust accounting must never break a turn. The service itself already fails open;
            // this is the last-resort belt-and-braces for anything it might surface.
        }
    }
}
