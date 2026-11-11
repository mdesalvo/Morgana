namespace Morgana.Contracts;

/// <summary>Identity and addressing coordinates that a channel declares at the conversation-start handshake.</summary>
// The non-capability string fields live here so that they can grow (callback URLs and routing keys for instance) without perturbing the capability budget.
public record ChannelCoordinates
{
    /// <summary>Free-form label that the channel declares for itself, such as <c>cauldron</c> or <c>twilio-sms</c>.</summary>
    // Persisted alongside the budget and preserved across restarts. Normalised (trimmed and lowercased) at ingress
    // so the name space stays case-insensitive end to end. It serves observability, routing and per-channel policy
    // and is explicitly not a trust boundary.
    public required string ChannelName { get; init; }

    /// <summary>Transport dispatch key that the channel declares at the handshake, such as <c>signalr</c> or <c>webhook</c>.</summary>
    // Stored free-form and normalised (trimmed and lowercased) at ingress. The finite set of valid keys is owned by the
    // channel service factory registry and not by this record, so new transports can be added without reshaping the contract.
    // The start-conversation gate rejects missing or whitespace-only values and an unregistered key is rejected at dispatch.
    public required string DeliveryMode { get; init; }

    /// <summary>Absolute URL where Morgana POSTs outbound <see cref="ChannelMessage"/>s for push-style transports such as <c>webhook</c>.</summary>
    // Null for pull or duplex transports such as signalr that need no callback target. The record itself cannot tell whether
    // the URL is required: the transport-specific validation at the start-conversation gate enforces its presence
    // and well-formedness per DeliveryMode.
    public string? CallbackUrl { get; init; }
}
