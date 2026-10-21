using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient closing an agent's turn the model answered in free text instead of calling Reply. Sits above the
/// tool loop, so it sees a whole turn: when that turn has text and no accepted Reply, it records the model's last words
/// through Reply itself.
/// </summary>
/// <remarks>
/// Free text never reaches the user, so the words are given to Reply as its text and the turn awaits nothing: the
/// buttons and the card that a Reply carries are the model's to choose and a turn it left open has chosen none.
/// </remarks>
public sealed class TurnClosingChatClient : DelegatingChatClient
{
    /// <summary>What an accepted Reply answers, which tells a closed turn from one whose Reply was refused.</summary>
    private readonly string turnClosedResult;

    /// <summary>Records each turn closed on the model's last words, a sign the prose is not doing its job.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Wraps the agent's tool loop.
    /// </summary>
    /// <param name="innerClient">The tool loop the agent runs on.</param>
    /// <param name="turnClosedResult">What an accepted Reply answers.</param>
    /// <param name="logger">Receives a line for every turn closed on the model's last words.</param>
    public TurnClosingChatClient(IChatClient innerClient, string turnClosedResult, ILogger logger) : base(innerClient)
    {
        this.turnClosedResult = turnClosedResult;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The tool loop below runs the turn whole.
        ChatResponse response = await base.GetResponseAsync(chatMessages, options, cancellationToken);

        // The turn has ended here, so it can be judged whole and closed when the model left it open.
        await CloseIfUnclosedAsync(options, response.Messages, cancellationToken);

        // The model's own response goes back as it is: the closure is recorded beside it and never replaces it.
        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The updates are gathered to rebuild the turn's messages once the stream has ended.
        List<ChatResponseUpdate> updates = [];

        // The updates pass through as they come; whether the turn is closed is known only once the stream has ended.
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            // Each update is kept, so the turn can be rebuilt whole once the stream has ended.
            updates.Add(update);
            yield return update;
        }

        // The stream has ended, so the turn can be judged whole and closed when the model left it open.
        await CloseIfUnclosedAsync(options, updates.ToChatResponse().Messages, cancellationToken);
    }

    /// <summary>
    /// Records the last words of a turn that has text and no accepted Reply, through Reply itself.
    /// </summary>
    private async Task CloseIfUnclosedAsync(
        ChatOptions? options,
        IList<ChatMessage> turnMessages,
        CancellationToken cancellationToken)
    {
        // A tool list without Reply is a call that is not an agent's turn, such as a summarization.
        List<AITool> agentTools = [.. options?.Tools ?? []];
        if (agentTools.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == Constants.Tools.Reply) is not AIFunction reply)
            return;

        // A turn waiting for the user's approval of a tool is closed by the agent, with the approval buttons.
        if (turnMessages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>().Any())
            return;

        // A Reply refused for its card left the turn open just as much as no Reply at all.
        bool closed = turnMessages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Any(result => result.Result is Records.FrameworkToolResult { Name: Constants.ToolInjections.TurnClosed }
                           || string.Equals(ResultText(result.Result), turnClosedResult, StringComparison.Ordinal));
        // A closed turn needs nothing more.
        if (closed)
            return;

        // The last assistant message with words is the model's final say of the turn: earlier ones are narration around its tool calls.
        string lastWords = turnMessages
            .Where(message => message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => message.Text.Trim())
            .LastOrDefault() ?? string.Empty;

        // A turn with no text is run again by the agent, which may close it then: nothing to close yet.
        if (lastWords.Length == 0)
            return;

        // Each closure on the model's last words is a sign that the prose leaves Reply unmentioned or unheeded.
        logger.LogWarning("Turn ended without Reply: closing it on the model's last words");

        try
        {
            // Recorded exactly as if the model had called Reply with these words, its refusals included.
            await reply.InvokeAsync(new AIFunctionArguments
            {
                [Constants.Tools.ReplyParameters.Text] = JsonSerializer.SerializeToElement(lastWords),
                [Constants.Tools.ReplyParameters.UserIsLeaving] = JsonSerializer.SerializeToElement(false)
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The turn stays unclosed and the agent runs it again.
            logger.LogError(ex, "Turn could not be closed on the model's last words");
        }
    }

    /// <summary>
    /// Reads a tool result as text: a tool's string comes back from the loop as a JSON string.
    /// </summary>
    private static string? ResultText(object? result) => result switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null
    };
}
