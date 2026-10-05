using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient closing an agent's turn the model wrote without calling Reply. Sits above the
/// tool loop, so it sees a whole turn: when that turn has text and no accepted Reply, it asks the model
/// for the closure alone and records it through Reply itself.
/// </summary>
/// <remarks>
/// A provider that can be told which tool to call is made to call Reply; one that cannot is asked for
/// the same closure as structured output. Either answer reaches Reply as its arguments, so the card is
/// held to the same contract whichever path produced it.
/// </remarks>
public sealed class TurnClosingChatClient : DelegatingChatClient
{
    /// <summary>
    /// What the model reads when its turn is closed on its behalf, from morgana.json. Null when the
    /// deployment declares none, which leaves such turns unclosed.
    /// </summary>
    private readonly string? turnClosureRequest;

    /// <summary>What an accepted Reply answers, which tells a closed turn from one whose Reply was refused.</summary>
    private readonly string turnClosedResult;

    /// <summary>Whether the provider honours a request naming the tool that the model must call.</summary>
    private readonly bool canForceToolCall;

    /// <summary>Records each turn closed on the model's behalf, a sign the prose is not doing its job.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Wraps the agent's tool loop.
    /// </summary>
    /// <param name="innerClient">The tool loop the agent runs on.</param>
    /// <param name="turnClosureRequest">The request for a turn's closure; null closes nothing.</param>
    /// <param name="turnClosedResult">What an accepted Reply answers.</param>
    /// <param name="canForceToolCall">Whether the provider can be made to call Reply.</param>
    /// <param name="logger">Receives a line for every turn closed on the model's behalf.</param>
    public TurnClosingChatClient(IChatClient innerClient, string? turnClosureRequest, string turnClosedResult, bool canForceToolCall, ILogger logger) : base(innerClient)
    {
        this.turnClosureRequest = turnClosureRequest;
        this.turnClosedResult = turnClosedResult;
        this.canForceToolCall = canForceToolCall;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> turnInput = [.. chatMessages];
        ChatResponse response = await base.GetResponseAsync(turnInput, options, cancellationToken);
        await CloseIfUnclosedAsync(turnInput, options, response.Messages, cancellationToken);
        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> turnInput = [.. chatMessages];
        List<ChatResponseUpdate> updates = [];

        // The text keeps streaming to the user as it is written; the closure is a question about the
        // turn as a whole and can only be asked once the stream has ended.
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(turnInput, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        await CloseIfUnclosedAsync(turnInput, options, updates.ToChatResponse().Messages, cancellationToken);
    }

    /// <summary>
    /// Asks for the closure of a turn that has text and no accepted Reply, then records it through Reply.
    /// </summary>
    private async Task CloseIfUnclosedAsync(
        List<ChatMessage> turnInput,
        ChatOptions? options,
        IList<ChatMessage> turnMessages,
        CancellationToken cancellationToken)
    {
        if (turnClosureRequest is null)
            return;

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
            .Any(result => result.Result is Records.FrameworkToolResult { Name: Constants.ToolResults.TurnClosed }
                           || string.Equals(ResultText(result.Result), turnClosedResult, StringComparison.Ordinal));
        if (closed)
            return;

        // A turn with no text is run again by the agent, which may close it then: nothing to close yet.
        string turnText = string.Concat(turnMessages.Where(message => message.Role == ChatRole.Assistant).Select(message => message.Text)).Trim();
        if (turnText.Length == 0)
            return;

        logger.LogWarning("Turn ended without Reply: closing it on the model's behalf");

        // The exchange being closed and nothing else: earlier turns are already closed and the tool
        // traffic of this one is folded into the text that it produced.
        List<ChatMessage> closingMessages =
        [
            new ChatMessage(ChatRole.User, turnInput.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty),
            new ChatMessage(ChatRole.Assistant, turnText),
            new ChatMessage(ChatRole.User, turnClosureRequest)
        ];

        try
        {
            IDictionary<string, object?>? arguments = canForceToolCall
                ? await RequestForcedClosureAsync(agentTools, options?.Instructions, closingMessages, cancellationToken)
                : await RequestStructuredClosureAsync(options?.Instructions, closingMessages, cancellationToken);

            // Recorded exactly as if the model had called Reply, its refusals included.
            if (arguments is not null)
                await reply.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The turn's text has reached the user already: an unclosed turn is answered and ends.
            logger.LogError(ex, "Turn could not be closed on the model's behalf");
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

    /// <summary>
    /// Makes the model call Reply, with every tool of the agent declared so its actions can name them.
    /// </summary>
    private async Task<IDictionary<string, object?>?> RequestForcedClosureAsync(
        List<AITool> agentTools,
        string? instructions,
        List<ChatMessage> closingMessages,
        CancellationToken cancellationToken)
    {
        // Declared and never run: the tool loop hands the call back instead of executing it.
        ChatResponse response = await InnerClient.GetResponseAsync(closingMessages, new ChatOptions
        {
            Instructions = instructions,
            Tools = [.. agentTools.Select(tool => tool is AIFunction function ? function.AsDeclarationOnly() : tool)],
            ToolMode = ChatToolMode.RequireSpecific(Constants.Tools.Reply)
        }, cancellationToken);

        return response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(call => call.Name == Constants.Tools.Reply)?
            .Arguments;
    }

    /// <summary>
    /// Asks the model for the closure as a JSON document shaped like Reply's arguments.
    /// </summary>
    private async Task<IDictionary<string, object?>?> RequestStructuredClosureAsync(
        string? instructions,
        List<ChatMessage> closingMessages,
        CancellationToken cancellationToken)
    {
        ChatResponse<Records.TurnReply> response = await InnerClient.GetResponseAsync<Records.TurnReply>(
            closingMessages,
            Records.DefaultJsonSerializerOptions,
            new ChatOptions { Instructions = instructions },
            useJsonSchemaResponseFormat: true,
            cancellationToken);

        if (!response.TryGetResult(out Records.TurnReply? turnReply))
            return null;

        // Handed to Reply as the arguments that the model would have passed it, property by property.
        return JsonSerializer.SerializeToElement(turnReply, Records.DefaultJsonSerializerOptions)
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
    }
}
