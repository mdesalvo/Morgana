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
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.AI.Services;
using Morgana.AI.Tools;
using Morgana.Contracts;
using PromptHarness.Infrastructure;
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
    /// <summary>The framework replies as morgana.json authors them, ids being what the channels act on.</summary>
    private static readonly Records.FrameworkReplies FrameworkReplies = new Records.FrameworkReplies(
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
    [InlineData("action_choice", false, true, "GetInvoices#1,continue_agent,exit_agent", false)]
    [InlineData("nothing", false, true, "GetInvoices#1,continue_agent,exit_agent", false)]
    // An answered request carries the closure pair and the agent stays until the user leaves.
    [InlineData("nothing", false, false, "continue_agent,exit_agent", false)]
    [InlineData("action_choice", false, false, "continue_agent,exit_agent", false)]
    public void TurnClosure_Framework_decides_the_framework_replies_from_the_closure(
        string awaits, bool userIsLeaving, bool withAction, string buttonIds, bool handsBack)
    {
        string actions = withAction ? """[{"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"}]""" : "[]";
        Records.TurnReply turnReply = Deserialize(
            $$"""{"awaits":"{{awaits}}","userIsLeaving":{{(userIsLeaving ? "true" : "false")}},"actions":{{actions}},"card":null}""");

        (List<QuickReply>? quickReplies, bool handsBackConversation) = turnReply.ToDelivery(FrameworkReplies);

        Assert.Equal(buttonIds, string.Join(",", quickReplies?.Select(button => button.Id) ?? []));
        Assert.Equal(handsBack, handsBackConversation);
    }

    [Fact]
    public void TurnClosure_Answered_request_with_no_authored_closure_hands_the_conversation_back()
    {
        Records.TurnReply turnReply = Deserialize("""{"awaits":"nothing","userIsLeaving":false,"actions":[],"card":null}""");

        (List<QuickReply>? quickReplies, bool handsBack) = turnReply.ToDelivery(new Records.FrameworkReplies([], []));

        // With no button to leave by, staying in service would trap the user with this agent.
        Assert.Null(quickReplies);
        Assert.True(handsBack);
    }

    [Fact]
    public void TurnClosure_Two_actions_on_one_tool_stay_two_buttons()
    {
        Records.TurnReply turnReply = Deserialize("""
            {"awaits":"action_choice","userIsLeaving":false,"card":null,"actions":[
              {"tool":"ConfirmOrder","label":"✅ Confirm","value":"Confirm my order"},
              {"tool":"ConfirmOrder","label":"✅ Confirm both","value":"Confirm both orders"}]}
            """);

        List<QuickReply> buttons = turnReply.ToDelivery(FrameworkReplies).QuickReplies!;

        Assert.Equal(["ConfirmOrder#1", "ConfirmOrder#2", "continue_agent", "exit_agent"], buttons.Select(button => button.Id));
        Assert.Equal("Confirm my order", buttons[0].Value);
    }

    [Fact]
    public void TurnClosure_Menu_replaces_the_closure_actions_and_drops_the_card()
    {
        // A closure that carried an action and a card, as a model might write on a landing.
        Records.TurnReply turnReply = Deserialize("""
            {"awaits":"typed_answer","userIsLeaving":false,
             "actions":[{"tool":"GetBalance","label":"Balance","value":"What is my balance?"}],
             "card":{"title":"Account","subtitle":null,"components":[{"type":"divider"}]}}
            """);
        List<Records.ReplyAction> menu =
        [
            new Records.ReplyAction("GetInvoices", "Invoices", "Show my invoices"),
            new Records.ReplyAction("OpenAccount", "Open an account", "I want to open an account")
        ];

        Records.TurnReply presented = turnReply.WithMenu(menu);
        List<QuickReply> buttons = presented.ToDelivery(FrameworkReplies).QuickReplies!;

        // The two menu buttons come first and the framework's escape pair follows; the card is gone.
        Assert.Equal(["GetInvoices#1", "OpenAccount#2", "continue_agent", "exit_agent"], buttons.Select(button => button.Id));
        Assert.Equal(["Invoices", "Open an account", "💬 Ask me something else", "✨ We're done, thanks"], buttons.Select(button => button.Label));
        Assert.Null(presented.Card);
        Assert.Equal(Records.AwaitedFromUser.ActionChoice, presented.Awaits);
    }

    [Fact]
    public void TurnClosure_Empty_menu_closes_the_turn_with_the_closure_pair()
    {
        Records.TurnReply turnReply = Deserialize("""{"awaits":"typed_answer","userIsLeaving":false,"actions":[],"card":null}""");

        Records.TurnReply presented = turnReply.WithMenu([]);
        (List<QuickReply>? quickReplies, bool handsBack) = presented.ToDelivery(FrameworkReplies);

        // An agent with nothing to offer awaits nothing and keeps the conversation behind the closure buttons.
        Assert.Equal(Records.AwaitedFromUser.Nothing, presented.Awaits);
        Assert.Equal(["continue_agent", "exit_agent"], quickReplies!.Select(button => button.Id));
        Assert.Equal(["🔮 I still need you", "✨ We're done, thanks"], quickReplies!.Select(button => button.Label));
        Assert.False(handsBack);
    }

    [Fact]
    public async Task TurnClosure_Landing_offers_the_model_only_Present_and_Reply()
    {
        // The model, the session and the context store that decide which tools the model is offered.
        (ScriptedChatClient model, AgentSession session, MorganaAIContextProvider provider) = await ToolNarrowingAsync();

        // The landing flag is what the agent sets before its model runs on a pressed welcome button.
        await provider.SetVariableAsync(session, "landing", true);

        // The request crosses the narrowing client the way a turn of the agent does.
        await new WorkflowToolsChatClient(model, () => session, provider, [], 3, canForceToolCall: false)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "What can you do with my invoices?")], AgentOptions(), TestContext.Current.CancellationToken);

        // Whatever else the agent holds stays out: the turn is the agent introducing itself.
        Assert.Equal(["Present", "Reply"], model.Requests.Single().Options!.Tools!.Select(tool => tool.Name).Order());
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task TurnClosure_Landing_forces_Present_only_while_it_is_uncalled_and_the_provider_can_force_a_call(bool canForceToolCall, bool presentCalled, bool expectForced)
    {
        (ScriptedChatClient model, AgentSession session, MorganaAIContextProvider provider) = await ToolNarrowingAsync();
        await provider.SetVariableAsync(session, "landing", true);

        // A turn in which Present has already been called carries its call after the user's words.
        List<ChatMessage> turn = [new ChatMessage(ChatRole.User, "What can you do with my invoices?")];
        if (presentCalled)
            turn.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("p1", "Present")]));

        await new WorkflowToolsChatClient(model, () => session, provider, [], 3, canForceToolCall)
            .GetResponseAsync(turn, AgentOptions(), TestContext.Current.CancellationToken);

        // The first call of a landing names Present; once it ran the model writes its text and closes with Reply.
        ChatToolMode? toolMode = model.Requests.Single().Options!.ToolMode;
        Assert.Equal(expectForced, toolMode is RequiredChatToolMode { RequiredFunctionName: "Present" });
        Assert.True(expectForced || toolMode is null);
    }

    [Fact]
    public async Task TurnClosure_Ordinary_turn_is_never_offered_Present()
    {
        // A session with no landing flag: the turn of a user who is already talking to the agent.
        (ScriptedChatClient model, AgentSession session, MorganaAIContextProvider provider) = await ToolNarrowingAsync();

        // The request crosses the narrowing client the way a turn of the agent does.
        await new WorkflowToolsChatClient(model, () => session, provider, [], 3, canForceToolCall: false)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "Show my invoices")], AgentOptions(), TestContext.Current.CancellationToken);

        // Presenting the agent belongs to a landing alone: any other turn keeps the domain tools and Reply.
        Assert.Equal(["GetInvoices", "Reply"], model.Requests.Single().Options!.Tools!.Select(tool => tool.Name).Order());
    }

    /// <summary>A scripted model answering once beside the session and context store that decide which tools it is offered.</summary>
    private static async Task<(ScriptedChatClient Model, AgentSession Session, MorganaAIContextProvider Provider)> ToolNarrowingAsync()
    {
        // One plain answer is enough: the case reads the tools of the request, not the response.
        ScriptedChatClient model = new ScriptedChatClient(Text("Here you are."));
        // The session belongs to an agent that never talks to a model: it only carries the context variables.
        AgentSession session = await new ChatClientAgent(new ScriptedChatClient()).CreateSessionAsync();

        // The context store is the one the narrowing client reads the landing flag from.
        return (model, session, new MorganaAIContextProvider(NullLogger.Instance));
    }

    /// <summary>The options of an agent holding the base tools Present and Reply beside one domain tool.</summary>
    private static ChatOptions AgentOptions()
        => new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(() => "menu", "Present"),
                AIFunctionFactory.Create(() => "closed", "Reply"),
                AIFunctionFactory.Create(() => "invoices", "GetInvoices")
            ]
        };

    [Theory]
    [InlineData("ConfirmOrder#1", "ConfirmOrder")]
    [InlineData("continue_agent", null)]
    [InlineData("approve_action", null)]
    public void TurnClosure_Pressed_button_names_the_tool_its_action_leads_to(string buttonId, string? tool)
        => Assert.Equal(tool, Records.TurnReply.ActionTool(buttonId));

    [Fact]
    public async Task TurnClosure_Reply_refuses_an_action_leading_to_a_tool_the_agent_lacks()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);

        // Refused with the fact stated: what the action leads to and the tools that it could have led to.
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments("""
            {"awaits":"action_choice","userIsLeaving":false,"actions":[
              {"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"},
              {"tool":"DownloadPdf","label":"⬇️ PDF","value":"Download it as PDF"}]}
            """)).AsTask());
        Assert.Equal("the action '⬇️ PDF' leads to 'DownloadPdf', which is not one of your tools: GetInvoices", refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Theory]
    [InlineData("""{"awaits":"action_choice","userIsLeaving":false,"actions":[null]}""", "actions[0]")]
    [InlineData("""{"awaits":"nothing","userIsLeaving":false,"card":{"title":"t","subtitle":null,"components":[{"type":"divider"},{"type":"section","title":"s","subtitle":null,"components":[null]}]}}""", "card.components[1].components[0]")]
    public async Task TurnClosure_Reply_refuses_a_null_element_in_a_list(string arguments, string place)
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);

        // Refused with the place of the hole, never as a failure of the code that reads the list.
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments(arguments)).AsTask());
        Assert.Equal($"{place} is null and a list holds no null element", refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task TurnClosure_Reply_schema_states_which_fields_may_be_null()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);
        JsonElement properties = reply.Function.JsonSchema.GetProperty("properties");
        JsonElement card = properties.GetProperty("card");

        // The card itself is optional.
        Assert.Equal(["object", "null"], card.GetProperty("type").EnumerateArray().Select(type => type.GetString()));

        // A title is declared non-nullable while a subtitle is declared nullable.
        Assert.Equal("string", card.GetProperty("properties").GetProperty("title").GetProperty("type").GetString());
        Assert.Equal(["string", "null"], card.GetProperty("properties").GetProperty("subtitle").GetProperty("type").EnumerateArray().Select(type => type.GetString()));

        // The elements of the lists are declared non-nullable too.
        Assert.Equal("object", properties.GetProperty("actions").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("object", card.GetProperty("properties").GetProperty("components").GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public async Task TurnClosure_Reply_schema_offers_actions_only_toward_the_tools_of_the_agent()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices", "DownloadPdf"]);

        JsonElement tool = reply.Function.JsonSchema.GetProperty("properties").GetProperty("actions")
            .GetProperty("items").GetProperty("properties").GetProperty("tool");
        Assert.Equal(["GetInvoices", "DownloadPdf"], tool.GetProperty("enum").EnumerateArray().Select(name => name.GetString()));

        // An agent with nothing to lead to has no actions to offer.
        ReplyUnderTest toollessReply = await ReplyUnderTest.CreateAsync(actionableToolNames: []);
        Assert.False(toollessReply.Function.JsonSchema.GetProperty("properties").TryGetProperty("actions", out _));
    }

    [Fact]
    public async Task TurnClosure_Reply_schema_admits_the_card_depth_that_the_validation_accepts_and_no_deeper()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);
        JsonElement components = reply.Function.JsonSchema.GetProperty("properties").GetProperty("card")
            .GetProperty("properties").GetProperty("components");

        // Levels at which a section is still offered: the card's components, then the section's, then the nested section's.
        int levelsOfferingSection = 0;
        JsonElement level = components.GetProperty("items");
        while (true)
        {
            JsonElement? section = level.GetProperty("anyOf").EnumerateArray()
                .Where(alternative => alternative.GetProperty("properties").GetProperty("type").GetProperty("const").GetString() == "section")
                .Select(alternative => (JsonElement?)alternative).FirstOrDefault();
            if (section is null)
                break;
            levelsOfferingSection++;
            level = section.Value.GetProperty("properties").GetProperty("components").GetProperty("items");
        }
        Assert.Equal(2, levelsOfferingSection);

        // The deepest card that the schema allows is accepted by the validation.
        object? deepestCardResult = await reply.Function.InvokeAsync(Arguments("""
            {"awaits":"nothing","userIsLeaving":false,"card":{"title":"Deep","subtitle":null,"components":[
              {"type":"section","title":"1","subtitle":null,"components":[
                {"type":"section","title":"2","subtitle":null,"components":[{"type":"divider"}]}]}]}}
            """));
        Assert.Equal("TurnClosed", Assert.IsType<Records.FrameworkToolResult>(deepestCardResult).Name);

        // One level deeper is what the validation refuses, so the schema does not offer it.
        ReplyUnderTest replyForDeeperCard = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => replyForDeeperCard.Function.InvokeAsync(Arguments("""
            {"awaits":"nothing","userIsLeaving":false,"card":{"title":"Deep","subtitle":null,"components":[
              {"type":"section","title":"1","subtitle":null,"components":[
                {"type":"section","title":"2","subtitle":null,"components":[
                  {"type":"section","title":"3","subtitle":null,"components":[{"type":"divider"}]}]}]}]}}
            """)).AsTask());
        Assert.Equal("the card nests 4 levels and at most 3 are allowed", refusal.Message);
    }

    [Fact]
    public async Task TurnClosure_Reply_schema_is_a_contract_for_awaits_and_card()
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
    public async Task TurnClosure_Reply_records_the_closure_and_closes_the_turn()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();

        object? result = await reply.Function.InvokeAsync(Arguments("""
            {"awaits":"action_choice","userIsLeaving":false,
             "actions":[{"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"}],
             "card":{"components":[{"content":"Overdue","type":"text_block"}],"title":"Account","subtitle":null}}
            """));

        Assert.Equal("TurnClosed", Assert.IsType<Records.FrameworkToolResult>(result).Name);
        Records.TurnReply recorded = reply.Recorded()!;
        Assert.Equal(Records.AwaitedFromUser.ActionChoice, recorded.Awaits);
        Assert.Equal("GetInvoices", Assert.Single(recorded.Actions).Tool);
        Assert.IsType<TextBlockComponent>(Assert.Single(recorded.Card!.Components));
    }

    [Fact]
    public async Task TurnClosure_Reply_refuses_a_card_nesting_deeper_than_three_levels()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();

        // Refused with the fact stated: the depth the card reaches and the limit; the tool loop hands it back to the model.
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments("""
            {"awaits":"nothing","userIsLeaving":false,"card":{"title":"Deep","subtitle":null,"components":[
              {"type":"section","title":"1","subtitle":null,"components":[
                {"type":"section","title":"2","subtitle":null,"components":[
                  {"type":"section","title":"3","subtitle":null,"components":[{"type":"divider"}]}]}]}]}}
            """)).AsTask());
        Assert.Equal("the card nests 4 levels and at most 3 are allowed", refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task TurnClosure_Reply_refuses_a_card_holding_more_components_than_the_channels_lay_out()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        string dividers = string.Join(",", Enumerable.Repeat("""{"type":"divider"}""", 51));

        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments(
            """{"awaits":"nothing","userIsLeaving":false,"card":{"title":"Long","subtitle":null,"components":[""" + dividers + "]}}")).AsTask());
        Assert.Equal("the card holds 51 components and at most 50 are allowed", refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task TurnClosure_Reply_at_a_choice_step_refuses_actions_other_than_the_steps_tools()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(
            actionableToolNames: ["Pay", "Postpone"], choiceStep: ("Settle", "Choose", ["Pay", "Postpone"]));

        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments("""
            {"awaits":"action_choice","userIsLeaving":false,"actions":[
              {"tool":"Pay","label":"Pay now","value":"Pay now"}]}
            """)).AsTask());
        Assert.Equal("the actions offered lead to [Pay] while step 'Choose' of workflow 'Settle' takes exactly one action to each of: Pay, Postpone", refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task TurnClosure_Reply_before_any_text_is_refused_until_the_turn_is_written()
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
    public async Task TurnClosure_Reply_rejects_a_card_breaking_its_schema(string card)
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
    public async Task TurnClosure_Turn_written_without_Reply_is_closed_on_the_model_s_behalf(bool canForceToolCall)
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
    public async Task TurnClosure_Structured_closure_is_requested_under_the_schema_that_Reply_publishes()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);
        ScriptedChatClient model = new ScriptedChatClient(
            Text("Which invoice do you mean?"),
            Text("""{"awaits":"typed_answer","userIsLeaving":false,"actions":[],"card":null}"""));

        await RunTurnAsync(model, reply, canForceToolCall: false);

        // The answer is constrained to the schema of Reply as the agent publishes it: its descriptions, its nullability, its actions enum and its card depth.
        ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(model.Requests[1].Options?.ResponseFormat);
        Assert.Equal("Reply", format.SchemaName);
        Assert.Equal(reply.Function.JsonSchema.GetRawText(), format.Schema!.Value.GetRawText());
        Assert.Equal(Records.AwaitedFromUser.TypedAnswer, reply.Recorded()!.Awaits);
    }

    [Fact]
    public async Task TurnClosure_Turn_closed_by_the_model_costs_no_further_call()
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
    public async Task TurnClosure_Reply_repeated_in_the_closing_response_is_answered_and_the_first_closure_stands()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(
            Merge(Merge(Text("Here are your invoices."),
                        Call("Reply", """{"awaits":"typed_answer","userIsLeaving":false,"card":{"components":[{"content":"Overdue","type":"text_block"}],"title":"Invoices","subtitle":null}}""")),
                  Call("Reply", """{"awaits":"nothing","userIsLeaving":false}""")));

        ChatResponse turn = await RunTurnAsync(model, reply, canForceToolCall: true);

        // Every call of the response has its result: without one the next turn's history would be refused.
        List<AIContent> contents = [.. turn.Messages.SelectMany(message => message.Contents)];
        Assert.Equal(
            contents.OfType<FunctionCallContent>().Select(call => call.CallId).Order(),
            contents.OfType<FunctionResultContent>().Select(result => result.CallId).Order());
        Assert.Single(model.Requests);
        Assert.Equal(Records.AwaitedFromUser.TypedAnswer, reply.Recorded()!.Awaits);
        Assert.Equal("Invoices", reply.Recorded()!.Card!.Title);
    }

    [Fact]
    public async Task TurnClosure_Turn_with_no_text_is_left_to_the_agent_to_run_again()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(Text(""));

        await RunTurnAsync(model, reply, canForceToolCall: true);

        Assert.Single(model.Requests);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task TurnClosure_Transcript_reads_back_the_buttons_and_card_the_turn_delivered()
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

    [Fact]
    public async Task TurnClosure_Welcome_buttons_are_the_configured_intents_whatever_the_model_wrote()
    {
        // The model wrote the welcome text alone: nothing in its answer names a button.
        Records.PresentationResult welcome = await PresentAsync("welcome-written", """{"message":"Welcome to the greenhouse."}""");

        // One button per intent in configuration order: the id is the intent, the value is what a press sends.
        Assert.Equal("Welcome to the greenhouse.", welcome.Message);
        Assert.Equal(["billing", "contract", "monkeys", "inventory"], welcome.QuickReplies.Select(button => button.Id));
        Assert.Equal(["🧾 Invoices", "📜 Care Plan", "🐒 Primate Almanac", "🌿 Greenhouse"], welcome.QuickReplies.Select(button => button.Label));
        Assert.Equal(
            ["What can you do with my invoices?", "What can you tell me about the care plan?", "What can you tell me about monkeys?", "What can you do for me in the greenhouse?"],
            welcome.QuickReplies.Select(button => button.Value));
    }

    [Fact]
    public async Task TurnClosure_Welcome_fallback_carries_the_same_buttons_when_the_model_fails()
    {
        // No answer to give: the model call throws, as a provider outage would.
        Records.PresentationResult welcome = await PresentAsync("welcome-failed", welcomeAnswer: null);

        // The user is still greeted and the buttons are the ones that a written welcome carries.
        Assert.False(string.IsNullOrWhiteSpace(welcome.Message));
        Assert.Equal(["billing", "contract", "monkeys", "inventory"], welcome.QuickReplies.Select(button => button.Id));
        Assert.Equal(["🧾 Invoices", "📜 Care Plan", "🐒 Primate Almanac", "🌿 Greenhouse"], welcome.QuickReplies.Select(button => button.Label));
        Assert.Equal(
            ["What can you do with my invoices?", "What can you tell me about the care plan?", "What can you tell me about monkeys?", "What can you do for me in the greenhouse?"],
            welcome.QuickReplies.Select(button => button.Value));
    }

    /// <summary>Presents the example plugin's four intents on a channel of the given name, over a model that answers as given.</summary>
    private static async Task<Records.PresentationResult> PresentAsync(string channelName, string? welcomeAnswer)
    {
        // The presenter caches by channel name for the whole process: every case brings a channel of its own.
        ConfigurationPromptResolverService resolver = new ConfigurationPromptResolverService(new ScriptedDomain());
        WelcomeModel model = new WelcomeModel(welcomeAnswer);
        LLMPresenterService presenter = new LLMPresenterService(
            model, resolver, new SingleChannelStore(channelName), new MorganaChannelAdapter(model, resolver, NullLogger.Instance), NullLogger.Instance);

        // The intents as Examples/agents.json declares them.
        return await presenter.GenerateAsync(
        [
            new Records.IntentDefinition("billing", "Invoices and payments.", "🧾 Invoices", "What can you do with my invoices?"),
            new Records.IntentDefinition("contract", "The care plan.", "📜 Care Plan", "What can you tell me about the care plan?"),
            new Records.IntentDefinition("monkeys", "Primates.", "🐒 Primate Almanac", "What can you tell me about monkeys?"),
            new Records.IntentDefinition("inventory", "The greenhouse catalogue.", "🌿 Greenhouse", "What can you do for me in the greenhouse?")
        ], "welcome-conversation");
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
    /// Runs one turn through the chain that an agent runs on: the tool loop, then the turn-closing client.
    /// </summary>
    private static async Task<ChatResponse> RunTurnAsync(ScriptedChatClient model, ReplyUnderTest reply, bool canForceToolCall)
    {
        TurnClosingChatClient chain = new TurnClosingChatClient(
            new FunctionInvokingChatClient(model),
            "[TURN CLOSURE]\nDeclare how that turn closes.",
            "Turn closed.",
            canForceToolCall,
            NullLogger.Instance);

        return await chain.GetResponseAsync(
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
        /// <param name="actionableToolNames">The agent's tools that an action may lead to; null accepts any.</param>
        /// <param name="choiceStep">The choice step that the turn stands at, if any.</param>
        public static async Task<ReplyUnderTest> CreateAsync(
            IReadOnlyCollection<string>? actionableToolNames = null,
            (string Workflow, string Step, IReadOnlyList<string> Tools)? choiceStep = null)
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

            ReplyTool baseTool = new ReplyTool(NullLogger.Instance,
                () => new MorganaTool.ToolContext(reply.provider, reply.session, "turn-closure", actionableToolNames, choiceStep));
            MorganaToolAdapter adapter = new MorganaToolAdapter(NullLogger.Instance,
                () => new MorganaTool.ToolContext(reply.provider, reply.session, "turn-closure"));

            Func<Records.AwaitedFromUser, bool, List<Records.ReplyAction>?, RichCard?, Task<Records.FrameworkToolResult>> implementation = baseTool.Reply;
            adapter.AddTool(definition.Name, implementation, definition);
            AIFunction createdReply = await adapter.CreateFunctionAsync(definition.Name);

            // An agent that declares the tools its actions lead to publishes Reply shaped to them.
            reply.Function = actionableToolNames is null ? createdReply : ReplyTool.NarrowSchemaToAgent(createdReply, actionableToolNames);

            return reply;
        }

        /// <summary>The closure recorded on the session; null when none was.</summary>
        public Records.TurnReply? Recorded()
            => provider.GetVariable(session, "turn_reply") is string json ? Deserialize(json) : null;
    }

    /// <summary>The framework tier's model as the presenter meets it: it answers the welcome as given or fails when it has none.</summary>
    /// <param name="welcomeAnswer">The JSON that the model returns; null makes the call fail.</param>
    private sealed class WelcomeModel(string? welcomeAnswer) : ILLMService
    {
        /// <inheritdoc />
        public Records.LLMTier FrameworkTier => Records.LLMTier.Economy;

        /// <inheritdoc />
        public Task<T?> CompleteStructuredAsync<T>(string conversationId, string systemPrompt, string userPrompt, JsonElement? responseSchema = null)
            => welcomeAnswer is null
                ? throw new InvalidOperationException("The model is unreachable")
                : Task.FromResult(JsonSerializer.Deserialize<T>(welcomeAnswer, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        /// <inheritdoc />
        public IChatClient GetChatClient(Records.LLMTier tier) => throw new InvalidOperationException("The presenter asks for text, never a chat client");

        /// <inheritdoc />
        public bool CanForceToolCall(Records.LLMTier tier) => false;

        /// <inheritdoc />
        public Records.MagicDustPricing GetPricing(Records.LLMTier tier) => new();
    }

    /// <summary>The metadata store of a conversation on one fully capable channel, so the welcome is never degraded.</summary>
    /// <param name="channelName">The channel that every conversation is on.</param>
    private sealed class SingleChannelStore(string channelName) : IChannelMetadataStore
    {
        /// <inheritdoc />
        public Task<ChannelMetadata> RegisterChannelMetadataAsync(string conversationId, ChannelMetadata declaredChannelMetadata)
            => throw new InvalidOperationException("The presenter only reads the handshake");

        /// <inheritdoc />
        public void EvictChannelMetadata(string conversationId) { }

        /// <inheritdoc />
        public ValueTask<ChannelMetadata> GetChannelMetadataAsync(string conversationId)
            => ValueTask.FromResult(new ChannelMetadata
            {
                Coordinates = new ChannelCoordinates { ChannelName = channelName, DeliveryMode = "webhook" },
                Capabilities = ChannelCapabilities.Default
            });
    }
}
