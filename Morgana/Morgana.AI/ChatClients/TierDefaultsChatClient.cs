using Microsoft.Extensions.AI;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient filling tier-level ChatOptions defaults (TierConfiguration) on every call,
/// fill-if-absent per field (never overwrite). Mirrors Microsoft.Agents.AI pattern one layer down at tier client.
/// Reason: provider SDKs disagree on null field handling (the Anthropic adapter falls back to a 1024-token ceiling when none is given, hence MaxOutputTokens is mandatory per tier).
/// </summary>
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
    /// Merges tier defaults into caller's ChatOptions: fills ModelId and MaxOutputTokens from
    /// tierDefaultOptions where caller left them null, leaves all other fields unchanged.
    /// Never mutates requestOptions — callers may reuse across turns.
    /// </summary>
    private ChatOptions ResolveEffectiveOptions(ChatOptions? requestOptions)
    {
        // A caller that already chose both fields needs no default, so its options pass untouched.
        if (requestOptions is not null &&
            requestOptions.ModelId is not null &&
            requestOptions.MaxOutputTokens is not null)
            return requestOptions;

        // The tier's defaults fill a clone: the shared tier instance and the caller's reusable options stay unchanged.
        ChatOptions effectiveOptions = requestOptions?.Clone() ?? new ChatOptions();
        effectiveOptions.ModelId ??= tierDefaultOptions.ModelId;
        effectiveOptions.MaxOutputTokens ??= tierDefaultOptions.MaxOutputTokens;
        // The clone carries the caller's choices plus the tier's model and output ceiling where the caller gave none.
        return effectiveOptions;
    }
}