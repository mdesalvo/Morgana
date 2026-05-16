using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that meters token consumption ("magic dust") for
/// every LLM call passing through it and charges the conversation's lifetime budget.
/// </summary>
/// <remarks>
/// <para><strong>Why an instance carries its own role.</strong> There is no singleton dust
/// wrapper on <see cref="MorganaLLM"/>'s chat client. Instead this client is constructed at
/// exactly two points, each stamping its own <c>role</c> at construction time — so we never
/// need a separate role-stamping decorator, and the same call is never charged twice:</para>
/// <list type="bullet">
///   <item><see cref="MorganaLLM.CompleteWithSystemPromptAsync"/> wraps per call with role
///   <c>"Morgana"</c> (framework actors: guard, classifier, presenter, channel adapter).</item>
///   <item><see cref="Adapters.MorganaAgentAdapter"/> wraps per agent with role
///   <c>"Morgana (Intent)"</c> (domain agents and their history reducer).</item>
/// </list>
/// <para>The conversation id is read from <see cref="ChatOptions.ConversationId"/>, which
/// every caller already sets. Charging is best-effort: <see cref="IDustLimitService"/> itself
/// fails open, and any exception here is swallowed so dust accounting can never break a turn.</para>
/// </remarks>
public sealed class DustAccountingChatClient : DelegatingChatClient
{
    private readonly IDustLimitService dustLimitService;
    private readonly Records.MagicDustPricing pricing;
    private readonly string role;

    /// <summary>
    /// Wraps <paramref name="innerClient"/>, metering every call against
    /// <paramref name="dustLimitService"/> using <paramref name="pricing"/> and attributing
    /// the cost to <paramref name="role"/>.
    /// </summary>
    public DustAccountingChatClient(
        IChatClient innerClient,
        IDustLimitService dustLimitService,
        Records.MagicDustPricing pricing,
        string role) : base(innerClient)
    {
        this.dustLimitService = dustLimitService;
        this.pricing = pricing;
        this.role = role;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        await ChargeAsync(options?.ConversationId, response.Usage);
        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Providers deliver streaming usage as a cumulative total (typically one final
        // UsageContent), not per-chunk deltas — so we keep the LAST one seen and charge once
        // when the stream completes. Summing would double-count.
        UsageDetails? usage = null;

        await foreach (ChatResponseUpdate update in
            base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (UsageContent usageContent in update.Contents.OfType<UsageContent>())
                usage = usageContent.Details;

            yield return update;
        }

        await ChargeAsync(options?.ConversationId, usage);
    }

    /// <summary>
    /// Converts a usage report into dust via the per-provider pricing and charges it.
    /// No-op when the conversation id or usage is absent, or when the computed dust is zero
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
    private async Task ChargeAsync(string? conversationId, UsageDetails? usage)
    {
        if (string.IsNullOrEmpty(conversationId) || usage is null)
            return;

        long totalInput = usage.InputTokenCount ?? 0;
        long cacheRead = usage.CachedInputTokenCount ?? 0;

        long cacheWrite = 0;
        if (usage.AdditionalCounts is not null &&
            usage.AdditionalCounts.TryGetValue("CacheCreationInputTokens", out long w))
            cacheWrite = w;

        // Fresh = total minus the two cache components. Clamp at 0: defends against any
        // adapter that might report the components non-disjointly.
        long freshInput = Math.Max(0, totalInput - cacheRead - cacheWrite);

        double effectiveInput =
            freshInput +
            cacheRead * pricing.CachedInputWeight +
            cacheWrite * pricing.CacheCreationWeight;

        long outputTokens = usage.OutputTokenCount ?? 0;

        double dust =
            (pricing.InputTokensPerDustUnit > 0
                ? effectiveInput / pricing.InputTokensPerDustUnit
                : 0.0) +
            (pricing.OutputTokensPerDustUnit > 0
                ? (double)outputTokens / pricing.OutputTokensPerDustUnit
                : 0.0);

        if (dust <= 0.0)
            return;

        try
        {
            await dustLimitService.ChargeAsync(conversationId, dust, role);
        }
        catch
        {
            // Dust accounting must never break a turn. The service itself already fails open;
            // this is the last-resort belt-and-braces for anything it might surface.
        }
    }
}
