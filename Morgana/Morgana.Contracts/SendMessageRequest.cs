namespace Morgana.Contracts;

/// <summary>HTTP body of <c>POST /api/morgana/conversation/{id}/message</c>, where the route names the conversation and the body carries only what is said to it.</summary>
/// <param name="Text">What the user said.</param>
public record SendMessageRequest(string Text);