using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.ChatClients;
using Morgana.AI.Providers;
using Morgana.AI.Services;
using Morgana.Contracts;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting how a turn closes: what Reply records and refuses, what the framework does
/// when the model forgets to call it and what a transcript reads back of what the turn delivered.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: no host turn, no model. A scripted chat client stands where the
/// model would, answering exactly what the case needs.</para>
///
/// <para>Every literal a model or a channel sees is spelled out here rather than read from
/// <c>Constants</c>, as the wire-contract groups do: the point is to notice the contract changing.</para>
/// </remarks>
public sealed class TurnClosureTests
{
    /// <summary>The service buttons as morgana.json authors them, ids being what the channels act on.</summary>
    private static readonly Records.ServiceButtons ServiceButtons = new Records.ServiceButtons(
        [new QuickReply("continue_agent", "🔮 I still need you", "I have another question for you"),
         new QuickReply("exit_agent", "✨ We're done, thanks", "We're done, thanks", true)],
        [new QuickReply("continue_agent", "💬 Ask me something else", "I want to ask you something else"),
         new QuickReply("exit_agent", "✨ We're done, thanks", "We're done, thanks", true)]);

    [Theory]
    // A departing user is let go: no button and the conversation returns to Morgana, whatever else was declared.
    [InlineData("nothing", true, false, "", true)]
    [InlineData("action_choice", true, true, "", true)]
    // A typed answer is asked with nothing gating it.
    [InlineData("typed_answer", false, false, "", false)]
    [InlineData("typed_answer", false, true, "", false)]
    // Offered actions carry the escape pair after them.
    [InlineData("action_choice", false, true, "GetInvoices-1,continue_agent,exit_agent", false)]
    [InlineData("nothing", false, true, "GetInvoices-1,continue_agent,exit_agent", false)]
    // An answered request carries the closure pair and the agent stays until the user leaves.
    [InlineData("nothing", false, false, "continue_agent,exit_agent", false)]
    [InlineData("action_choice", false, false, "continue_agent,exit_agent", false)]
    public void Framework_decides_the_service_buttons_from_the_closure(
        string awaits, bool userIsLeaving, bool withAction, string buttonIds, bool handsBack)
    {
        string actions = withAction ? """[{"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"}]""" : "[]";
        Records.TurnReply turnReply = Deserialize(
            $$"""{"awaits":"{{awaits}}","userIsLeaving":{{(userIsLeaving ? "true" : "false")}},"actions":{{actions}},"card":null}""");

        (List<QuickReply>? quickReplies, bool handsBackConversation) = turnReply.ToDelivery(ServiceButtons);

        Assert.Equal(buttonIds, string.Join(",", quickReplies?.Select(button => button.Id) ?? []));
        Assert.Equal(handsBack, handsBackConversation);
    }

    [Fact]
    public void Answered_request_with_no_authored_closure_hands_the_conversation_back()
    {
        Records.TurnReply turnReply = Deserialize("""{"awaits":"nothing","userIsLeaving":false,"actions":[],"card":null}""");

        (List<QuickReply>? quickReplies, bool handsBack) = turnReply.ToDelivery(new Records.ServiceButtons([], []));

        // With no button to leave by, staying in service would trap the user with this agent.
        Assert.Null(quickReplies);
        Assert.True(handsBack);
    }

    [Fact]
    public void Two_actions_on_one_tool_stay_two_buttons()
    {
        Records.TurnReply turnReply = Deserialize("""
            {"awaits":"action_choice","userIsLeaving":false,"card":null,"actions":[
              {"tool":"ConfirmOrder","label":"✅ Confirm","value":"Confirm my order"},
              {"tool":"ConfirmOrder","label":"✅ Confirm both","value":"Confirm both orders"}]}
            """);

        List<QuickReply> buttons = turnReply.ToDelivery(ServiceButtons).QuickReplies!;

        Assert.Equal(["ConfirmOrder-1", "ConfirmOrder-2", "continue_agent", "exit_agent"], buttons.Select(button => button.Id));
        Assert.Equal("Confirm my order", buttons[0].Value);
    }

    [Fact]
    public async Task Reply_discards_an_action_leading_to_a_tool_the_agent_lacks()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);

        await reply.Function.InvokeAsync(Arguments("""
            {"awaits":"action_choice","userIsLeaving":false,"actions":[
              {"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"},
              {"tool":"DownloadPdf","label":"⬇️ PDF","value":"Download it as PDF"}]}
            """));

        Assert.Equal("GetInvoices", Assert.Single(reply.Recorded()!.Actions).Tool);
    }

    [Fact]
    public async Task Reply_schema_is_a_contract_for_awaits_and_card()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        JsonElement properties = reply.Function.JsonSchema.GetProperty("properties");

        Assert.Equal(["nothing", "typed_answer", "action_choice"],
            properties.GetProperty("awaits").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

        // Every component is one closed alternative, told apart by its type.
        string[] componentTypes = [.. properties.GetProperty("card").GetProperty("properties").GetProperty("components")
            .GetProperty("items").GetProperty("anyOf").EnumerateArray()
            .Select(alternative => alternative.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!)];
        Assert.Equal(["text_block", "key_value", "divider", "list", "section", "grid", "badge", "image"], componentTypes);

        string[] required = [.. reply.Function.JsonSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()!)];
        Assert.Equal(["awaits", "userIsLeaving"], required);
    }

    [Fact]
    public async Task Reply_records_the_closure_and_closes_the_turn()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();

        object? result = await reply.Function.InvokeAsync(Arguments("""
            {"awaits":"action_choice","userIsLeaving":false,
             "actions":[{"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"}],
             "card":{"components":[{"content":"Overdue","type":"text_block"}],"title":"Account","subtitle":null}}
            """));

        Assert.Equal("Turn closed.", result?.ToString());
        Records.TurnReply recorded = reply.Recorded()!;
        Assert.Equal(Records.AwaitedFromUser.ActionChoice, recorded.Awaits);
        Assert.Equal("GetInvoices", Assert.Single(recorded.Actions).Tool);
        Assert.IsType<TextBlockComponent>(Assert.Single(recorded.Card!.Components));
    }

    [Fact]
    public async Task Reply_refuses_a_card_nesting_deeper_than_three_levels()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();

        // Refused with the reason as a fact, which the agent's tool loop hands back to the model.
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments("""
            {"awaits":"nothing","userIsLeaving":false,"card":{"title":"Deep","subtitle":null,"components":[
              {"type":"section","title":"1","subtitle":null,"components":[
                {"type":"section","title":"2","subtitle":null,"components":[
                  {"type":"section","title":"3","subtitle":null,"components":[{"type":"divider"}]}]}]}]}}
            """)).AsTask());

        Assert.Contains("nests 4 levels", refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task Reply_before_any_text_is_refused_until_the_turn_is_written()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(
            Call("Reply", """{"awaits":"nothing","userIsLeaving":true}"""),
            Merge(Text("Farewell: may your garden bloom."),
                  Call("Reply", """{"awaits":"nothing","userIsLeaving":true}""")));

        await RunTurnAsync(model, reply, canForceToolCall: true);

        // The first closure would have left the turn mute: refused, the model wrote and closed again.
        Assert.Equal(2, model.Requests.Count);
        Assert.True(reply.Recorded()!.UserIsLeaving);
    }

    [Theory]
    [InlineData("""{"title":"t","subtitle":null,"components":[{"type":"chart","content":"x"}]}""")]
    [InlineData("""{"subtitle":null,"components":[]}""")]
    public async Task Reply_rejects_a_card_breaking_its_schema(string card)
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();

        // The tool loop hands this error back to the model for repair; nothing is recorded meanwhile.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            reply.Function.InvokeAsync(Arguments($$"""{"awaits":"nothing","userIsLeaving":false,"card":{{card}}}""")).AsTask());
        Assert.Null(reply.Recorded());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Turn_written_without_Reply_is_closed_on_the_model_s_behalf(bool canForceToolCall)
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        const string closure = """{"awaits":"typed_answer","userIsLeaving":false,"actions":[],"card":null}""";
        ScriptedChatClient model = new ScriptedChatClient(
            Text("Which invoice do you mean?"),
            canForceToolCall ? Call("Reply", closure) : Text(closure));

        await RunTurnAsync(model, reply, canForceToolCall);

        // The provider able to be told which tool to call was told; the other was asked for JSON.
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(canForceToolCall, model.Requests[1].Options?.ToolMode is RequiredChatToolMode { RequiredFunctionName: "Reply" });
        Assert.Equal(canForceToolCall, model.Requests[1].Options?.ResponseFormat is null);
        Assert.Equal(Records.AwaitedFromUser.TypedAnswer, reply.Recorded()!.Awaits);
    }

    [Fact]
    public async Task Turn_closed_by_the_model_costs_no_further_call()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(
            Merge(Text("Here are your invoices."),
                  Call("Reply", """{"awaits":"nothing","userIsLeaving":false}""")));

        await RunTurnAsync(model, reply, canForceToolCall: true);

        Assert.Single(model.Requests);
        Assert.Equal(Records.AwaitedFromUser.Nothing, reply.Recorded()!.Awaits);
    }

    [Fact]
    public async Task Turn_with_no_text_is_left_to_the_agent_to_run_again()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(Text(""));

        await RunTurnAsync(model, reply, canForceToolCall: true);

        Assert.Single(model.Requests);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task Transcript_reads_back_the_buttons_and_card_the_turn_delivered()
    {
        string storagePath = Path.Combine(Path.GetTempPath(), $"turn-closure-{Guid.NewGuid():N}");
        SQLiteConversationPersistenceService persistence = new SQLiteConversationPersistenceService(
            Options.Create(new Records.ConversationPersistenceOptions
            {
                StoragePath = storagePath,
                EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }),
            NullLogger.Instance);

        try
        {
            MorganaChatHistoryProvider history = new MorganaChatHistoryProvider("billing", null, NullLogger.Instance);
            ChatClientAgent agent = new ChatClientAgent(new ScriptedChatClient(), new ChatClientAgentOptions { ChatHistoryProvider = history });
            AgentSession session = await agent.CreateSessionAsync();

            // The turn as MorganaAgent leaves it: the user-facing message carries what was delivered.
            ChatMessage answer = new ChatMessage(ChatRole.Assistant, "Here is your account.") { CreatedAt = DateTimeOffset.UtcNow };
            answer.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["morgana:user_facing"] = true,
                ["morgana:turn_text"] = "Here is your account.",
                ["morgana:turn_quick_replies"] = """[{"id":"GetInvoices-1","label":"📄 Invoices","value":"Show my invoices"}]""",
                ["morgana:turn_rich_card"] = """{"title":"Account","subtitle":null,"components":[{"type":"badge","text":"Active"}]}"""
            };
            history.AppendMessage(session, new ChatMessage(ChatRole.User, "Show my account") { CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
            history.AppendMessage(session, answer);

            await persistence.EnsureDatabaseInitializedAsync("conv1");
            await persistence.SaveAgentConversationAsync("billing-conv1", agent, session, isCompleted: false);

            MorganaChatMessage delivered = (await persistence.GetConversationHistoryAsync("conv1")).Last();

            Assert.Equal("Here is your account.", delivered.Text);
            Assert.Equal("GetInvoices-1", Assert.Single(delivered.QuickReplies!).Id);
            Assert.IsType<BadgeComponent>(Assert.Single(delivered.RichCard!.Components));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(storagePath, recursive: true);
        }
    }

    /// <summary>Reads a closure as MorganaAgent reads what Reply recorded.</summary>
    private static Records.TurnReply Deserialize(string json)
        => JsonSerializer.Deserialize<Records.TurnReply>(json, new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true })!;

    /// <summary>Arguments as a model sends them: one JSON value per parameter.</summary>
    private static AIFunctionArguments Arguments(string json)
        => new AIFunctionArguments(JsonDocument.Parse(json).RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

    /// <summary>One model answer carrying text.</summary>
    private static ChatResponse Text(string text) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>One model answer calling a tool.</summary>
    private static ChatResponse Call(string name, string json)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, Arguments(json))]));

    /// <summary>One model answer writing text and then calling a tool, as a closing turn does.</summary>
    private static ChatResponse Merge(ChatResponse text, ChatResponse call)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. text.Messages[0].Contents, .. call.Messages[0].Contents]));

    /// <summary>
    /// Runs one turn through the chain an agent runs on: the tool loop, then the turn-closing client.
    /// </summary>
    private static async Task RunTurnAsync(ScriptedChatClient model, ReplyUnderTest reply, bool canForceToolCall)
    {
        TurnClosingChatClient chain = new TurnClosingChatClient(
            new FunctionInvokingChatClient(model),
            "[TURN CLOSURE]\nDeclare how that turn closes.",
            canForceToolCall,
            NullLogger.Instance);

        await chain.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Show me an invoice")],
            new ChatOptions { Instructions = "You are a billing assistant.", Tools = [reply.Function] });
    }

    /// <summary>
    /// The Reply tool as an agent holds it: the base tool, registered through the adapter, over a live session.
    /// </summary>
    private sealed class ReplyUnderTest
    {
        /// <summary>The function the model calls.</summary>
        public AIFunction Function { get; private set; } = null!;

        /// <summary>The agent's context store, where Reply records the closure.</summary>
        private readonly MorganaAIContextProvider provider = new MorganaAIContextProvider(NullLogger.Instance);

        /// <summary>The in-flight session.</summary>
        private AgentSession session = null!;

        /// <summary>
        /// Builds Reply over a session of an agent that never talks to a model.
        /// </summary>
        /// <param name="actionableToolNames">The agent's tools an action may lead to; null accepts any.</param>
        public static async Task<ReplyUnderTest> CreateAsync(IReadOnlyCollection<string>? actionableToolNames = null)
        {
            ReplyUnderTest reply = new ReplyUnderTest();
            reply.session = await new ChatClientAgent(new ScriptedChatClient()).CreateSessionAsync();

            Records.ToolDefinition definition = new Records.ToolDefinition("Reply", "Closes your turn.",
            [
                new Records.ToolParameter("awaits", "What the turn awaits.", true, ""),
                new Records.ToolParameter("userIsLeaving", "Whether the user is leaving.", true, ""),
                new Records.ToolParameter("actions", "The actions offered.", false, ""),
                new Records.ToolParameter("card", "The card.", false, "")
            ]);

            MorganaTool baseTool = new MorganaTool(NullLogger.Instance,
                () => new MorganaTool.ToolContext(reply.provider, reply.session, "turn-closure", actionableToolNames));
            MorganaToolAdapter adapter = new MorganaToolAdapter(NullLogger.Instance,
                () => new MorganaTool.ToolContext(reply.provider, reply.session, "turn-closure"));

            Func<Records.AwaitedFromUser, bool, List<Records.ReplyAction>?, RichCard?, Task<object>> implementation = baseTool.Reply;
            adapter.AddTool(definition.Name, implementation, definition);
            reply.Function = await adapter.CreateFunctionAsync(definition.Name);

            return reply;
        }

        /// <summary>The closure recorded on the session; null when none was.</summary>
        public Records.TurnReply? Recorded()
            => provider.GetVariable(session, "turn_reply") is string json ? Deserialize(json) : null;
    }

    /// <summary>
    /// Stands where the model would, answering each request with the next scripted response and
    /// keeping every request it received.
    /// </summary>
    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        /// <summary>The scripted answers still to give.</summary>
        private readonly Queue<ChatResponse> pending = new(responses);

        /// <summary>Every request received, in order.</summary>
        public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(([.. messages], options));
            return Task.FromResult(pending.Count > 0 ? pending.Dequeue() : Text(""));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No streaming is scripted in this group");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
