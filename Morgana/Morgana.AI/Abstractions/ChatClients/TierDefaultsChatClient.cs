using Microsoft.Extensions.AI;

namespace Morgana.AI.Abstractions;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that fills in a fixed set of tier-level
/// <see cref="ChatOptions"/> defaults (<see cref="Records.TierConfiguration"/>) on every call
/// whose caller left the corresponding field unset — never overriding a value already present.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists:</strong> none of the four LLM provider SDKs Morgana wraps
/// (Anthropic, Azure OpenAI, OpenAI, Ollama) agree on what happens when a given
/// <see cref="ChatOptions"/> field is left null. The Anthropic .NET SDK, for instance, silently
/// substitutes a hardcoded 1024-token <see cref="ChatOptions.MaxOutputTokens"/> ceiling
/// client-side — observed truncating agents (any tier, not just the expensive ones) mid-response
/// while composing a tool-driven rich card, with the truncated turn then misread downstream as a
/// normally completed one. This decorator makes the handful of fields Morgana chooses to default
/// (see <see cref="Records.TierConfiguration"/> for the full census and rationale) an explicit,
/// provider-agnostic choice instead of an accident of which SDK happens to be active.</para>
///
/// <para><strong>Merge semantics — fill-if-absent, per field, never a blind overwrite:</strong>
/// this mirrors exactly the pattern <c>Microsoft.Agents.AI.ChatClientAgent.CreateConfiguredChatOptions</c>
/// already uses one layer up (agent-level <c>ChatClientAgentOptions.ChatOptions</c> defaults
/// filling gaps in the per-run <see cref="ChatOptions"/>) — not a novel invention, the same
/// pattern applied one layer further down, at the tier client itself. A caller's explicit value
/// is always left untouched; only null fields are filled from the tier default.</para>
/// </remarks>
public sealed class TierDefaultsChatClient : DelegatingChatClient
{
    /// <summary>
    /// The tier-level defaults this client was constructed with (<see cref="Records.TierConfiguration.ToChatOptions"/>) —
    /// a single shared instance, reused for every call this client ever serves, across every
    /// conversation. Never mutated after construction; see <see cref="ResolveEffectiveOptions"/>
    /// remarks for why that matters.
    /// </summary>
    private readonly ChatOptions tierDefaultOptions;

    /// <summary>
    /// Wraps <paramref name="innerClient"/>, applying <paramref name="tierDefaultOptions"/>
    /// field-by-field to any call whose corresponding <see cref="ChatOptions"/> field is left unset.
    /// </summary>
    public TierDefaultsChatClient(IChatClient innerClient, ChatOptions tierDefaultOptions) : base(innerClient)
    {
        this.tierDefaultOptions = tierDefaultOptions;
    }

    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? requestOptions = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(chatMessages, ResolveEffectiveOptions(requestOptions), cancellationToken);

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? requestOptions = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(chatMessages, ResolveEffectiveOptions(requestOptions), cancellationToken);

    /// <summary>
    /// Returns the <see cref="ChatOptions"/> that will actually be sent for this call: the
    /// caller's own <paramref name="requestOptions"/> (agent turn or framework-actor call) with
    /// <see cref="Records.TierConfiguration"/>'s two fields (<c>ModelId</c>,
    /// <c>MaxOutputTokens</c>) filled in from <see cref="tierDefaultOptions"/> wherever the
    /// caller left them null — every other field on the returned instance is exactly what the
    /// caller passed in. <paramref name="requestOptions"/> itself is never mutated — callers
    /// upstream (Microsoft.Agents.AI in particular) may still hold and reuse a reference to it
    /// across turns. Reasoning is deliberately not part of this merge — see
    /// <see cref="Records.TierConfiguration"/> remarks on why it is not a JSON-configurable tier
    /// default: each provider connector owns its own fixed reasoning behavior per die instead.
    /// </summary>
    private ChatOptions ResolveEffectiveOptions(ChatOptions? requestOptions)
    {
        if (requestOptions is not null &&
            requestOptions.ModelId is not null &&
            requestOptions.MaxOutputTokens is not null)
            return requestOptions;

        ChatOptions effectiveOptions = requestOptions?.Clone() ?? new ChatOptions();
        effectiveOptions.ModelId ??= tierDefaultOptions.ModelId;
        effectiveOptions.MaxOutputTokens ??= tierDefaultOptions.MaxOutputTokens;
        return effectiveOptions;
    }
}