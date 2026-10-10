using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Morgana.AI.Tools;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient sitting between the model and the tool loop. A model response that calls a tool
/// needing the user's approval closes the turn: it is let through only once the turn has text and it loses
/// its Reply call, since that turn is closed by the framework with the approval buttons, never by the model.
/// </summary>
/// <remarks>
/// The tool loop holds back every call of a response that asks for approval and runs the ones needing
/// none at the next request. Left in place, the Reply written for this turn would therefore run at the
/// start of the next one and close that turn with this one's decision.
/// </remarks>
public sealed class ApprovalTurnChatClient : DelegatingChatClient
{
    /// <summary>Refusals of a text-less approval request within one call, after which the request goes through as it is.</summary>
    private const int MaxTextlessRefusals = 2;

    /// <summary>What a call asking for approval in a turn with no text receives; null lets every request through.</summary>
    private readonly string? textMissingRefusal;

    /// <summary>
    /// Wraps the model the agent's tool loop calls.
    /// </summary>
    /// <param name="innerClient">The metered model client.</param>
    /// <param name="textMissingRefusal">The result handed to the calls of an approval request made before the turn has text.</param>
    public ApprovalTurnChatClient(IChatClient innerClient, string? textMissingRefusal = null) : base(innerClient)
        => this.textMissingRefusal = textMissingRefusal;

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The conversation is copied because a refused response is appended to it before the model is asked again.
        List<ChatMessage> messages = [.. chatMessages];
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);

        // Nothing closes a turn before its words: an approval request with no text would put the user before a
        // question about something nobody described, so its calls are refused and the model asked again.
        for (int refusals = 0; refusals < MaxTextlessRefusals && IsTextlessApprovalRequest(messages, response.Messages, options); refusals++)
        {
            // The refused attempt joins the conversation with its refusal, so the next attempt sees why its calls were turned down.
            messages = [.. messages, .. response.Messages, RefusalOf(response.Messages)];
            response = await base.GetResponseAsync(messages, options, cancellationToken);
        }

        // A response that asks for nothing keeps its Reply call: the model closes that turn itself.
        if (!AsksForApproval(response.Messages.SelectMany(message => message.Contents), options))
            return response;

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

        // Each attempt is repeated after a textless refusal, until text arrives or the refusals are spent.
        for (int refusals = 0; ; refusals++)
        {
            // The updates held back for this attempt start empty, since a refused attempt must leave no trace.
            heldUpdates = [];
            callUpdates = [];
            bool textStarted = false;

            // Text reaches the user as it is written. Until the first word, what the model sends is held: a
            // response refused for having no text must leave no trace in the turn, its reasoning included.
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

                // The first word releases what was held, so the user sees the turn from its beginning.
                if (!textStarted && update.Contents.OfType<TextContent>().Any(text => !string.IsNullOrWhiteSpace(text.Text)))
                {
                    // The first word is recorded, so every later update reaches the user as it comes.
                    textStarted = true;
                    foreach (ChatResponseUpdate heldUpdate in heldUpdates)
                        yield return heldUpdate;
                    heldUpdates.Clear();
                }

                // Once the turn has text every update reaches the user as it comes.
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

            // Same rule as the non-streaming path: an approval request is let through only once the turn has text.
            // The refusal is appended so the next attempt sees why its calls were turned down.
            List<ChatMessage> attemptMessages = [.. heldUpdates.Concat(callUpdates).ToChatResponse().Messages];
            if (textStarted || refusals >= MaxTextlessRefusals || !IsTextlessApprovalRequest(messages, attemptMessages, options))
                break;

            // The refused attempt joins the conversation with its refusal, so the next attempt sees why it was turned down.
            messages = [.. messages, .. attemptMessages, RefusalOf(attemptMessages)];
        }

        // Whatever was still held belongs to the response that goes through, so it reaches the user now.
        foreach (ChatResponseUpdate heldUpdate in heldUpdates)
            yield return heldUpdate;

        // The calls are released with the Reply dropped when the response asks for approval, as in the non-streaming path.
        bool asksForApproval = AsksForApproval(callUpdates.SelectMany(update => update.Contents), options);
        foreach (ChatResponseUpdate update in callUpdates)
        {
            // A response that asks for approval loses its Reply calls, since the approval buttons close the turn.
            if (asksForApproval)
                update.Contents = [.. update.Contents.Where(content => !IsReplyCall(content))];

            yield return update;
        }
    }

    /// <summary>
    /// True when a response asks for approval in a turn that has no text yet, counting the response's own.
    /// </summary>
    private bool IsTextlessApprovalRequest(List<ChatMessage> messages, IList<ChatMessage> response, ChatOptions? options)
        => textMissingRefusal is not null
            && AsksForApproval(response.SelectMany(message => message.Contents), options)
            && !ReplyTool.HasTurnText([.. messages, .. response]);

    /// <summary>The tool message answering every call of a refused response with the refusal.</summary>
    private ChatMessage RefusalOf(IEnumerable<ChatMessage> response)
        => new ChatMessage(ChatRole.Tool, [.. response
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Select(call => (AIContent)new FunctionResultContent(call.CallId, textMissingRefusal))]);

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

    /// <summary>True for a call closing the turn through Reply.</summary>
    private static bool IsReplyCall(AIContent content)
        => content is FunctionCallContent { Name: Constants.Tools.Reply };
}
