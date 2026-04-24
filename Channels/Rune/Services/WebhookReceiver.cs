using Rune.Messages.Contracts;

namespace Rune.Services;

/// <summary>
/// Thin dispatcher invoked by the minimal-API <c>POST /morgana-hook</c> endpoint.
/// Decouples the HTTP surface from the console UI: <see cref="OnMessage"/> is wired
/// in <c>Program.cs</c> after both the receiver and the UI have been resolved from DI,
/// avoiding a constructor-level circular dependency.
/// </summary>
public sealed class WebhookReceiver
{
    /// <summary>Callback invoked for every inbound message; wired in <c>Program.cs</c> to <see cref="ConsoleUi.EnqueueIncoming"/>.</summary>
    public Action<ChannelMessage>? OnMessage { get; set; }

    /// <summary>Forwards the deserialized webhook payload to <see cref="OnMessage"/>; a no-op if no handler is attached yet.</summary>
    public void Dispatch(ChannelMessage message) => OnMessage?.Invoke(message);
}
