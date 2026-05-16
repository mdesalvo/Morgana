namespace Morgana.AI.Interfaces;

/// <summary>
/// Enforces the per-conversation lifetime dust budget — a token-consumption guard
/// orthogonal to <see cref="IRateLimitService"/>.
/// <para>
/// The rate limiter controls message <em>frequency</em>; the dust limiter controls token
/// <em>consumption</em>. A conversation is born with a fixed dust budget; every LLM call
/// burns some. The budget is a lifetime resource — no sliding window, no reset. When it is
/// exhausted the conversation is terminal: the next user turn is rejected with a narrative
/// error and the only way forward is a brand-new conversation.
/// </para>
/// <para>All methods fail open: a storage fault must never block the user.</para>
/// </summary>
public interface IDustLimitService
{
    /// <summary>
    /// Records dust consumed by a single LLM call and appends an audit-log row.
    /// </summary>
    /// <param name="conversationId">Conversation whose budget is charged.</param>
    /// <param name="dust">Dust units to add (computed by the caller from token counts and
    /// per-provider pricing). Calls with <paramref name="dust"/> &lt;= 0 are no-ops.</param>
    /// <param name="llmRole">Who burned it — <c>"Morgana"</c> for framework actors,
    /// <c>"Morgana (Intent)"</c> for domain agents. Diagnostic only, not used for enforcement.</param>
    Task ChargeAsync(string conversationId, double dust, string llmRole);

    /// <summary>
    /// True when the conversation has consumed its full budget and the next turn must be
    /// blocked. False (fail open) on storage errors or when dust limiting is disabled.
    /// </summary>
    Task<bool> IsOverBudgetAsync(string conversationId);

    /// <summary>
    /// Ratio of consumed dust to the configured budget (0.0 to &gt;1.0). 0.0 when the
    /// conversation has no usage yet, when dust limiting is disabled, or on error.
    /// </summary>
    Task<double> GetUsageRatioAsync(string conversationId);

    /// <summary>
    /// Atomically checks the 80% and 90% thresholds against their one-shot flags, marking
    /// any newly-crossed threshold so it never re-triggers. Returns which warnings the caller
    /// should emit. (false, false) on error or when disabled.
    /// </summary>
    Task<(bool Send80, bool Send90)> CheckAndMarkWarningsAsync(string conversationId);
}
