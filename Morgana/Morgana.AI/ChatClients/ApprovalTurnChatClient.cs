using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient sitting between the model and the tool loop. From a model response that calls a
/// tool needing the user's approval it removes the Reply call: that turn is closed by the framework,
/// with the approval buttons, never by the model.
/// </summary>
/// <remarks>
/// The tool loop holds back every call of a response that asks for approval and runs the ones needing
/// none at the next request. Left in place, the Reply written for this turn would therefore run at the
/// start of the next one and close that turn with this one's decision.
/// </remarks>
public sealed class ApprovalTurnChatClient : DelegatingChatClient
{
    /// <summary>
    /// Wraps the model the agent's tool loop calls.
    /// </summary>
    /// <param name="innerClient">The metered model client.</param>
    public ApprovalTurnChatClient(IChatClient innerClient) : base(innerClient) { }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(chatMessages, options, cancellationToken);
        if (!AsksForApproval(response.Messages.SelectMany(message => message.Contents), options))
            return response;

        foreach (ChatMessage message in response.Messages)
            message.Contents = [.. message.Contents.Where(content => !IsReplyCall(content))];

        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> callUpdates = [];

        // Text reaches the user as it is written. The calls are held to the end of the response, where
        // the tool loop reads them anyway: only then is it known whether one of them asks for approval.
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            if (update.Contents.Any(content => content is FunctionCallContent))
                callUpdates.Add(update);
            else
                yield return update;
        }

        bool asksForApproval = AsksForApproval(callUpdates.SelectMany(update => update.Contents), options);
        foreach (ChatResponseUpdate update in callUpdates)
        {
            if (asksForApproval)
                update.Contents = [.. update.Contents.Where(content => !IsReplyCall(content))];

            yield return update;
        }
    }

    /// <summary>
    /// True when one of the calls is to a tool that this agent runs only with the user's approval.
    /// </summary>
    private static bool AsksForApproval(IEnumerable<AIContent> contents, ChatOptions? options)
    {
        HashSet<string> approvalRequired =
            [.. (options?.Tools ?? []).OfType<ApprovalRequiredAIFunction>().Select(tool => tool.Name)];

        return approvalRequired.Count > 0
            && contents.OfType<FunctionCallContent>().Any(call => approvalRequired.Contains(call.Name));
    }

    /// <summary>True for a call closing the turn through Reply.</summary>
    private static bool IsReplyCall(AIContent content)
        => content is FunctionCallContent { Name: Constants.Tools.Reply };
}
