using System.Text.Json.Serialization;

namespace Morgana.Contracts;

/// <summary>
/// How far a running command has got, carried on the <see cref="ChannelMessage"/> that reports it.
/// A widget rather than speech: a channel showing it replaces the frame before it instead of adding a line and keeps none of it in the transcript.
/// A channel that does not draw widgets still has the message's own text, which says the same thing in one line.
/// Total is counted before the command starts because a command reporting at all knows what it is about to do and that makes the bar a measure.
/// On the Finished frame the message text is the command's outcome. The frame names the run so that outcome is never taken for another one's.
/// A command run on the channel's own side reports no InvocationId.
/// </summary>
/// <param name="Command">The command the progress belongs to, without slash; successive frames of one command replace each other.</param>
/// <param name="Label">What is being worked on right now, short enough to sit on one row.</param>
/// <param name="Completed">Steps finished so far, between 0 and <paramref name="Total"/>.</param>
/// <param name="Total">Steps the command will take, counted before it starts.</param>
/// <param name="Finished">True on the last frame, which takes the widget off the screen.</param>
/// <param name="InvocationId">The <see cref="ExecuteCommandRequest.InvocationId"/> of the run this frame belongs to, as the channel sent it; null when the channel sent none.</param>
public record CommandProgress(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("finished")] bool Finished = false,
    [property: JsonPropertyName("invocationId")] string? InvocationId = null);