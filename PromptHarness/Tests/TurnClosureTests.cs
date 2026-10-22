using System.Security.Cryptography;
using System.Text.Json;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;
using Morgana.AI.LanguageModels.Ollama;
using Morgana.AI.Providers;
using Morgana.AI.Services;
using Morgana.AI.Tools;
using Morgana.Contracts;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting how a turn is answered and closed: what Reply records and refuses, what the framework does
/// when the model answers in free text instead of calling it and what a transcript reads back of what the turn delivered.
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
        [new QuickReply("back_to_morgana", "✨ We're done, thanks", "We're done, thanks", true)]);

    [Theory]
    // A departing user is let go: no button and the conversation returns to Morgana, whatever else was declared.
    [InlineData("nothing", true, false, "", true)]
    [InlineData("action_choice", true, true, "", true)]
    // A typed answer is asked with no button at all, the way out included: buttons would lock the field the user types in.
    [InlineData("typed_answer", false, false, "", false)]
    [InlineData("typed_answer", false, true, "", false)]
    // Offered actions carry the way out after them.
    [InlineData("action_choice", false, true, "GetInvoices#1,back_to_morgana", false)]
    [InlineData("nothing", false, true, "GetInvoices#1,back_to_morgana", false)]
    // An answered request carries the way out and the agent stays until the user leaves.
    [InlineData("nothing", false, false, "back_to_morgana", false)]
    [InlineData("action_choice", false, false, "back_to_morgana", false)]
    public void TurnClosure_Framework_decides_the_framework_replies_from_the_closure(
        string awaits, bool userIsLeaving, bool withAction, string buttonIds, bool handsBack)
    {
        string actions = withAction ? """[{"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"}]""" : "[]";
        Records.ClosingDeclaration closingDeclaration = Deserialize(
            $$"""{"awaits":"{{awaits}}","userIsLeaving":{{(userIsLeaving ? "true" : "false")}},"actions":{{actions}},"card":null}""");

        (List<QuickReply>? quickReplies, bool handsBackConversation) = closingDeclaration.ToDelivery(FrameworkReplies);

        Assert.Equal(buttonIds, string.Join(",", quickReplies?.Select(button => button.Id) ?? []));
        Assert.Equal(handsBack, handsBackConversation);
    }

    [Fact]
    public void TurnClosure_Answered_request_with_no_authored_closure_hands_the_conversation_back()
    {
        Records.ClosingDeclaration closingDeclaration = Deserialize("""{"text":"Here you are.","awaits":"nothing","userIsLeaving":false,"actions":[],"card":null}""");

        (List<QuickReply>? quickReplies, bool handsBack) = closingDeclaration.ToDelivery(new Records.FrameworkReplies([]));

        // With no button to leave by, staying in service would trap the user with this agent.
        Assert.Null(quickReplies);
        Assert.True(handsBack);
    }

    [Fact]
    public void TurnClosure_Two_actions_on_one_tool_stay_two_buttons()
    {
        Records.ClosingDeclaration closingDeclaration = Deserialize("""
            {"text":"Here you are.","awaits":"action_choice","userIsLeaving":false,"card":null,"actions":[
              {"tool":"ConfirmOrder","label":"✅ Confirm","value":"Confirm my order"},
              {"tool":"ConfirmOrder","label":"✅ Confirm both","value":"Confirm both orders"}]}
            """);

        List<QuickReply> buttons = closingDeclaration.ToDelivery(FrameworkReplies).QuickReplies!;

        Assert.Equal(["ConfirmOrder#1", "ConfirmOrder#2", "back_to_morgana"], buttons.Select(button => button.Id));
        Assert.Equal("Confirm my order", buttons[0].Value);
    }

    [Fact]
    public void TurnClosure_Menu_replaces_the_closure_actions_and_keeps_the_card()
    {
        // A closure that carried an action and a card, as a model might write one.
        Records.ClosingDeclaration closingDeclaration = Deserialize("""
            {"text":"Here you are.","awaits":"typed_answer","userIsLeaving":false,
             "actions":[{"tool":"GetBalance","label":"Balance","value":"What is my balance?"}],
             "card":{"title":"Account","subtitle":null,"components":[{"type":"divider"}]}}
            """);
        List<Records.OfferedAction> menu =
        [
            new Records.OfferedAction("GetInvoices", "Invoices", "Show my invoices"),
            new Records.OfferedAction("OpenAccount", "Open an account", "I want to open an account")
        ];

        Records.ClosingDeclaration presented = closingDeclaration.WithMenu(menu);
        List<QuickReply> buttons = presented.ToDelivery(FrameworkReplies).QuickReplies!;

        // The two menu buttons come first and the framework's way out follows; the card that presents the turn stays.
        Assert.Equal(["GetInvoices#1", "OpenAccount#2", "back_to_morgana"], buttons.Select(button => button.Id));
        Assert.Equal(["Invoices", "Open an account", "✨ We're done, thanks"], buttons.Select(button => button.Label));
        Assert.NotNull(presented.Card);
        Assert.Equal(Records.AwaitedFromUser.ActionChoice, presented.Awaits);
    }

    [Fact]
    public void TurnClosure_Empty_menu_closes_the_turn_with_the_way_out()
    {
        Records.ClosingDeclaration closingDeclaration = Deserialize("""{"text":"Here you are.","awaits":"typed_answer","userIsLeaving":false,"actions":[],"card":null}""");

        Records.ClosingDeclaration presented = closingDeclaration.WithMenu([]);
        (List<QuickReply>? quickReplies, bool handsBack) = presented.ToDelivery(FrameworkReplies);

        // An agent with nothing to offer awaits nothing and keeps the conversation behind the way out.
        Assert.Equal(Records.AwaitedFromUser.Nothing, presented.Awaits);
        Assert.Equal(["back_to_morgana"], quickReplies!.Select(button => button.Id));
        Assert.Equal(["✨ We're done, thanks"], quickReplies!.Select(button => button.Label));
        Assert.False(handsBack);
    }

    [Fact]
    public async Task TurnClosure_Landing_offers_the_model_only_Reply()
    {
        // The model, the session and the context store that decide which tools the model is offered.
        (ScriptedChatClient model, AgentSession session, MorganaAIContextProvider provider) = await ToolNarrowingAsync();

        // The landing flag is what the agent sets before its model runs on a pressed welcome button.
        await provider.SetVariableAsync(session, "landing", true);

        // The request crosses the narrowing client the way a turn of the agent does.
        await new WorkflowToolsChatClient(model, () => session, provider, [], 3)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "What can you do with my invoices?")], AgentOptions(), TestContext.Current.CancellationToken);

        // Whatever else the agent holds stays out: the turn is the agent introducing itself and answering.
        Assert.Equal(["Reply"], model.Requests.Single().Options!.Tools!.Select(tool => tool.Name).Order());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TurnClosure_Run_instructions_reach_the_model_for_that_run_alone(bool withRunInstructions)
    {
        // A plainly built context provider: it adds whatever the session holds for the run and knows nothing of why.
        MorganaAIContextProvider provider = new MorganaAIContextProvider(NullLogger.Instance);

        // The agent answers once, whatever it is asked: the case reads the instructions of the request.
        ScriptedChatClient model = new ScriptedChatClient(Text("Welcome."));
        ChatClientAgent agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            AIContextProviders = [provider],
            ChatOptions = new ChatOptions { Instructions = "You keep the accounts book." }
        });
        AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // The agent writes these before its model runs, on a landing or on a consultation.
        if (withRunInstructions)
            await provider.SetVariableAsync(session, "run_instructions", "[THE USER HAS JUST CHOSEN YOU]");

        await agent.RunAsync("What can you do with my invoices?", session, cancellationToken: TestContext.Current.CancellationToken);

        // The instructions are read when the session holds them and the agent's own are always there.
        string instructions = model.Requests.Single().Options!.Instructions!;
        Assert.Equal(withRunInstructions, instructions.Contains("[THE USER HAS JUST CHOSEN YOU]", StringComparison.Ordinal));
        Assert.Contains("You keep the accounts book.", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnClosure_Ordinary_turn_is_offered_the_agent_s_tools()
    {
        // A session with no landing flag: the turn of a user who is already talking to the agent.
        (ScriptedChatClient model, AgentSession session, MorganaAIContextProvider provider) = await ToolNarrowingAsync();

        // The request crosses the narrowing client the way a turn of the agent does.
        await new WorkflowToolsChatClient(model, () => session, provider, [], 3)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "Show my invoices")], AgentOptions(), TestContext.Current.CancellationToken);

        // Narrowing to Reply belongs to a landing alone: any other turn keeps the domain tools and Reply.
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

    /// <summary>The options of an agent holding the base tool Reply beside one domain tool.</summary>
    private static ChatOptions AgentOptions()
        => new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(() => "closed", "Reply"),
                AIFunctionFactory.Create(() => "invoices", "GetInvoices")
            ]
        };

    [Theory]
    [InlineData("ConfirmOrder#1", "ConfirmOrder")]
    [InlineData("approve_action", null)]
    public void TurnClosure_Pressed_button_names_the_tool_its_action_leads_to(string buttonId, string? tool)
        => Assert.Equal(tool, Records.ClosingDeclaration.ActionTool(buttonId));

    [Fact]
    public async Task TurnClosure_Reply_refuses_an_action_leading_to_a_tool_the_agent_lacks()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);

        // Refused with the fact stated: what the action leads to and the tools that it could have led to.
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments("""
            {"text":"Here you are.","userIsLeaving":false,"actions":[
              {"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"},
              {"tool":"DownloadPdf","label":"⬇️ PDF","value":"Download it as PDF"}]}
            """)).AsTask());
        Assert.Equal("the action '⬇️ PDF' leads to 'DownloadPdf', which is not one of your tools: GetInvoices", refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Theory]
    [InlineData("""{"text":"Here you are.","userIsLeaving":false,"actions":[null]}""", "actions[0]")]
    [InlineData("""{"text":"Here you are.","userIsLeaving":false,"card":{"title":"t","subtitle":null,"components":[{"type":"divider"},{"type":"section","title":"s","subtitle":null,"components":[null]}]}}""", "card.components[1].components[0]")]
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
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(holdsChoiceStep: true);
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
    public async Task TurnClosure_Reply_schema_offers_actions_only_to_an_agent_with_a_choice_step()
    {
        // A choice step is where the model words the buttons that the step proposes.
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(holdsChoiceStep: true);
        Assert.True(reply.Function.JsonSchema.GetProperty("properties").TryGetProperty("actions", out _));

        // Anywhere else the buttons are the agent's menu, written by code, so the model is never asked for them.
        ReplyUnderTest menuReply = await ReplyUnderTest.CreateAsync(holdsChoiceStep: false);
        Assert.False(menuReply.Function.JsonSchema.GetProperty("properties").TryGetProperty("actions", out _));
    }

    [Fact]
    public async Task TurnClosure_Reply_schema_admits_the_card_depth_that_the_validation_accepts_and_no_deeper()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(holdsChoiceStep: false);
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
            {"text":"Here you are.","userIsLeaving":false,"card":{"title":"Deep","subtitle":null,"components":[
              {"type":"section","title":"1","subtitle":null,"components":[
                {"type":"section","title":"2","subtitle":null,"components":[{"type":"divider"}]}]}]}}
            """));
        Assert.Equal("TurnClosed", Assert.IsType<Records.FrameworkToolResult>(deepestCardResult).Name);

        // One level deeper is what the validation refuses, so the schema does not offer it.
        ReplyUnderTest replyForDeeperCard = await ReplyUnderTest.CreateAsync(actionableToolNames: ["GetInvoices"]);
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => replyForDeeperCard.Function.InvokeAsync(Arguments("""
            {"text":"Here you are.","userIsLeaving":false,"card":{"title":"Deep","subtitle":null,"components":[
              {"type":"section","title":"1","subtitle":null,"components":[
                {"type":"section","title":"2","subtitle":null,"components":[
                  {"type":"section","title":"3","subtitle":null,"components":[{"type":"divider"}]}]}]}]}}
            """)).AsTask());
        Assert.Equal("the card nests 4 levels and at most 3 are allowed", refusal.Message);
    }

    [Fact]
    public async Task TurnClosure_Reply_schema_is_a_contract_for_card()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        JsonElement properties = reply.Function.JsonSchema.GetProperty("properties");

        // The model never says what the turn awaits: the framework derives it.
        Assert.False(properties.TryGetProperty("awaits", out _));

        // Every component is one closed alternative, told apart by its type.
        string[] componentTypes = [.. properties.GetProperty("card").GetProperty("properties").GetProperty("components")
            .GetProperty("items").GetProperty("anyOf").EnumerateArray()
            .Select(alternative => alternative.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!)];
        Assert.Equal(["text_block", "key_value", "divider", "list", "section", "grid", "badge", "image"], componentTypes);

        string[] required = [.. reply.Function.JsonSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()!)];
        Assert.Equal(["text", "userIsLeaving"], required);
    }

    [Fact]
    public async Task TurnClosure_Reply_records_the_closure_and_closes_the_turn()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();

        object? result = await reply.Function.InvokeAsync(Arguments("""
            {"text":"Here you are.","userIsLeaving":false,
             "actions":[{"tool":"GetInvoices","label":"📄 Invoices","value":"Show my invoices"}],
             "card":{"components":[{"content":"Overdue","type":"text_block"}],"title":"Account","subtitle":null}}
            """));

        Assert.Equal("TurnClosed", Assert.IsType<Records.FrameworkToolResult>(result).Name);
        Records.ClosingDeclaration recorded = reply.Recorded()!;

        // Outside a choice step Reply records that nothing is awaited, whatever the actions it carries.
        Assert.Equal(Records.AwaitedFromUser.Nothing, recorded.Awaits);
        Assert.Equal("GetInvoices", Assert.Single(recorded.Actions).Tool);
        Assert.IsType<TextBlockComponent>(Assert.Single(recorded.Card!.Components));
    }

    [Fact]
    public async Task TurnClosure_Reply_refuses_a_card_nesting_deeper_than_three_levels()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();

        // Refused with the fact stated: the depth the card reaches and the limit; the tool loop hands it back to the model.
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments("""
            {"text":"Here you are.","userIsLeaving":false,"card":{"title":"Deep","subtitle":null,"components":[
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
            """{"text":"Here you are.","userIsLeaving":false,"card":{"title":"Long","subtitle":null,"components":[""" + dividers + "]}}")).AsTask());
        Assert.Equal("the card holds 51 components and at most 50 are allowed", refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task TurnClosure_Reply_at_a_choice_step_refuses_actions_other_than_the_steps_tools()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(
            actionableToolNames: ["Pay", "Postpone"], choiceStep: ("Settle", "Choose", ["Pay", "Postpone"]));
        ScriptedChatClient model = new ScriptedChatClient(
            Call("Reply", """{"text":"Here you are.","userIsLeaving":false,"actions":[{"tool":"Pay","label":"Pay now","value":"Pay now"}]}"""),
            Call("Reply", """{"text":"Here you are.","userIsLeaving":false,"actions":[{"tool":"Postpone","label":"Later","value":"Later"},{"tool":"Pay","label":"Pay now","value":"Pay now"}]}"""));

        ChatResponse turn = await RunTurnAsync(model, reply);

        // The model's own call is held to the step: refused with the fact stated and repaired on the next call.
        FunctionResultContent refusal = turn.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().First();
        Assert.Equal("the actions offered lead to [Pay] while step 'Choose' of workflow 'Settle' takes exactly one action to each of: Pay, Postpone", refusal.Exception!.Message);
        Assert.Equal(["Pay", "Postpone"], reply.Recorded()!.Actions.Select(action => action.Tool));
    }

    [Fact]
    public async Task TurnClosure_Reply_made_by_the_framework_at_a_choice_step_is_not_held_to_the_step()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(
            actionableToolNames: ["Pay", "Postpone"], choiceStep: ("Settle", "Choose", ["Pay", "Postpone"]));

        // Called outside any tool loop, as the framework closes a turn: the agent imposes the step's buttons afterwards.
        await reply.Function.InvokeAsync(Arguments("""{"text":"Which one?","userIsLeaving":false}"""));

        Assert.Equal("Which one?", reply.Recorded()!.Text);
    }

    [Fact]
    public async Task TurnClosure_Reply_with_blank_text_is_refused_and_the_turn_stays_open()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(
            Call("Reply", """{"text":"  ","userIsLeaving":true}"""),
            Call("Reply", """{"text":"Farewell: may your garden bloom.","userIsLeaving":true}"""));

        ChatResponse turn = await RunTurnAsync(model, reply);

        // The first Reply would have left the turn mute: refused with the fact and nothing recorded. The model then answered again.
        FunctionResultContent refusal = turn.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().First();
        Assert.Equal("the text is empty", refusal.Exception!.Message);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("Farewell: may your garden bloom.", reply.Recorded()!.Text);
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
            reply.Function.InvokeAsync(Arguments($$"""{"text":"Here you are.","userIsLeaving":false,"card":{{card}}}""")).AsTask());
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task TurnClosure_Turn_ending_in_free_text_is_closed_on_its_last_assistant_message()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(
            Merge(Text("Let me look."), Call("Lookup", """{}""")),
            Text("Which invoice do you mean?"));

        await RunTurnAsync(model, reply);

        // The closure is recorded by the framework from the last words alone: no further model call and nothing awaited.
        Assert.Equal(2, model.Requests.Count);
        Records.ClosingDeclaration recorded = reply.Recorded()!;
        Assert.Equal("Which invoice do you mean?", recorded.Text);
        Assert.Equal(Records.AwaitedFromUser.Nothing, recorded.Awaits);
        Assert.False(recorded.UserIsLeaving);
        Assert.Empty(recorded.Actions);
    }

    [Fact]
    public async Task TurnClosure_Free_text_written_beside_an_accepted_Reply_never_becomes_the_answer()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(
            Merge(Text("Here are your invoices, written outside Reply."),
                  Call("Reply", """{"text":"Your invoices are below.","userIsLeaving":false}""")));

        await RunTurnAsync(model, reply);

        // The answer is Reply's text alone and the turn costs no further call.
        Assert.Single(model.Requests);
        Assert.Equal("Your invoices are below.", reply.Recorded()!.Text);
    }

    [Fact]
    public async Task TurnClosure_Turn_closed_by_the_model_costs_no_further_call()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(
            Call("Reply", """{"text":"Here are your invoices.","userIsLeaving":false}"""));

        await RunTurnAsync(model, reply);

        Assert.Single(model.Requests);
        Assert.Equal(Records.AwaitedFromUser.Nothing, reply.Recorded()!.Awaits);
    }

    [Fact]
    public async Task TurnClosure_Reply_repeated_in_the_closing_response_is_answered_and_the_first_closure_stands()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(
            Merge(Merge(Text("Here are your invoices."),
                        Call("Reply", """{"text":"First answer.","userIsLeaving":false,"card":{"components":[{"content":"Overdue","type":"text_block"}],"title":"Invoices","subtitle":null}}""")),
                  Call("Reply", """{"text":"Here you are.","userIsLeaving":false}""")));

        ChatResponse turn = await RunTurnAsync(model, reply);

        // Every call of the response has its result: without one the next turn's history would be refused.
        List<AIContent> contents = [.. turn.Messages.SelectMany(message => message.Contents)];
        Assert.Equal(
            contents.OfType<FunctionCallContent>().Select(call => call.CallId).Order(),
            contents.OfType<FunctionResultContent>().Select(result => result.CallId).Order());
        Assert.Single(model.Requests);
        Assert.Equal("First answer.", reply.Recorded()!.Text);
        Assert.Equal("Invoices", reply.Recorded()!.Card!.Title);
    }

    [Fact]
    public async Task TurnClosure_Streaming_sends_exactly_one_chunk_carrying_the_Reply_text()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Merge(Text("Free words that no one reads."),
            Call("Reply", """{"text":"Your invoices are below.","userIsLeaving":false}""")));

        List<object> received = await turn.AskAsync(new Records.AgentRequest("conv-turn", "Show my invoices", null));

        // The text reaches the channel once and only as Reply gave it; every other update only keeps the agent alive.
        Assert.Equal(["Your invoices are below."], received.OfType<Records.AgentStreamChunk>().Select(chunk => chunk.Text));
        Assert.Equal("Your invoices are below.", received.OfType<Records.AgentResponse>().Single().Response);
    }

    [Fact]
    public async Task TurnClosure_Delivered_text_is_the_Reply_text_whatever_free_text_came_beside_it()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync(streaming: false);
        turn.Model.Enqueue(Merge(Text("Free words that no one reads."),
            Call("Reply", """{"text":"Your invoices are below.","userIsLeaving":false}""")));

        List<object> received = await turn.AskAsync(new Records.AgentRequest("conv-turn", "Show my invoices", null));

        // Batched, the channel gets the final response alone and its text is Reply's.
        Assert.Empty(received.OfType<Records.AgentStreamChunk>());
        Assert.Equal("Your invoices are below.", received.OfType<Records.AgentResponse>().Single().Response);
    }

    [Fact]
    public async Task TurnClosure_Approval_turn_delivers_the_held_Reply_text_with_the_approval_buttons()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(
            Call("ConfirmOrder", """{"orderId":"ORD-1","sealWord":"fern"}"""),
            Call("Reply", """{"text":"Shall I confirm ORD-1?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Order ORD-1 is confirmed.","userIsLeaving":false}""")));

        List<object> asked = await turn.AskAsync(new Records.AgentRequest("conv-turn", "Confirm ORD-1", null));

        // The approval turn delivers the held Reply's text with the framework's approval buttons and nothing else.
        Records.AgentResponse approval = asked.OfType<Records.AgentResponse>().Single();
        Assert.Equal("Shall I confirm ORD-1?", approval.Response);
        Assert.Equal(["approve_action", "reject_action", "back_to_morgana"], approval.QuickReplies!.Select(button => button.Id));
        Assert.Empty(WorkflowTests.InventoryTools.Calls);

        // The message that asked for approval is the last of the history and carries what the user was shown,
        // so no text sits between the call and the result it will receive.
        ChatMessage lastMessage = (await turn.HistoryAsync()).Last();
        Assert.Equal(ChatRole.Assistant, lastMessage.Role);
        Assert.Contains(lastMessage.Contents, content => content is ToolApprovalRequestContent);
        Assert.Equal(true, lastMessage.AdditionalProperties!["morgana:user_facing"]);
        Assert.Equal("Shall I confirm ORD-1?", lastMessage.AdditionalProperties["morgana:turn_text"]);

        // The user approves: the tool runs once and the held Reply never runs, so the answer is the one written after the result.
        List<object> approved = await turn.AskAsync(new Records.AgentRequest("conv-turn", "Go ahead", null));
        Assert.Equal("Order ORD-1 is confirmed.", approved.OfType<Records.AgentResponse>().Single().Response);
        Assert.Equal(["ConfirmOrder:ORD-1:fern"], WorkflowTests.InventoryTools.Calls);
    }

    [Fact]
    public async Task TurnClosure_Every_turn_outside_a_workflow_closes_on_the_menu_whatever_the_model_offered()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"I keep the stock.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Stock", "{}")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Which plant?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Stock", """{"item":"tulip"}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Try again later.","userIsLeaving":false,"actions":[{"tool":"Stock","label":"🔁 Retry","value":"Retry"}]}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Which colour?","userIsLeaving":false}""")));

        List<object> answered = await turn.AskAsync(new Records.AgentRequest("conv-turn", "What do you keep?", null));
        List<object> asking = await turn.AskAsync(new Records.AgentRequest("conv-turn", "I want a plant", null));
        List<object> offering = await turn.AskAsync(new Records.AgentRequest("conv-turn", "tulip", null));
        List<object> questioning = await turn.AskAsync(new Records.AgentRequest("conv-turn", "Do you have it in colours?", null));

        // An answered turn and a turn whose model wrote buttons of its own close alike: the menu in menu order, then the way out.
        string[] menuButtons = ["🌼 A plant", "📦 My orders", "✨ We're done, thanks"];
        Assert.Equal(menuButtons, answered.OfType<Records.AgentResponse>().Single().QuickReplies!.Select(button => button.Label));
        Assert.Equal(menuButtons, offering.OfType<Records.AgentResponse>().Single().QuickReplies!.Select(button => button.Label));

        // A turn that leaves a call waiting for the user to type offers no button at all, since buttons would lock the field the user types in.
        Assert.Null(asking.OfType<Records.AgentResponse>().Single().QuickReplies);

        // A model that asks a question with no call behind it has left nothing waiting, so its turn closes on the menu like any answered one.
        Assert.Equal(menuButtons, questioning.OfType<Records.AgentResponse>().Single().QuickReplies!.Select(button => button.Label));

        // The model is never asked for buttons: Reply reaches it with no actions.
        AIFunction reply = Assert.IsAssignableFrom<AIFunction>(Assert.Single(turn.Model.Requests[0].Options!.Tools!, tool => tool.Name == "Reply"));
        Assert.False(reply.JsonSchema.GetProperty("properties").TryGetProperty("actions", out _));
    }

    [Fact]
    public async Task TurnClosure_Press_of_the_button_to_leave_ends_the_stay_whatever_the_model_answered()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Which plant?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Goodbye, come back soon.","userIsLeaving":false}""")));

        await turn.AskAsync(new Records.AgentRequest("conv-turn", "I want a plant", null));
        List<object> leaving = await turn.AskAsync(new Records.AgentRequest("conv-turn", "We're done, thanks", null));

        // The model missed the goodbye, yet the press is the user leaving: no button and the conversation goes back to Morgana.
        Records.AgentResponse farewell = leaving.OfType<Records.AgentResponse>().Single();
        Assert.Null(farewell.QuickReplies);
        Assert.True(farewell.IsCompleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TurnClosure_Pressed_menu_button_makes_its_call_and_the_model_only_answers_on_its_result(bool streaming)
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync(streaming);
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"I keep the orders.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"What is your customer code?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("OrderHistory", """{"customerCode":"P994E"}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"You have 2 orders.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"I keep the stock too.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Still 2 orders.","userIsLeaving":false}""")));

        // The first press finds no customer code and the typed one completes the call, which the conversation then holds.
        await turn.AskAsync(new Records.AgentRequest("conv-turn", "What do you keep?", null));
        await turn.AskAsync(new Records.AgentRequest("conv-turn", "📦 My orders", null));
        List<object> pressAnswer = await turn.AskAsync(new Records.AgentRequest("conv-turn", "P994E", null));

        // The orders are the conversation's customer's alone, so the turn that answers the press leaves its button off the menu.
        Assert.Equal(["🌼 A plant", "✨ We're done, thanks"],
            pressAnswer.OfType<Records.AgentResponse>().Single().QuickReplies!.Select(button => button.Label));

        // An ordinary request answers no press, so it offers the button again.
        List<object> nextAnswer = await turn.AskAsync(new Records.AgentRequest("conv-turn", "What else do you keep?", null));
        Assert.Equal(["🌼 A plant", "📦 My orders", "✨ We're done, thanks"],
            nextAnswer.OfType<Records.AgentResponse>().Single().QuickReplies!.Select(button => button.Label));
        List<object> pressed = await turn.AskAsync(new Records.AgentRequest("conv-turn", "📦 My orders", null));

        // The second press made the call at once with the code the conversation holds and no model was asked whether to make it.
        Assert.Equal(["OrderHistory:P994E", "OrderHistory:P994E"], WorkflowTests.InventoryTools.Calls);
        Assert.Equal(6, turn.Model.Requests.Count);

        // The one model call of the pressed turn reads the result and has Reply alone to answer with.
        Assert.Equal(["Reply"], OfferedTools(turn.Model, 5));
        Assert.Contains(turn.Model.Requests[5].Messages.SelectMany(message => message.Contents), content => content is FunctionCallContent { Name: "OrderHistory" });
        Assert.Equal("Still 2 orders.", pressed.OfType<Records.AgentResponse>().Single().Response);
    }

    [Fact]
    public async Task TurnClosure_Press_lacking_a_value_asks_for_it_and_the_typed_answer_completes_the_call()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"I keep the stock.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Which plant?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Stock", """{"item":"tulip"}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"There are 12 tulips.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Anything else?","userIsLeaving":false}""")));

        await turn.AskAsync(new Records.AgentRequest("conv-turn", "What do you keep?", null));
        await turn.AskAsync(new Records.AgentRequest("conv-turn", "🌼 A plant", null));

        // The press carried no plant, so the tool did not run: the model read which value is lacking and had Reply alone to ask for it.
        Assert.Empty(WorkflowTests.InventoryTools.Calls);
        Assert.Equal(["Reply"], OfferedTools(turn.Model, 1));
        Assert.Contains("Stock was not run: this conversation does not hold item yet.", ResultTexts(turn.Model.Requests[1]));

        List<object> pressAnswer = await turn.AskAsync(new Records.AgentRequest("conv-turn", "tulip", null));

        // The typed answer leaves the model one move, composing the pressed call; after it, the answer.
        Assert.Equal(["Stock"], OfferedTools(turn.Model, 2));
        Assert.Equal(["Reply"], OfferedTools(turn.Model, 3));
        Assert.Equal(["Stock:tulip"], WorkflowTests.InventoryTools.Calls);

        // The plant is the user's to give, so the turn that answers the press still offers it for another plant.
        Assert.Equal(["🌼 A plant", "📦 My orders", "✨ We're done, thanks"],
            pressAnswer.OfType<Records.AgentResponse>().Single().QuickReplies!.Select(button => button.Label));

        await turn.AskAsync(new Records.AgentRequest("conv-turn", "and roses?", null));

        // The call was made, so the press is over: the next message is an ordinary request with every tool on offer.
        Assert.Contains("CancelOrder", OfferedTools(turn.Model, 4));
        Assert.Contains("Stock", OfferedTools(turn.Model, 4));
    }

    [Fact]
    public async Task TurnClosure_Press_missing_a_context_value_waits_for_the_user_to_type_it()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"I keep the orders.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"What is your customer code?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("OrderHistory", """{"customerCode":"P994E"}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"You have 2 orders.","userIsLeaving":false}""")));

        await turn.AskAsync(new Records.AgentRequest("conv-turn", "What do you keep?", null));
        await turn.AskAsync(new Records.AgentRequest("conv-turn", "📦 My orders", null));

        // The framework made the call and the conversation held no customer code: the tool did not run and the model was told which value it lacks.
        Assert.Empty(WorkflowTests.InventoryTools.Calls);
        Assert.Contains("OrderHistory was not run: this conversation does not hold customerCode yet.", ResultTexts(turn.Model.Requests[1]));

        List<object> answered = await turn.AskAsync(new Records.AgentRequest("conv-turn", "P994E", null));

        // The typed code completes the pressed call, offered alone, which the model composes from it.
        Assert.Equal(["OrderHistory"], OfferedTools(turn.Model, 2));
        Assert.Equal(["OrderHistory:P994E"], WorkflowTests.InventoryTools.Calls);
        Assert.Equal("You have 2 orders.", answered.OfType<Records.AgentResponse>().Single().Response);
    }

    [Fact]
    public async Task TurnClosure_Press_waiting_for_a_value_is_let_go_by_a_turn_that_does_not_make_it()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"I keep the stock.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Our catalogue is online.","userIsLeaving":false}""")));
        turn.Model.Enqueue(Text("Seeds are in the nursery."));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Hello again.","userIsLeaving":false}""")));

        await turn.AskAsync(new Records.AgentRequest("conv-turn", "What do you keep?", null));
        List<object> pressAnswer = await turn.AskAsync(new Records.AgentRequest("conv-turn", "🌼 A plant", null));

        // The press lacks the plant, so the framework waits for it though the model closed the turn as answered.
        Assert.Null(pressAnswer.OfType<Records.AgentResponse>().Single().QuickReplies);

        List<object> otherAnswer = await turn.AskAsync(new Records.AgentRequest("conv-turn", "Do you sell seeds?", null));

        // The user wrote no plant and the model made no call, so the waiting call is let go and the turn closes on the menu.
        Assert.Equal(["🌼 A plant", "📦 My orders", "✨ We're done, thanks"],
            otherAnswer.OfType<Records.AgentResponse>().Single().QuickReplies!.Select(button => button.Label));

        await turn.AskAsync(new Records.AgentRequest("conv-turn", "Hello", null));

        // Nothing waits any longer, so the next message is bound to no call: every tool is on offer and none ran.
        Assert.Contains("CancelOrder", OfferedTools(turn.Model, 3));
        Assert.Contains("Stock", OfferedTools(turn.Model, 3));
        Assert.Empty(WorkflowTests.InventoryTools.Calls);
    }

    [Fact]
    public async Task TurnClosure_Own_call_lacking_a_value_waits_for_the_typed_answer()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(Call("Stock", "{}")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Which plant?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Stock", """{"item":"tulip"}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"There are 12 tulips.","userIsLeaving":false}""")));

        List<object> asking = await turn.AskAsync(new Records.AgentRequest("conv-turn", "How many do you have?", null));

        // The model's own call lacked the plant, so the tool did not run and the model read which value is lacking.
        Assert.Empty(WorkflowTests.InventoryTools.Calls);
        Assert.Contains("Stock was not run: this conversation does not hold item yet.", ResultTexts(turn.Model.Requests[1]));

        // The call is left waiting, so the rest of the turn offers Reply alone and the answer carries no button.
        Assert.Equal(["Reply"], OfferedTools(turn.Model, 1));
        Assert.Null(asking.OfType<Records.AgentResponse>().Single().QuickReplies);

        List<object> answered = await turn.AskAsync(new Records.AgentRequest("conv-turn", "tulip", null));

        // The typed answer leaves the model one move, composing the waiting call; after it, the answer.
        Assert.Equal(["Stock"], OfferedTools(turn.Model, 2));
        Assert.Equal(["Reply"], OfferedTools(turn.Model, 3));
        Assert.Equal(["Stock:tulip"], WorkflowTests.InventoryTools.Calls);

        // The call was made, so the turn closes on the menu.
        Assert.Equal(["🌼 A plant", "📦 My orders", "✨ We're done, thanks"],
            answered.OfType<Records.AgentResponse>().Single().QuickReplies!.Select(button => button.Label));
    }

    [Fact]
    public async Task TurnClosure_Own_call_missing_a_context_value_waits_for_the_typed_answer()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(Call("OrderHistory", "{}")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"What is your customer code?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("OrderHistory", """{"customerCode":"P994E"}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"You have 2 orders.","userIsLeaving":false}""")));

        List<object> asking = await turn.AskAsync(new Records.AgentRequest("conv-turn", "What are my orders?", null));

        // Nobody holds the customer code, so the tool did not run and the model read which value is lacking.
        Assert.Empty(WorkflowTests.InventoryTools.Calls);
        Assert.Contains("OrderHistory was not run: this conversation does not hold customerCode yet.", ResultTexts(turn.Model.Requests[1]));

        // The call is left waiting, so the rest of the turn offers Reply alone and the answer carries no button.
        Assert.Equal(["Reply"], OfferedTools(turn.Model, 1));
        Assert.Null(asking.OfType<Records.AgentResponse>().Single().QuickReplies);

        List<object> answered = await turn.AskAsync(new Records.AgentRequest("conv-turn", "P994E", null));

        // The typed code completes the waiting call, offered alone, which the model composes from it.
        Assert.Equal(["OrderHistory"], OfferedTools(turn.Model, 2));
        Assert.Equal(["OrderHistory:P994E"], WorkflowTests.InventoryTools.Calls);
        Assert.Equal("You have 2 orders.", answered.OfType<Records.AgentResponse>().Single().Response);
    }

    [Theory]
    [InlineData("""{"plant":"rose"}""", "the action '🌹 Roses' passes 'plant' to 'Stock', which takes: item")]
    [InlineData("""{"item":12}""", "the action '🌹 Roses' passes 'item' as number while 'Stock' takes it as string")]
    public async Task TurnClosure_Reply_refuses_action_arguments_that_the_tool_cannot_take(string arguments, string reason)
    {
        Records.CallSignature stock = Records.CallSignature.Of(JsonDocument.Parse("""{"type":"object","properties":{"item":{"type":"string"}},"required":["item"]}""").RootElement);
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(actionableToolNames: ["Stock"], actionSignatures: new Dictionary<string, Records.CallSignature> { ["Stock"] = stock });

        // Refused with the fact stated, so the model repairs the action before any press could make a call that cannot run.
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() => reply.Function.InvokeAsync(Arguments($$"""
            {"text":"We grow roses.","userIsLeaving":false,"actions":[
              {"tool":"Stock","label":"🌹 Roses","value":"How many roses are there?","arguments":{{arguments}}}]}
            """)).AsTask());
        Assert.Equal(reason, refusal.Message);
        Assert.Null(reply.Recorded());
    }

    [Fact]
    public async Task TurnClosure_Reply_schema_offers_arguments_on_every_action()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync(holdsChoiceStep: true);

        // The arguments of an action are where a button carries the values that its call needs.
        JsonElement actionProperties = reply.Function.JsonSchema.GetProperty("properties").GetProperty("actions").GetProperty("items").GetProperty("properties");
        Assert.True(actionProperties.TryGetProperty("arguments", out JsonElement argumentsSchema));
        Assert.Contains("parameter names", argumentsSchema.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    /// <summary>The sample tools with a menu of two entries: a plant's stock, which the user types, and the orders, which the conversation holds.</summary>
    private static Records.ToolDefinition[] MenuTools() =>
    [
        .. WorkflowTests.DeclaredTools().Select(tool => tool.Name switch
        {
            "Stock" => tool with { MenuEntry = new Records.MenuEntry(1, "🌼 A plant") },
            "OrderHistory" => tool with { MenuEntry = new Records.MenuEntry(2, "📦 My orders") },
            _ => tool
        })
    ];

    /// <summary>The names of the tools that the model was offered on one of its requests, in alphabetical order.</summary>
    private static string[] OfferedTools(ScriptedChatClient model, int request)
        => [.. model.Requests[request].Options!.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal)];

    /// <summary>The texts of the tool results that one request handed the model.</summary>
    private static string[] ResultTexts(ModelRequest request)
        => [.. request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => result.Result is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString()! : result.Result?.ToString() ?? string.Empty)];

    [Fact]
    public async Task TurnClosure_Consultation_answers_with_the_Reply_text_and_not_the_free_text()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Merge(Text("Free words that no one reads."),
            Call("Reply", """{"text":"ORD-1 is in stock.","userIsLeaving":false}""")));

        List<object> received = await turn.AskAsync(new Records.PeerConsultation("conv-turn", "billing", "Is ORD-1 in stock?", null));

        // What travels back to the asking colleague is the answer that Reply gave.
        Assert.Equal("ORD-1 is in stock.", received.OfType<Records.PeerConsultationResponse>().Single().Answer);
    }

    [Fact]
    public async Task TurnClosure_Approved_call_leaves_the_delivery_marks_on_the_approval_request_and_the_final_reply_only()
    {
        await using AgentTurnUnderTest turn = await AgentTurnUnderTest.CreateAsync();
        turn.Model.Enqueue(Answer(
            Call("ConfirmOrder", """{"orderId":"ORD-1","sealWord":"fern"}"""),
            Call("Reply", """{"text":"Shall I confirm ORD-1?","userIsLeaving":false}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"Order ORD-1 is confirmed.","userIsLeaving":false}""")));

        await turn.AskAsync(new Records.AgentRequest("conv-turn", "Confirm ORD-1", null));
        await turn.AskAsync(new Records.AgentRequest("conv-turn", "Go ahead", null));

        // Whatever copy of the approval request the tool loop re-emitted on approval, only two messages tell the transcript what was shown.
        List<ChatMessage> history = await turn.HistoryAsync();
        List<ChatMessage> userFacing = [.. history.Where(message => message.Role == ChatRole.Assistant
                                                                    && message.AdditionalProperties?.ContainsKey("morgana:user_facing") == true)];
        Assert.Equal(2, userFacing.Count);
        Assert.Contains(userFacing[0].Contents, content => content is ToolApprovalRequestContent);
        Assert.Equal("Shall I confirm ORD-1?", userFacing[0].AdditionalProperties!["morgana:turn_text"]);
        Assert.Contains(userFacing[1].Contents, content => content is FunctionCallContent { Name: "Reply" });
        Assert.Equal("Order ORD-1 is confirmed.", userFacing[1].AdditionalProperties!["morgana:turn_text"]);

        // No other message carries a turn text, so a reload shows each answer once.
        Assert.Equal(2, history.Count(message => message.AdditionalProperties?.ContainsKey("morgana:turn_text") == true));
    }

    [Fact]
    public async Task TurnClosure_Turn_with_no_text_is_left_to_the_agent_to_run_again()
    {
        ReplyUnderTest reply = await ReplyUnderTest.CreateAsync();
        ScriptedChatClient model = new ScriptedChatClient(Text(""));

        await RunTurnAsync(model, reply);

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
        Records.PresentationResult welcome = await PresentAsync("welcome-written", """{"message":"Welcome to the greenhouse.","reintroduction":"This is the greenhouse."}""");

        // Both texts are the model's, the second one served to a request that no agent claims.
        Assert.Equal("Welcome to the greenhouse.", welcome.Message);
        Assert.Equal("This is the greenhouse.", welcome.Reintroduction);

        // One button per intent in configuration order: the id is the intent, the value is what a press sends.
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
    private static Records.ClosingDeclaration Deserialize(string json)
        => JsonSerializer.Deserialize<Records.ClosingDeclaration>(json, new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true })!;

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

    /// <summary>One model answer made of the given calls.</summary>
    private static ChatResponse Answer(params ChatResponse[] calls)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. calls.SelectMany(call => call.Messages[0].Contents)]));

    /// <summary>One model answer writing text and then calling a tool, as a closing turn does.</summary>
    private static ChatResponse Merge(ChatResponse text, ChatResponse call)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. text.Messages[0].Contents, .. call.Messages[0].Contents]));

    /// <summary>
    /// Runs one turn through the chain that an agent runs on: the tool loop, then the turn-closing client.
    /// </summary>
    private static async Task<ChatResponse> RunTurnAsync(ScriptedChatClient model, ReplyUnderTest reply)
    {
        TurnClosingChatClient chain = new TurnClosingChatClient(
            new FunctionInvokingChatClient(model),
            "Turn closed.",
            NullLogger.Instance);

        return await chain.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Show me an invoice")],
            new ChatOptions { Instructions = "You are a billing assistant.", Tools = [reply.Function, AIFunctionFactory.Create(() => "found", "Lookup")] });
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
        /// <param name="actionSignatures">What a call of each actionable tool may carry; null takes every action's arguments as written.</param>
        /// <param name="holdsChoiceStep">Whether the agent's workflows hold a choice step; null publishes Reply unshaped.</param>
        public static async Task<ReplyUnderTest> CreateAsync(
            IReadOnlyCollection<string>? actionableToolNames = null,
            (string Workflow, string Step, IReadOnlyList<string> Tools)? choiceStep = null,
            IReadOnlyDictionary<string, Records.CallSignature>? actionSignatures = null,
            bool? holdsChoiceStep = null)
        {
            ReplyUnderTest reply = new ReplyUnderTest();
            reply.session = await new ChatClientAgent(new ScriptedChatClient()).CreateSessionAsync();

            Records.ToolDefinition definition = new Records.ToolDefinition("Reply", "Gives your turn's answer.",
            [
                new Records.ToolParameter("text", "The words of the answer.", true, ""),
                new Records.ToolParameter("userIsLeaving", "Whether the user is leaving.", true, ""),
                new Records.ToolParameter("actions", "The actions offered.", false, ""),
                new Records.ToolParameter("card", "The card.", false, "")
            ]);

            ReplyTool baseTool = new ReplyTool(NullLogger.Instance,
                () => new MorganaTool.ToolContext(reply.provider, reply.session, "turn-closure", actionableToolNames, choiceStep, actionSignatures));
            MorganaToolAdapter adapter = new MorganaToolAdapter(NullLogger.Instance,
                () => new MorganaTool.ToolContext(reply.provider, reply.session, "turn-closure"));

            Func<string, bool, List<Records.OfferedAction>?, RichCard?, Task<Records.FrameworkToolResult>> implementation = baseTool.Reply;
            adapter.AddTool(definition.Name, implementation, definition);
            AIFunction createdReply = await adapter.CreateFunctionAsync(definition.Name);

            // An agent publishes Reply shaped to its workflows: actions exist only where a choice step proposes them.
            reply.Function = holdsChoiceStep is { } choiceStepHeld ? ReplyTool.NarrowSchemaToAgent(createdReply, choiceStepHeld) : createdReply;

            return reply;
        }

        /// <summary>The closure recorded on the session; null when none was.</summary>
        public Records.ClosingDeclaration? Recorded()
            => provider.GetVariable(session, "closing_declaration") is string json ? Deserialize(json) : null;
    }

    /// <summary>The agent that the turn cases run: the sample inventory agent over the scripted model.</summary>
    [HandlesIntent("inventory")]
    [RequiresLLMTier(Records.LLMTier.Efficiency)]
    private sealed class ScriptedAgent : MorganaAgent
    {
        /// <summary>
        /// Builds the agent the way a plugin does: its model, context store and history come from the adapter.
        /// </summary>
        public ScriptedAgent(
            string conversationId,
            ILanguageModelService llmService,
            IPromptResolverService promptResolverService,
            IConversationPersistenceService conversationPersistenceService,
            MorganaAgentAdapter morganaAgentAdapter,
            IConfiguration configuration) : base(conversationId, llmService, promptResolverService, conversationPersistenceService, NullLogger.Instance, configuration)
        {
            (aiAgent, aiContextProvider, aiChatHistoryProvider)
                = morganaAgentAdapter.CreateAgent(GetType(), conversationId, () => CurrentSession, OnSharedContextUpdate);

            // The case reads the history through the agent's own mailbox, so it sees it between turns and never during one.
            Receive<ReadHistory>(_ => Sender.Tell(aiChatHistoryProvider.GetMessages(CurrentSession!).ToList()));
        }
    }

    /// <summary>Asks the scripted agent for the messages of its session.</summary>
    private sealed record ReadHistory;

    /// <summary>
    /// The sample inventory agent running as an actor over a scripted model, with a record on disk,
    /// so a whole turn is delivered the way a channel receives it.
    /// </summary>
    internal sealed class AgentTurnUnderTest : IAsyncDisposable
    {
        /// <summary>The script standing where the model would.</summary>
        public ScriptedChatClient Model { get; }

        /// <summary>The actor system that the agent lives in.</summary>
        private readonly ActorSystem system;

        /// <summary>The agent under test.</summary>
        private readonly IActorRef agent;

        /// <summary>Where the conversation's record is kept, removed with the case.</summary>
        private readonly string storagePath;

        /// <summary>
        /// Holds the parts of a running agent.
        /// </summary>
        private AgentTurnUnderTest(ScriptedChatClient model, ActorSystem system, IActorRef agent, string storagePath)
        {
            Model = model;
            this.system = system;
            this.agent = agent;
            this.storagePath = storagePath;
        }

        /// <summary>
        /// Starts the agent over a record in a throwaway folder.
        /// </summary>
        /// <param name="streaming">Whether the turn streams its answer to the channel.</param>
        public static async Task<AgentTurnUnderTest> CreateAsync(bool streaming = true)
        {
            WorkflowTests.InventoryTools.Calls.Clear();
            ScriptedChatClient model = new ScriptedChatClient();
            string storagePath = Path.Combine(Path.GetTempPath(), $"turn-agent-{Guid.NewGuid():N}");

            SQLiteConversationPersistenceService persistence = new SQLiteConversationPersistenceService(
                Options.Create(new Records.ConversationPersistenceOptions
                {
                    StoragePath = storagePath,
                    EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                }),
                NullLogger.Instance);
            await persistence.EnsureDatabaseInitializedAsync("conv-turn");

            Records.Prompt prompt = new("inventory", "Sell plants.", "Answer plainly.", "Plain text.", null, null, "en-US", "1");
            ConfigurationPromptResolverService resolver = new ConfigurationPromptResolverService(new ScriptedDomain(prompt));
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Morgana:HistoryReducer:Enabled"] = "false",
                    ["Morgana:AgentToAgent:Enabled"] = "false",
                    ["Morgana:AdaptiveMessaging:EnableStreamingResponse"] = streaming ? "true" : "false"
                })
                .Build();
            WorkflowTests.SampleLlm llm = new WorkflowTests.SampleLlm(model);

            MorganaAgentAdapter adapter = new MorganaAgentAdapter(
                llm,
                resolver,
                new ConfigurationPromptComposerService(resolver),
                new WorkflowTests.SampleToolRegistry([], MenuTools()),
                new MCPClientRegistryService(NullLogger.Instance),
                new HistoryReducerService(configuration, NullLogger.Instance),
                new WorkflowTests.NoDust(),
                null!,
                new SchemaCallSignatureService(),
                configuration,
                NullLogger.Instance);

            ActorSystem system = ActorSystem.Create($"turn-closure-{Guid.NewGuid():N}");
            IActorRef agent = system.ActorOf(Props.Create(() => new ScriptedAgent("conv-turn", llm, resolver, persistence, adapter, configuration)));

            return new AgentTurnUnderTest(model, system, agent, storagePath);
        }

        /// <summary>
        /// Sends the agent one message and gathers everything it sends back up to its answer.
        /// </summary>
        /// <param name="message">The user's turn or a colleague's question.</param>
        public async Task<List<object>> AskAsync(object message)
        {
            Inbox inbox = Inbox.Create(system);
            inbox.Send(agent, message);

            // Chunks and keep-alives arrive first; the answer ends the turn.
            List<object> received = [];
            while (true)
            {
                object update = await inbox.ReceiveAsync(TimeSpan.FromSeconds(30));
                received.Add(update);
                if (update is Records.AgentResponse or Records.PeerConsultationResponse)
                    return received;
            }
        }

        /// <summary>The messages the agent's session holds, as the next turn would hand them to the model.</summary>
        public async Task<List<ChatMessage>> HistoryAsync()
        {
            Inbox inbox = Inbox.Create(system);
            inbox.Send(agent, new ReadHistory());
            return (List<ChatMessage>)await inbox.ReceiveAsync(TimeSpan.FromSeconds(30));
        }

        /// <summary>Stops the actors and removes the record.</summary>
        public async ValueTask DisposeAsync()
        {
            await system.Terminate();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, recursive: true);
        }
    }

    /// <summary>The framework tier's model as the presenter meets it: it answers the welcome as given or fails when it has none.</summary>
    /// <param name="welcomeAnswer">The JSON that the model returns; null makes the call fail.</param>
    private sealed class WelcomeModel(string? welcomeAnswer) : ILanguageModelService
    {
        /// <inheritdoc />
        public Records.LLMTier FrameworkTier => Records.LLMTier.Economy;

        /// <inheritdoc />
        public Task<T?> CompleteStructuredAsync<T>(string conversationId, string systemPrompt, string userPrompt, JsonElement? responseSchema = null, IReadOnlyList<ChatMessage>? precedingMessages = null)
            => welcomeAnswer is null
                ? throw new InvalidOperationException("The model is unreachable")
                : Task.FromResult(JsonSerializer.Deserialize<T>(welcomeAnswer, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        /// <inheritdoc />
        public IChatClient GetChatClient(Records.LLMTier tier) => throw new InvalidOperationException("The presenter asks for text, never a chat client");

        /// <inheritdoc />
        public Records.MagicDustPricing GetPricing(Records.LLMTier tier) => new();

        // A provider that never reads a failure as a content refusal, so every fault of the double takes the general path.
        public MorganaLanguageModel GetLanguageModel(Records.LLMTier tier) => new OllamaLanguageModel();
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
