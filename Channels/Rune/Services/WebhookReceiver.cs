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
    public Action<ChannelMessage>? OnMessage { get; set; }

    public void Dispatch(ChannelMessage message) => OnMessage?.Invoke(message);
}
