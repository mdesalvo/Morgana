using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.ChatClients;
using Morgana.AI.Providers;
using PromptHarness.Infrastructure;
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
    public async Task ExecutionApproval_Tool_requiring_approval_reaches_the_model_wrapped_for_it()
    {
        MorganaToolAdapter adapter = new MorganaToolAdapter();
        Func<string, string> confirmOrder = orderId => $"order {orderId} confirmed";
        Records.ToolDefinition definition = new Records.ToolDefinition("ConfirmOrder", "Confirms an order.",
            [new Records.ToolParameter("orderId", "The order.", true, "request")], RequiresExecutionApproval: true);
        adapter.AddTool(definition.Name, confirmOrder, definition);

        Assert.IsType<ApprovalRequiredAIFunction>(await adapter.CreateFunctionAsync(definition.Name));
    }

    [Fact]
    public async Task ExecutionApproval_Approved_call_runs_once_with_the_approved_arguments_across_a_saved_session()
    {
        ApprovalUnderTest turn = new ApprovalUnderTest();

        // First turn: the model asks for the confirmation and gives its words through Reply. Nothing runs.
        List<ToolApprovalRequestContent> requests = await turn.RunFirstTurnAsync();
        Assert.Equal("ConfirmOrder", ((FunctionCallContent)Assert.Single(requests).ToolCall).Name);
        Assert.Empty(turn.Confirmed);

        // Second turn, on the session as it was saved: the user's approval travels with their words.
        await turn.AnswerAsync("Go ahead", [.. requests.Select(request => (AIContent)request.CreateResponse(true))]);

        Assert.Equal(["ORD-1"], turn.Confirmed);
    }

    [Fact]
    public async Task ExecutionApproval_Declined_call_never_runs()
    {
        ApprovalUnderTest turn = new ApprovalUnderTest();

        List<ToolApprovalRequestContent> requests = await turn.RunFirstTurnAsync();
        await turn.AnswerAsync("Actually, what else do you sell?", [.. requests.Select(request => (AIContent)request.CreateResponse(false))]);

        Assert.Empty(turn.Confirmed);
    }

    [Fact]
    public async Task ExecutionApproval_Reply_written_beside_a_call_awaiting_approval_never_runs()
    {
        ApprovalUnderTest turn = new ApprovalUnderTest();

        List<ToolApprovalRequestContent> requests = await turn.RunFirstTurnAsync();
        await turn.AnswerAsync("Go ahead", [.. requests.Select(request => (AIContent)request.CreateResponse(true))]);

        // Kept, it would have run at the start of the second turn and closed it with the first one's decision.
        Assert.Empty(turn.ReplyCalls);
    }

    [Fact]
    public async Task ExecutionApproval_Reply_of_a_turn_asking_no_approval_is_left_alone()
    {
        ScriptedChatClient model = new ScriptedChatClient(
            Answer(Reply("r1", "Here is your order.")));
        ApprovalTurnChatClient client = new ApprovalTurnChatClient(model, () => null, new MorganaAIContextProvider(NullLogger.Instance));

        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "status of ORD-1")],
            new ChatOptions { Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string orderId) => "", "ConfirmOrder"))] });

        Assert.Contains(response.Messages.SelectMany(message => message.Contents), content => content is FunctionCallContent { Name: "Reply" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionApproval_Approval_asked_without_a_Reply_carrying_text_is_refused_until_it_comes(bool streaming)
    {
        FunctionCallContent Confirm(string callId) => new FunctionCallContent(callId, "ConfirmOrder", new Dictionary<string, object?> { ["orderId"] = "ORD-1" });
        ScriptedChatClient model = new ScriptedChatClient(
            Answer(Confirm("c1")),
            Answer(new TextContent("Free text that no one will read."), Confirm("c2")),
            Answer(Confirm("c3"), Reply("r3", "I'll confirm ORD-1 for you.")));
        (ApprovalTurnChatClient client, MorganaAIContextProvider provider, AgentSession session) = await ClientOverSessionAsync(model, pressedTool: null);
        List<ChatMessage> turn = [new ChatMessage(ChatRole.User, "Confirm ORD-1")];
        ChatOptions options = ApprovalOptions();

        List<AIContent> delivered = streaming
            ? [.. (await client.GetStreamingResponseAsync(turn, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken)).SelectMany(update => update.Contents)]
            : [.. (await client.GetResponseAsync(turn, options, TestContext.Current.CancellationToken)).Messages.SelectMany(message => message.Contents)];

        // Neither the call without words nor the one beside free text went through: each was answered with the refusal.
        FunctionResultContent firstRefusal = Assert.IsType<FunctionResultContent>(Assert.Single(model.Requests[1].Messages[^1].Contents));
        Assert.Equal(("c1", Refusal), (firstRefusal.CallId, firstRefusal.Result as string));
        FunctionResultContent secondRefusal = Assert.IsType<FunctionResultContent>(Assert.Single(model.Requests[2].Messages[^1].Contents));
        Assert.Equal(("c2", Refusal), (secondRefusal.CallId, secondRefusal.Result as string));

        // The one written with its Reply went through, its Reply dropped and its words recorded as the turn's answer.
        Assert.Equal("c3", Assert.Single(delivered.OfType<FunctionCallContent>(), call => call.Name == "ConfirmOrder").CallId);
        Assert.DoesNotContain(delivered, content => content is FunctionCallContent { Name: "Reply" });
        Records.ClosingDeclaration recorded = RecordedClosure(provider, session)!;
        Assert.Equal("I'll confirm ORD-1 for you.", recorded.Text);
        Assert.Equal(Records.AwaitedFromUser.Nothing, recorded.Awaits);
        Assert.Empty(recorded.Actions);
    }

    [Fact]
    public async Task ExecutionApproval_Approval_asked_with_no_Reply_text_goes_through_once_the_refusals_are_spent()
    {
        FunctionCallContent Confirm(string callId) => new FunctionCallContent(callId, "ConfirmOrder", new Dictionary<string, object?> { ["orderId"] = "ORD-1" });
        ScriptedChatClient model = new ScriptedChatClient(Answer(Confirm("c1")), Answer(Confirm("c2")), Answer(Confirm("c3")));
        (ApprovalTurnChatClient client, MorganaAIContextProvider provider, AgentSession session) = await ClientOverSessionAsync(model, pressedTool: null);

        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Confirm ORD-1")], ApprovalOptions(), TestContext.Current.CancellationToken);

        // Bounded, so a model that never gives its words cannot hold the turn: the framework's own question then speaks for it.
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal("c3", Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()).CallId);
        Assert.Null(RecordedClosure(provider, session));
    }

    [Theory]
    [InlineData("ConfirmOrder", 1)]
    [InlineData("CancelOrder", 3)]
    public async Task ExecutionApproval_Request_without_Reply_text_is_refused_unless_it_is_to_the_tool_the_user_pressed(string pressedTool, int expectedRequests)
    {
        FunctionCallContent Confirm(string callId) => new FunctionCallContent(callId, "ConfirmOrder", new Dictionary<string, object?> { ["orderId"] = "ORD-1" });
        ScriptedChatClient model = new ScriptedChatClient(Answer(Confirm("c1")), Answer(Confirm("c2")), Answer(Confirm("c3")));
        (ApprovalTurnChatClient client, _, _) = await ClientOverSessionAsync(model, pressedTool);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Confirm ORD-1")], ApprovalOptions(), TestContext.Current.CancellationToken);

        // The pressed tool's request goes through at the first ask; any other tool's is refused until the refusals are spent.
        Assert.Equal(expectedRequests, model.Requests.Count);
    }

    [Fact]
    public async Task ExecutionApproval_Reply_beside_the_pressed_tool_is_dropped_and_not_recorded()
    {
        FunctionCallContent Confirm(string callId) => new FunctionCallContent(callId, "ConfirmOrder", new Dictionary<string, object?> { ["orderId"] = "ORD-1" });
        ScriptedChatClient model = new ScriptedChatClient(Answer(Confirm("c1"), Reply("r1", "Confirming ORD-1.")));
        (ApprovalTurnChatClient client, MorganaAIContextProvider provider, AgentSession session) = await ClientOverSessionAsync(model, pressedTool: "ConfirmOrder");

        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Confirm ORD-1")], ApprovalOptions(), TestContext.Current.CancellationToken);

        // The tool runs at once and the model answers after its result, so the Reply written beside the call is not the answer.
        Assert.Null(RecordedClosure(provider, session));
        Assert.DoesNotContain(response.Messages.SelectMany(message => message.Contents), content => content is FunctionCallContent { Name: "Reply" });
    }

    /// <summary>What the client hands the calls of a refused approval request.</summary>
    private const string Refusal = "REFUSED: no Reply text";

    /// <summary>The tool list of an agent holding ConfirmOrder, which needs the user's approval.</summary>
    private static ChatOptions ApprovalOptions()
        => new ChatOptions { Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string orderId) => "", "ConfirmOrder"))] };

    /// <summary>
    /// The approval client over a session that carries the tool a button press may have named.
    /// </summary>
    /// <param name="model">The script standing where the model would.</param>
    /// <param name="pressedTool">The tool the user's button leads to; null when the turn opens on typed words.</param>
    private static async Task<(ApprovalTurnChatClient Client, MorganaAIContextProvider Provider, AgentSession Session)> ClientOverSessionAsync(ScriptedChatClient model, string? pressedTool)
    {
        MorganaAIContextProvider provider = new MorganaAIContextProvider(NullLogger.Instance);

        // The session belongs to an agent that never talks to a model: it only carries the context variables.
        AgentSession session = await new ChatClientAgent(new ScriptedChatClient()).CreateSessionAsync(TestContext.Current.CancellationToken);
        if (pressedTool is not null)
            await provider.SetVariableAsync(session, Constants.ContextKeys.PressedActionTool, pressedTool);

        return (new ApprovalTurnChatClient(model, () => session, provider, Refusal), provider, session);
    }

    /// <summary>The closure that the client recorded on the session; null when none was.</summary>
    private static Records.ClosingDeclaration? RecordedClosure(MorganaAIContextProvider provider, AgentSession session)
        => provider.GetVariable(session, Constants.ContextKeys.ClosingDeclaration) is string json
            ? JsonSerializer.Deserialize<Records.ClosingDeclaration>(json, new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true })
            : null;

    /// <summary>A call of Reply carrying the given words and awaiting nothing.</summary>
    private static FunctionCallContent Reply(string callId, string text)
        => new FunctionCallContent(callId, "Reply", new Dictionary<string, object?> { ["text"] = text, ["awaits"] = "nothing" });

    /// <summary>One model answer made of the given contents.</summary>
    private static ChatResponse Answer(params AIContent[] contents) => new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));

    /// <summary>
    /// An agent over the chain that Morgana gives its agents, minus metering and turn closing: the
    /// approval-aware model client under the tool loop, with ConfirmOrder requiring approval. Messages
    /// are filed into Morgana's history before each run, as MorganaAgent files them.
    /// </summary>
    private sealed class ApprovalUnderTest
    {
        /// <summary>Orders that ConfirmOrder actually confirmed.</summary>
        public List<string> Confirmed { get; } = [];

        /// <summary>Answers that Reply actually gave.</summary>
        public List<string> ReplyCalls { get; } = [];

        /// <summary>The agent under test.</summary>
        private readonly AIAgent agent;

        /// <summary>Morgana's history provider, where every message is filed before the run reads it.</summary>
        private readonly MorganaChatHistoryProvider history = new MorganaChatHistoryProvider("inventory", null, NullLogger.Instance);

        /// <summary>The session, replaced by its saved and reloaded copy at the turn boundary.</summary>
        private AgentSession? session;

        /// <summary>
        /// Builds the agent: the model calls ConfirmOrder and Reply together, then answers plainly.
        /// </summary>
        public ApprovalUnderTest()
        {
            ScriptedChatClient model = new ScriptedChatClient(
                Answer(new FunctionCallContent("c1", "ConfirmOrder", new Dictionary<string, object?> { ["orderId"] = "ORD-1" }),
                       new FunctionCallContent("r1", "Reply", new Dictionary<string, object?> { ["text"] = "Shall I confirm ORD-1?", ["awaits"] = "action_choice" })),
                Answer(new TextContent("Done.")));

            AIFunction confirmOrder = new ApprovalRequiredAIFunction(
                AIFunctionFactory.Create((string orderId) => { Confirmed.Add(orderId); return "confirmed"; }, "ConfirmOrder"));
            AIFunction reply = AIFunctionFactory.Create((string text, string awaits) => { ReplyCalls.Add(text); return "Turn closed."; }, "Reply");

            agent = new FunctionInvokingChatClient(new ApprovalTurnChatClient(model, () => null, new MorganaAIContextProvider(NullLogger.Instance)))
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
}
