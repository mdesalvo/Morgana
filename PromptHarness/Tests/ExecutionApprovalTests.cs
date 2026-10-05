using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.ChatClients;
using Morgana.AI.Providers;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting how a tool declared <c>RequiresExecutionApproval</c> waits for the user: it never
/// runs on the model's call, it runs once with the arguments that were approved and a turn asking for
/// approval is never closed by a Reply that would outlive it.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: a scripted client stands where the model would. The approval itself is
/// Microsoft.Extensions.AI's and Microsoft.Agents.AI's; what is asserted is that Morgana's wiring leaves
/// it intact across the turn boundary, where a session is saved and read back.</para>
/// </remarks>
public sealed class ExecutionApprovalTests
{
    [Fact]
    public async Task Tool_requiring_approval_reaches_the_model_wrapped_for_it()
    {
        MorganaToolAdapter adapter = new MorganaToolAdapter();
        Func<string, string> confirmOrder = orderId => $"order {orderId} confirmed";
        Records.ToolDefinition definition = new Records.ToolDefinition("ConfirmOrder", "Confirms an order.",
            [new Records.ToolParameter("orderId", "The order.", true, "request")], RequiresExecutionApproval: true);
        adapter.AddTool(definition.Name, confirmOrder, definition);

        Assert.IsType<ApprovalRequiredAIFunction>(await adapter.CreateFunctionAsync(definition.Name));
    }

    [Fact]
    public async Task Approved_call_runs_once_with_the_approved_arguments_across_a_saved_session()
    {
        ApprovalUnderTest turn = new ApprovalUnderTest();

        // First turn: the model writes, asks for the confirmation and closes. Nothing runs.
        List<ToolApprovalRequestContent> requests = await turn.RunFirstTurnAsync();
        Assert.Equal("ConfirmOrder", ((FunctionCallContent)Assert.Single(requests).ToolCall).Name);
        Assert.Empty(turn.Confirmed);

        // Second turn, on the session as it was saved: the user's approval travels with their words.
        await turn.AnswerAsync("Go ahead", [.. requests.Select(request => (AIContent)request.CreateResponse(true))]);

        Assert.Equal(["ORD-1"], turn.Confirmed);
    }

    [Fact]
    public async Task Declined_call_never_runs()
    {
        ApprovalUnderTest turn = new ApprovalUnderTest();

        List<ToolApprovalRequestContent> requests = await turn.RunFirstTurnAsync();
        await turn.AnswerAsync("Actually, what else do you sell?", [.. requests.Select(request => (AIContent)request.CreateResponse(false))]);

        Assert.Empty(turn.Confirmed);
    }

    [Fact]
    public async Task Reply_written_beside_a_call_awaiting_approval_never_runs()
    {
        ApprovalUnderTest turn = new ApprovalUnderTest();

        List<ToolApprovalRequestContent> requests = await turn.RunFirstTurnAsync();
        await turn.AnswerAsync("Go ahead", [.. requests.Select(request => (AIContent)request.CreateResponse(true))]);

        // Kept, it would have run at the start of the second turn and closed it with the first one's decision.
        Assert.Empty(turn.Replies);
    }

    [Fact]
    public async Task Reply_of_a_turn_asking_no_approval_is_left_alone()
    {
        ScriptedModel model = new ScriptedModel(
            [new TextContent("Here is your order."), new FunctionCallContent("r1", "Reply", new Dictionary<string, object?> { ["awaits"] = "nothing" })]);
        ApprovalTurnChatClient client = new ApprovalTurnChatClient(model);

        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "status of ORD-1")],
            new ChatOptions { Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string orderId) => "", "ConfirmOrder"))] });

        Assert.Contains(response.Messages.SelectMany(message => message.Contents), content => content is FunctionCallContent { Name: "Reply" });
    }

    /// <summary>
    /// An agent over the chain that Morgana gives its agents, minus metering and turn closure: the
    /// approval-aware model client under the tool loop, with ConfirmOrder requiring approval. Messages
    /// are filed into Morgana's history before each run, as MorganaAgent files them.
    /// </summary>
    private sealed class ApprovalUnderTest
    {
        /// <summary>Orders that ConfirmOrder actually confirmed.</summary>
        public List<string> Confirmed { get; } = [];

        /// <summary>Closures that Reply actually recorded.</summary>
        public List<string> Replies { get; } = [];

        /// <summary>The agent under test.</summary>
        private readonly AIAgent agent;

        /// <summary>Morgana's history provider, where every message is filed before the run reads it.</summary>
        private readonly MorganaChatHistoryProvider history = new MorganaChatHistoryProvider("inventory", null, NullLogger.Instance);

        /// <summary>The session, replaced by its saved and reloaded copy at the turn boundary.</summary>
        private AgentSession? session;

        /// <summary>
        /// Builds the agent: the model writes, calls ConfirmOrder and Reply together, then answers plainly.
        /// </summary>
        public ApprovalUnderTest()
        {
            ScriptedModel model = new ScriptedModel(
                [new TextContent("Shall I confirm ORD-1?"),
                 new FunctionCallContent("c1", "ConfirmOrder", new Dictionary<string, object?> { ["orderId"] = "ORD-1" }),
                 new FunctionCallContent("r1", "Reply", new Dictionary<string, object?> { ["awaits"] = "action_choice" })],
                [new TextContent("Done.")]);

            AIFunction confirmOrder = new ApprovalRequiredAIFunction(
                AIFunctionFactory.Create((string orderId) => { Confirmed.Add(orderId); return "confirmed"; }, "ConfirmOrder"));
            AIFunction reply = AIFunctionFactory.Create((string awaits) => { Replies.Add(awaits); return "Turn closed."; }, "Reply");

            agent = new FunctionInvokingChatClient(new ApprovalTurnChatClient(model))
                .AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = history, ChatOptions = new ChatOptions { Tools = [confirmOrder, reply] } });
        }

        /// <summary>Runs the turn asking for approval and answers the requests it raised.</summary>
        public async Task<List<ToolApprovalRequestContent>> RunFirstTurnAsync()
        {
            session = await agent.CreateSessionAsync();
            history.AppendMessage(session, new ChatMessage(ChatRole.User, "Confirm ORD-1"));
            await agent.RunAsync(session);
            return [.. history.GetMessages(session).SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>()];
        }

        /// <summary>Saves and reloads the session, as a turn boundary does, then sends the user's answer.</summary>
        public async Task AnswerAsync(string userText, List<AIContent> approvalAnswers)
        {
            AgentSession reloaded = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session!));
            history.AppendMessage(reloaded, new ChatMessage(ChatRole.User, [new TextContent(userText), .. approvalAnswers]));
            await agent.RunAsync(reloaded);
        }
    }

    /// <summary>
    /// Stands where the model would, answering each request with the next scripted contents.
    /// </summary>
    private sealed class ScriptedModel(params List<AIContent>[] responses) : IChatClient
    {
        /// <summary>The scripted answers still to give.</summary>
        private readonly Queue<List<AIContent>> pending = new(responses);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                pending.Count > 0 ? pending.Dequeue() : [new TextContent("")])));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No streaming is scripted in this group");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
