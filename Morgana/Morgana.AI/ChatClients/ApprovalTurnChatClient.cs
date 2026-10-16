using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Providers;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient sitting between the model and the tool loop. A model response that calls a tool
/// needing the user's approval closes the turn: it is let through only when it holds a Reply with text, whose
/// text is recorded as the turn's answer. It then loses its Reply call, since that turn is closed by the framework
/// with the approval buttons, never by the model.
/// </summary>
/// <remarks>
/// The tool loop holds back every call of a response that asks for approval and runs the ones needing
/// none at the next request. Left in place, the Reply written for this turn would therefore run at the
/// start of the next one and close that turn with this one's decision.
/// </remarks>
public sealed class ApprovalTurnChatClient : DelegatingChatClient
{
    /// <summary>Refusals of an approval request with no Reply text within one call, after which the request goes through as it is.</summary>
    private const int MaxTextlessRefusals = 2;

    /// <summary>Returns the agent's current session, the one whose context names the tool that the user pressed.</summary>
    private readonly Func<AgentSession?> sessionAccessor;

    /// <summary>The store that the pressed tool is read from.</summary>
    private readonly MorganaAIContextProvider contextProvider;

    /// <summary>What a call asking for approval with no Reply text beside it receives; null lets every request through.</summary>
    private readonly string? textMissingRefusal;

    /// <summary>
    /// Wraps the model the agent's tool loop calls.
    /// </summary>
    /// <param name="innerClient">The metered model client.</param>
    /// <param name="sessionAccessor">Returns the agent's current session.</param>
    /// <param name="contextProvider">The agent's context store.</param>
    /// <param name="textMissingRefusal">The result handed to the calls of an approval request made with no Reply text beside it.</param>
    public ApprovalTurnChatClient(
        IChatClient innerClient,
        Func<AgentSession?> sessionAccessor,
        MorganaAIContextProvider contextProvider,
        string? textMissingRefusal = null) : base(innerClient)
    {
        this.sessionAccessor = sessionAccessor;
        this.contextProvider = contextProvider;
        this.textMissingRefusal = textMissingRefusal;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The conversation is copied because a refused response is appended to it before the model is asked again.
        List<ChatMessage> messages = [.. chatMessages];
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);

        // An approval request needs its words in a Reply: with none the user would face a question about something
        // nobody described, so its calls are refused and the model asked again.
        for (int refusals = 0; refusals < MaxTextlessRefusals && IsTextlessApprovalRequest(response.Messages, options); refusals++)
        {
            // The refused attempt joins the conversation with its refusal, so the next attempt sees why its calls were turned down.
            messages = [.. messages, .. response.Messages, RefusalOf(response.Messages)];
            response = await base.GetResponseAsync(messages, options, cancellationToken);
        }

        // A response that asks for nothing keeps its Reply call: the model closes that turn itself.
        List<AIContent> responseContents = [.. response.Messages.SelectMany(message => message.Contents)];
        if (!AsksForApproval(responseContents, options))
            return response;

        // The Reply held beside the request gives the approval turn its words, recorded before the call is dropped.
        await RecordHeldReplyAsync(responseContents, options);

        // The turn is closed by the framework with the approval buttons, so the Reply written beside the request is dropped.
        foreach (ChatMessage message in response.Messages)
            message.Contents = [.. message.Contents.Where(content => !IsReplyCall(content))];

        // The response goes back with its Reply calls dropped, since the approval buttons close the turn.
        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The conversation is copied because a refused response is appended to it before the model is asked again.
        List<ChatMessage> messages = [.. chatMessages];
        List<ChatResponseUpdate> heldUpdates;
        List<ChatResponseUpdate> callUpdates;

        // Each attempt is repeated after a refusal, until a Reply with text arrives or the refusals are spent.
        for (int refusals = 0; ; refusals++)
        {
            // The updates held back for this attempt start empty, since a refused attempt must leave no trace.
            heldUpdates = [];
            callUpdates = [];
            bool textStarted = false;

            // Until the first word, what the model sends is held: a response refused for lacking a Reply
            // with text must leave no trace in the turn, its reasoning included.
            // The calls are held to the end of the response, where only then is it known whether one asks for approval.
            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                // Calls are held whole to the end of the response: whether one asks for approval is known only then.
                if (update.Contents.Any(content => content is FunctionCallContent))
                {
                    // A call is held until the response ends, so whether it needs approval is judged on the whole response.
                    callUpdates.Add(update);
                    continue;
                }

                // The first word releases what was held, so the agent sees the turn from its beginning.
                if (!textStarted && update.Contents.OfType<TextContent>().Any(text => !string.IsNullOrWhiteSpace(text.Text)))
                {
                    // The first word is recorded, so every later update passes as it comes.
                    textStarted = true;
                    foreach (ChatResponseUpdate heldUpdate in heldUpdates)
                        yield return heldUpdate;
                    heldUpdates.Clear();
                }

                // Once the turn has text every update passes as it comes.
                if (textStarted)
                    yield return update;
                else
                {
                    // An empty update in its place still tells the agent the model is working: the supervisor's
                    // wait counts silence and a long reasoning held back would otherwise read as a dead agent.
                    heldUpdates.Add(update);
                    yield return new ChatResponseUpdate();
                }
            }

            // Same rule as the non-streaming path: an approval request is let through only with a Reply that carries text.
            // Free text before it proves nothing, since the user never sees it.
            // The refusal is appended so the next attempt sees why its calls were turned down.
            List<ChatMessage> attemptMessages = [.. heldUpdates.Concat(callUpdates).ToChatResponse().Messages];
            if (refusals >= MaxTextlessRefusals || !IsTextlessApprovalRequest(attemptMessages, options))
                break;

            // The refused attempt joins the conversation with its refusal, so the next attempt sees why it was turned down.
            messages = [.. messages, .. attemptMessages, RefusalOf(attemptMessages)];
        }

        // Whatever was still held belongs to the response that goes through, so it passes now.
        foreach (ChatResponseUpdate heldUpdate in heldUpdates)
            yield return heldUpdate;

        // The calls are released with the Reply dropped when the response asks for approval, as in the non-streaming path.
        List<AIContent> callContents = [.. callUpdates.SelectMany(update => update.Contents)];
        bool asksForApproval = AsksForApproval(callContents, options);

        // The Reply held beside the request gives the approval turn its words, recorded before the call is dropped.
        if (asksForApproval)
            await RecordHeldReplyAsync(callContents, options);

        foreach (ChatResponseUpdate update in callUpdates)
        {
            // A response that asks for approval loses its Reply calls, since the approval buttons close the turn.
            if (asksForApproval)
                update.Contents = [.. update.Contents.Where(content => !IsReplyCall(content))];

            yield return update;
        }
    }

    /// <summary>
    /// True when a response asks for approval with no Reply text beside it.
    /// A response whose approval calls are all to the tool that the user pressed is not one: the press is the approval.
    /// </summary>
    private bool IsTextlessApprovalRequest(IList<ChatMessage> response, ChatOptions? options)
    {
        // Without a refusal text no request is turned down.
        if (textMissingRefusal is null)
            return false;

        // A response that asks for nothing needs no text before it.
        List<AIContent> responseContents = [.. response.SelectMany(message => message.Contents)];
        if (!AsksForApproval(responseContents, options))
            return false;

        // The turn that a button press opened holds the tool that the button leads to; any other turn holds none.
        AgentSession? session = sessionAccessor();
        string? pressedActionTool = session is null
            ? null
            : contextProvider.GetVariable(session, Constants.ContextKeys.PressedActionTool) as string;

        // The press already told the user what is about to run, so its tool needs no sentence before it.
        if (pressedActionTool is not null && ApprovalCallNames(responseContents, options).All(toolName => toolName == pressedActionTool))
            return false;

        // Any other approval request waits for a Reply with text beside it.
        return ReplyText(responseContents) is null;
    }

    /// <summary>
    /// Records the text of the Reply held beside an approval request as the turn's answer.
    /// </summary>
    private async Task RecordHeldReplyAsync(IReadOnlyCollection<AIContent> responseContents, ChatOptions? options)
    {
        // The press already told the user what is about to run: the tool runs at once and the model answers after its result.
        AgentSession? session = sessionAccessor();
        string? pressedActionTool = session is null
            ? null
            : contextProvider.GetVariable(session, Constants.ContextKeys.PressedActionTool) as string;
        if (session is null || (pressedActionTool is not null && ApprovalCallNames(responseContents, options).All(toolName => toolName == pressedActionTool)))
            return;

        // Without a Reply carrying text there are no words to record; the refusal above has already asked for them.
        if (ReplyText(responseContents) is not { } replyText)
            return;

        // Only the text matters here: the approval turn's buttons are the framework's.
        await contextProvider.SetVariableAsync(session, Constants.ContextKeys.ClosingDeclaration,
            JsonSerializer.Serialize(
                new Records.ClosingDeclaration(replyText, Records.AwaitedFromUser.Nothing, false, [], null),
                Records.DefaultJsonSerializerOptions));
    }

    /// <summary>The non-blank text that a Reply call of the response carries; null when there is none.</summary>
    private static string? ReplyText(IEnumerable<AIContent> contents)
        => contents
            .OfType<FunctionCallContent>()
            .Where(call => call.Name == Constants.Tools.Reply)
            .Select(call => ArgumentOf(call, "text") switch
            {
                // The text arrives as a string when the provider parsed the arguments already.
                string text => text,
                // The text arrives as a JSON element when the arguments were kept as parsed.
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            })
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    /// <summary>The names of the tools that the calls are to, counting only those needing the user's approval.</summary>
    private static IEnumerable<string> ApprovalCallNames(IEnumerable<AIContent> contents, ChatOptions? options)
    {
        // The agent's tools wrapped for approval are the ones whose calls need the user's consent.
        HashSet<string> approvalRequired =
            [.. (options?.Tools ?? []).OfType<ApprovalRequiredAIFunction>().Select(tool => tool.Name)];

        // Calls to any other tool are not part of an approval request.
        return contents.OfType<FunctionCallContent>().Select(call => call.Name).Where(approvalRequired.Contains);
    }

    /// <summary>The tool message answering every call of a refused response with the refusal.</summary>
    private ChatMessage RefusalOf(IEnumerable<ChatMessage> response)
        => new ChatMessage(ChatRole.Tool, [.. response
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Select(AIContent (call) => new FunctionResultContent(call.CallId, textMissingRefusal))]);

    /// <summary>
    /// True when one of the calls is to a tool that this agent runs only with the user's approval.
    /// </summary>
    private static bool AsksForApproval(IEnumerable<AIContent> contents, ChatOptions? options)
    {
        // The agent's tools wrapped for approval are the ones whose calls need the user's consent.
        HashSet<string> approvalRequired =
            [.. (options?.Tools ?? []).OfType<ApprovalRequiredAIFunction>().Select(tool => tool.Name)];

        // The response asks for approval when one of its calls is to a tool that needs the user's consent.
        return approvalRequired.Count > 0
            && contents.OfType<FunctionCallContent>().Any(call => approvalRequired.Contains(call.Name));
    }

    /// <summary>True for a call giving the turn's answer through Reply.</summary>
    private static bool IsReplyCall(AIContent content)
        => content is FunctionCallContent { Name: Constants.Tools.Reply };

    /// <summary>One argument of a call as the provider delivered it; null when the call does not carry it.</summary>
    private static object? ArgumentOf(FunctionCallContent call, string argumentName)
        => call.Arguments is not null && call.Arguments.TryGetValue(argumentName, out object? argument) ? argument : null;
}
