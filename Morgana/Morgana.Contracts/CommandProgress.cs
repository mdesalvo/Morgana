using System.Text.Json.Serialization;

namespace Morgana.Contracts;

/// <summary>
/// How far a running command has got, carried on the <see cref="ChannelMessage"/> that reports it. It is
/// the first widget of the command system: something a command draws on the channel rather than says in the
/// conversation, so a channel showing it replaces the frame before it instead of adding a line
/// and keeps none of it in the transcript. A channel that does not draw widgets still has the message's
/// own text, which says the same thing in one line.
/// </summary>
/// <param name="Command">The command the progress belongs to, without slash; successive frames of one command replace each other.</param>
/// <param name="Label">What is being worked on right now, short enough to sit on one row.</param>
/// <param name="Completed">Steps finished so far, between 0 and <paramref name="Total"/>.</param>
/// <param name="Total">Steps the command will take, counted before it starts: a command reporting at all is one that knows what it is about to do, which is what makes the bar a measure rather than a decoration.</param>
/// <param name="Finished">
/// True on the last frame, which takes the widget off the screen. Its message's text is the command's
/// outcome: arriving on a frame that names the run, an outcome can never be taken for another one's.
/// </param>
/// <param name="InvocationId">
/// The <see cref="ExecuteCommandRequest.InvocationId"/> of the run this frame belongs to, as the channel sent
/// it; null when the channel sent none. A command run on the channel's own side reports none either.
/// </param>
public record CommandProgress(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("finished")] bool Finished = false,
    [property: JsonPropertyName("invocationId")] string? InvocationId = null);
