namespace Morgana.Contracts;

/// <summary>
/// Identity + addressing coordinates declared by a channel at the conversation-start
/// handshake: who the channel is (<see cref="ChannelName"/>) and how Morgana should route
/// outbound traffic to it (<see cref="DeliveryMode"/>). Non-capability string fields live
/// here so they can grow (e.g. callback URLs, routing keys) without perturbing the
/// capability budget.
/// </summary>
/// <remarks>
/// <para>Coordinates are "where/how to reach the channel", capabilities are "what the
/// channel can render" — the two concerns evolve on different axes, which is why they sit
/// in distinct records and are composed by <see cref="ChannelMetadata"/>.</para>
/// </remarks>
public record ChannelCoordinates
{
    /// <summary>Free-form label declared by the channel (e.g. <c>cauldron</c>,
    /// <c>twilio-sms</c>). Persisted alongside the budget and preserved across restarts.
    /// Normalised (trimmed + lowercased) at ingress so the name space stays case-insensitive end-to-end.
    /// Intended for observability, routing and per-channel policy; explicitly NOT a trust boundary.</summary>
    public required string ChannelName { get; init; }

    /// <summary>Transport dispatch key declared by the channel at the handshake (e.g.
    /// <c>signalr</c>, <c>webhook</c>). Stored free-form as a string and normalised
    /// (trimmed + lowercased) at ingress; the finite set of valid keys is owned by the
    /// channel service factory registry, not by this record, so new transports can be added
    /// without reshaping this contract. The start-conversation gate rejects missing or
    /// whitespace-only values; an unregistered key is rejected at dispatch.</summary>
    public required string DeliveryMode { get; init; }

    /// <summary>Absolute URL where Morgana POSTs outbound <see cref="ChannelMessage"/>s for
    /// push-style transports (e.g. <c>deliveryMode=webhook</c>). Null for pull/duplex
    /// transports (e.g. <c>signalr</c>) that do not need a callback target. The
    /// start-conversation gate enforces the presence and well-formedness of this field per
    /// <see cref="DeliveryMode"/>: unknown to the record itself, the requirement is owned by
    /// the transport-specific validation at the ingress.</summary>
    public string? CallbackUrl { get; init; }
}
