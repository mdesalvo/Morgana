using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.AI.Services;
using Morgana.AI.Workflows;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting how a workflow keeps its steps in order: the engine's transitions and bound values,
/// the tools that each step offers, the guard against a tool called out of turn, a workflow resumed from a
/// saved session and every refusal of the startup check.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: a scripted client stands where the model would, on the real adapter, the
/// real tool loop and the real engine. What the model does with a workflow is the business of the scenarios;
/// what is asserted here is what no prompt can bend.</para>
///
/// <para>The turn-end drop of the position on <c>userIsLeaving</c> lives in <c>MorganaAgent</c>, an Akka actor
/// that this group does not boot: what is asserted is the provider operation that the actor calls.</para>
/// </remarks>
public sealed class WorkflowTests
{
    // =========================================================================
    // THE ENGINE
    // =========================================================================

    [Fact]
    public async Task Launch_stops_at_the_first_step_with_nothing_bound()
    {
        Records.WorkflowPosition position = await new WorkflowEngine([PlaceOrder()]).LaunchAsync("PlaceOrder");

        Assert.Equal(("PlaceOrder", "Quote"), (position.Workflow, position.Step));
        Assert.Empty(position.Arguments);
        Assert.False(string.IsNullOrEmpty(position.Checkpoint));
    }

    [Fact]
    public async Task A_successful_outcome_leads_to_the_next_step_with_its_values_bound()
    {
        WorkflowEngine engine = new WorkflowEngine([PlaceOrder()]);
        Records.WorkflowPosition quote = await engine.LaunchAsync("PlaceOrder");

        Records.WorkflowPosition? decide = await engine.AdvanceAsync(quote, Succeeded("CreatePurchaseOrder", """{"orderId":"ORD-1","sealWord":"fern"}"""));

        Assert.Equal("Decide", decide!.Step);
        Assert.Equal("\"ORD-1\"", decide.Arguments["OrderId"]);
        Assert.Equal("\"fern\"", decide.Arguments["SealWord"]);
    }

    [Fact]
    public async Task A_failed_outcome_follows_the_failure_link()
    {
        WorkflowEngine engine = new WorkflowEngine([PlaceOrder()]);
        Records.WorkflowPosition decide = await ReachDecideAsync(engine);

        Records.WorkflowPosition? back = await engine.AdvanceAsync(decide, new Records.StepOutcome("ConfirmOrder", true, """{"error":"declined"}"""));

        Assert.Equal("Quote", back!.Step);
        Assert.Empty(back.Arguments);
    }

    [Fact]
    public async Task A_failure_with_no_link_ends_the_workflow()
    {
        WorkflowEngine engine = new WorkflowEngine([PlaceOrder()]);
        Records.WorkflowPosition quote = await engine.LaunchAsync("PlaceOrder");

        Assert.Null(await engine.AdvanceAsync(quote, new Records.StepOutcome("CreatePurchaseOrder", true, """{"error":"no such plant"}""")));
    }

    [Fact]
    public async Task A_step_with_no_edge_for_an_outcome_ends_the_workflow()
    {
        WorkflowEngine engine = new WorkflowEngine([PlaceOrder()]);
        Records.WorkflowPosition decide = await ReachDecideAsync(engine);

        // Decide has an edge for a failed confirmation only: a cancellation or a confirmation that went well leads nowhere.
        Assert.Null(await engine.AdvanceAsync(decide, Succeeded("CancelOrder", """{"status":"cancelled"}""")));
        Assert.Null(await engine.AdvanceAsync(decide, Succeeded("ConfirmOrder", """{"status":"confirmed"}""")));
    }

    [Fact]
    public async Task A_second_pass_through_a_step_binds_the_new_result()
    {
        WorkflowEngine engine = new WorkflowEngine([PlaceOrder()]);
        Records.WorkflowPosition decide = await ReachDecideAsync(engine);
        Records.WorkflowPosition quote = (await engine.AdvanceAsync(decide, new Records.StepOutcome("ConfirmOrder", true, """{"error":"declined"}""")))!;

        Records.WorkflowPosition? again = await engine.AdvanceAsync(quote, Succeeded("CreatePurchaseOrder", """{"orderId":"ORD-2","sealWord":"moss"}"""));

        Assert.Equal("\"ORD-2\"", again!.Arguments["OrderId"]);
    }

    [Fact]
    public async Task A_carried_value_is_read_from_the_result_exactly_as_the_result_wrote_it()
    {
        WorkflowEngine engine = new WorkflowEngine([Settle()]);
        Records.WorkflowPosition quote = await engine.LaunchAsync("Settle");

        Records.WorkflowPosition? pay = await engine.AdvanceAsync(quote, Succeeded("CreatePurchaseOrder", """{"orderId":"ORD-9","total":12.5}"""));

        // The field is read by the property's name whatever the record cased it as, numbers staying numbers.
        Assert.Equal("Pay", pay!.Step);
        Assert.Equal("\"ORD-9\"", pay.Arguments["OrderId"]);
        Assert.Equal("12.5", pay.Arguments["Total"]);
    }

    [Fact]
    public async Task A_carried_property_that_the_result_lacks_is_left_unbound()
    {
        WorkflowEngine engine = new WorkflowEngine([PlaceOrder()]);
        Records.WorkflowPosition quote = await engine.LaunchAsync("PlaceOrder");

        Records.WorkflowPosition? decide = await engine.AdvanceAsync(quote, Succeeded("CreatePurchaseOrder", """{"orderId":"ORD-1","sealWord":null}"""));

        Assert.Equal(["OrderId"], decide!.Arguments.Keys);
    }

    [Fact]
    public void A_field_is_read_whatever_its_casing_and_null_holds_nothing()
    {
        Assert.Equal("\"ORD-1\"", WorkflowEngine.ReadField("""{"OrderId":"ORD-1"}""", "orderId"));
        Assert.Null(WorkflowEngine.ReadField("""{"error":null}""", "error"));
        Assert.Null(WorkflowEngine.ReadField("""{"orderId":"ORD-1"}""", "error"));
        Assert.Null(WorkflowEngine.ReadField("not json", "error"));
    }

    [Fact]
    public async Task A_workflow_resumes_from_the_position_as_it_was_saved_with_a_fresh_engine()
    {
        Records.WorkflowPosition quote = await new WorkflowEngine([PlaceOrder()]).LaunchAsync("PlaceOrder");

        // The position is all that survives a turn or a restart: a copy read back from its text serves as well.
        Records.WorkflowPosition restored = JsonSerializer.Deserialize<Records.WorkflowPosition>(JsonSerializer.Serialize(quote))!;
        Records.WorkflowPosition? decide = await new WorkflowEngine([PlaceOrder()])
            .AdvanceAsync(restored, Succeeded("CreatePurchaseOrder", """{"orderId":"ORD-1","sealWord":"fern"}"""));

        Assert.Equal("Decide", decide!.Step);
    }

    [Fact]
    public void Dropping_the_position_leaves_no_workflow_running()
    {
        MorganaAIContextProvider provider = new MorganaAIContextProvider(NullLogger.Instance);
        AgentSession session = new ChatClientAgent(new SilentModel()).CreateSessionAsync().AsTask().GetAwaiter().GetResult();
        provider.SetWorkflowPosition(session, new Records.WorkflowPosition("PlaceOrder", "Quote", new Dictionary<string, string>(), "{}"));
        Assert.NotNull(provider.GetWorkflowPosition(session));

        provider.DropWorkflowPosition(session);

        Assert.Null(provider.GetWorkflowPosition(session));
    }

    // =========================================================================
    // THE AGENT OVER THE ENGINE
    // =========================================================================

    [Fact]
    public async Task LaunchWorkflow_is_one_required_enum_of_the_workflows_each_described()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder(), Settle());
        ScriptedModel model = agent.Model;
        model.Enqueue(Closing("Hello."));

        await agent.TurnAsync("hi");

        AIFunction launch = Assert.IsAssignableFrom<AIFunction>(Assert.Single(model.ToolsPerCall[0], tool => tool.Name == "LaunchWorkflow"));
        JsonElement schema = launch.JsonSchema;

        Assert.Equal(["workflow"], schema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["workflow"], schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        JsonElement workflow = schema.GetProperty("properties").GetProperty("workflow");
        Assert.Equal(["PlaceOrder", "Settle"], workflow.GetProperty("enum").EnumerateArray().Select(name => name.GetString()));

        // The parameter's authored text first and then each workflow beside its own description.
        string description = workflow.GetProperty("description").GetString()!;
        Assert.StartsWith("The workflow to start.", description, StringComparison.Ordinal);
        Assert.Contains("PlaceOrder: Placing an order.", description, StringComparison.Ordinal);
        Assert.Contains("Settle: Settling an order.", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_declaring_no_workflow_is_never_offered_LaunchWorkflow()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync();
        agent.Model.Enqueue(Closing("Hello."));

        await agent.TurnAsync("hi");

        Assert.DoesNotContain(agent.Model.ToolsPerCall[0], tool => tool.Name == "LaunchWorkflow");
        Assert.Contains(agent.Model.ToolsPerCall[0], tool => tool.Name == "CreatePurchaseOrder");
    }

    [Fact]
    public async Task Outside_a_workflow_every_tool_is_offered_and_a_consultation_is_never_offered_the_launch()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        agent.Model.Enqueue(Closing("Hello."));
        await agent.TurnAsync("hi");

        Assert.Equal(
            ["CancelOrder", "ConfirmOrder", "CreatePurchaseOrder", "LaunchWorkflow", "Refund", "Reply", "Stock"],
            Names(agent.Model.ToolsPerCall[0]));

        AgentUnderTest serving = await AgentUnderTest.CreateAsync(PlaceOrder());
        await serving.Provider.SetVariableAsync(serving.Session, Constants.ContextKeys.ServingConsultation, true);
        serving.Model.Enqueue(Closing("Hello."));
        await serving.TurnAsync("a colleague's question");

        string[] offered = [.. serving.Model.ToolsPerCall[0].Select(tool => tool.Name)];
        Assert.DoesNotContain("LaunchWorkflow", offered);
        Assert.Contains("CreatePurchaseOrder", offered);
    }

    [Fact]
    public async Task Inside_a_workflow_only_the_current_step_is_offered_and_it_changes_within_the_turn()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync();

        // Launch call: everything. Quote step: its tool, the private Stock and Reply. Decide step: its two tools.
        string[] atLaunch = Names(agent.Model.ToolsPerCall[0]);
        string[] atQuote = Names(agent.Model.ToolsPerCall[1]);
        string[] atDecide = Names(agent.Model.ToolsPerCall[2]);

        Assert.Contains("LaunchWorkflow", atLaunch);
        Assert.Equal(["CreatePurchaseOrder", "Reply", "Stock"], atQuote);
        Assert.Equal(["CancelOrder", "ConfirmOrder", "Reply", "Stock"], atDecide);
    }

    [Fact]
    public async Task A_private_tool_needing_approval_is_hidden_inside_a_workflow()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync();

        // Refund outside the workflow's signature is binding: inside the workflow it would be a second one.
        Assert.Contains("Refund", Names(agent.Model.ToolsPerCall[0]));
        Assert.DoesNotContain("Refund", Names(agent.Model.ToolsPerCall[1]));
        Assert.DoesNotContain("Refund", Names(agent.Model.ToolsPerCall[2]));
    }

    [Fact]
    public async Task A_bound_parameter_is_taken_out_of_the_schema_of_the_tool_that_the_step_offers()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync();

        AITool confirm = Assert.Single(agent.Model.ToolsPerCall[2], tool => tool.Name == "ConfirmOrder");
        AITool cancel = Assert.Single(agent.Model.ToolsPerCall[2], tool => tool.Name == "CancelOrder");

        // The approval wrapper survives the rewrite: the client below recognizes the tool by it.
        Assert.IsType<ApprovalRequiredAIFunction>(confirm);
        Assert.Empty(((AIFunction)confirm).JsonSchema.GetProperty("properties").EnumerateObject());
        Assert.Empty(((AIFunction)confirm).JsonSchema.GetProperty("required").EnumerateArray());
        Assert.Empty(((AIFunction)cancel).JsonSchema.GetProperty("properties").EnumerateObject());

        // A tool of the step that binds nothing keeps its schema.
        AITool quoteTool = Assert.Single(agent.Model.ToolsPerCall[1], tool => tool.Name == "CreatePurchaseOrder");
        Assert.Equal(["item"], ((AIFunction)quoteTool).JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task The_result_of_a_step_tool_reaches_the_model_under_the_label_of_where_the_workflow_stands()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync();

        string[] results = agent.FunctionResults();

        Assert.Contains("PlaceOrder has started at its step Quote.", results);
        Assert.Contains(results, result => result.StartsWith("[WORKFLOW PlaceOrder — NOW AT ITS STEP Decide]\n{", StringComparison.Ordinal) && result.Contains("ORD-1", StringComparison.Ordinal));
        Assert.Equal("Decide", agent.Provider.GetWorkflowPosition(agent.Session)!.Step);
    }

    [Fact]
    public async Task Reply_offers_actions_that_lead_to_the_tools_of_the_current_step_only()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync();

        // Stock is the agent's own tool but not the step's: pressing it would lead nowhere that the workflow allows.
        Assert.Equal(["ConfirmOrder", "CancelOrder"], ClosureOf(agent).Actions.Select(action => action.Tool));
    }

    [Fact]
    public async Task Reply_is_presented_at_a_choice_step_with_the_actions_required_and_bound_to_the_step_tools()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync();

        JsonElement atLaunch = ReplySchema(agent.Model.ToolsPerCall[0]);
        JsonElement atQuote = ReplySchema(agent.Model.ToolsPerCall[1]);
        JsonElement atDecide = ReplySchema(agent.Model.ToolsPerCall[2]);

        // Outside a choice step the schema is the one Reply was built with: the actions stay optional and free.
        Assert.Equal(atLaunch.GetRawText(), atQuote.GetRawText());
        Assert.DoesNotContain("actions", atQuote.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.False(atQuote.GetProperty("properties").GetProperty("actions").TryGetProperty("minItems", out _));

        JsonElement actions = atDecide.GetProperty("properties").GetProperty("actions");
        Assert.Contains("actions", atDecide.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal("array", actions.GetProperty("type").GetString());
        Assert.Equal((2, 2), (actions.GetProperty("minItems").GetInt32(), actions.GetProperty("maxItems").GetInt32()));
        Assert.Equal(["ConfirmOrder", "CancelOrder"], actions.GetProperty("items").GetProperty("properties").GetProperty("tool").GetProperty("enum").EnumerateArray().Select(tool => tool.GetString()));
    }

    [Theory]
    [InlineData("""{"awaits":"action_choice","userIsLeaving":false,"actions":[{"tool":"ConfirmOrder","label":"Confirm","value":"confirm"}]}""")]
    [InlineData("""{"awaits":"action_choice","userIsLeaving":false,"actions":[{"tool":"ConfirmOrder","label":"Confirm","value":"confirm"},{"tool":"CancelOrder","label":"Cancel","value":"cancel"},{"tool":"Stock","label":"Stock","value":"stock"}]}""")]
    [InlineData("""{"awaits":"action_choice","userIsLeaving":false,"actions":[{"tool":"ConfirmOrder","label":"Confirm","value":"confirm"},{"tool":"ConfirmOrder","label":"Again","value":"again"}]}""")]
    [InlineData("""{"awaits":"nothing","userIsLeaving":false}""")]
    public async Task Reply_at_a_choice_step_is_refused_unless_it_offers_each_step_tool_once(string refused)
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync(refused, ExactReply);

        // The refusal names the step and its tools. The repaired closure is the one that was recorded.
        Assert.Contains(agent.FunctionResults(), result => result.Contains("At step Decide of PlaceOrder the actions are exactly ConfirmOrder, CancelOrder, one each.", StringComparison.Ordinal));
        Assert.Equal(["ConfirmOrder", "CancelOrder"], ClosureOf(agent).Actions.Select(action => action.Tool));
    }

    [Fact]
    public async Task Reply_at_a_choice_step_records_the_actions_in_the_step_order_awaiting_an_action_choice()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync("""{"awaits":"typed_answer","userIsLeaving":false,"actions":[{"tool":"CancelOrder","label":"Drop it","value":"drop"},{"tool":"ConfirmOrder","label":"Take it","value":"take"}]}""");

        Records.TurnReply closure = ClosureOf(agent);

        Assert.DoesNotContain(agent.FunctionResults(), result => result.Contains("one each", StringComparison.Ordinal));
        Assert.Equal(Records.AwaitedFromUser.ActionChoice, closure.Awaits);
        Assert.Equal([("ConfirmOrder", "Take it"), ("CancelOrder", "Drop it")], closure.Actions.Select(action => (action.Tool, action.Label)));
    }

    [Fact]
    public async Task A_user_who_is_leaving_is_not_held_to_the_step_actions()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync("""{"awaits":"nothing","userIsLeaving":true}""");

        Assert.True(ClosureOf(agent).UserIsLeaving);
        Assert.DoesNotContain(agent.FunctionResults(), result => result.Contains("one each", StringComparison.Ordinal));
    }

    [Fact]
    public void A_closure_is_completed_with_a_button_for_each_missing_step_tool_worded_from_its_name()
    {
        Records.TurnReply closed = new(Records.AwaitedFromUser.Nothing, false, [], null);

        Records.TurnReply completed = closed.WithStepActions(["ConfirmOrder", "CancelOrder"]);

        Assert.Equal(Records.AwaitedFromUser.ActionChoice, completed.Awaits);
        Assert.Equal([("ConfirmOrder", "Confirm order", "Confirm order"), ("CancelOrder", "Cancel order", "Cancel order")],
            completed.Actions.Select(action => (action.Tool, action.Label, action.Value)));
    }

    [Fact]
    public void A_closure_keeps_the_model_wording_drops_foreign_actions_and_orders_the_rest_as_declared()
    {
        Records.TurnReply closed = new(Records.AwaitedFromUser.TypedAnswer, false,
        [
            new("Stock", "Stock", "stock"),
            new("CancelOrder", "Drop it", "drop"),
            new("CancelOrder", "Drop it again", "drop again")
        ], null);

        Records.TurnReply completed = closed.WithStepActions(["ConfirmOrder", "CancelOrder"]);

        // Cancel is the model's first one; Confirm is derived; Stock leads outside the step.
        Assert.Equal([("ConfirmOrder", "Confirm order"), ("CancelOrder", "Drop it")], completed.Actions.Select(action => (action.Tool, action.Label)));
        Assert.Equal(Records.AwaitedFromUser.ActionChoice, completed.Awaits);
    }

    [Fact]
    public async Task A_tool_of_the_workflow_called_out_of_turn_is_not_run_and_is_told_so()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        agent.Model.Enqueue([new TextContent("Starting."), Call("LaunchWorkflow", """{"workflow":"PlaceOrder"}""")]);
        agent.Model.Enqueue([Call("CancelOrder", """{"orderId":"ORD-1"}""")]);
        agent.Model.Enqueue([Call("LaunchWorkflow", """{"workflow":"PlaceOrder"}""")]);
        agent.Model.Enqueue(Closing("Which plant?"));

        await agent.TurnAsync("I want a rose");

        string[] results = agent.FunctionResults();
        Assert.Contains("CancelOrder is not available at this step of PlaceOrder.", results);
        Assert.Contains("LaunchWorkflow is not available at this step of PlaceOrder.", results);
        Assert.DoesNotContain(InventoryTools.Calls, call => call.StartsWith("CancelOrder", StringComparison.Ordinal));
        Assert.Equal("Quote", agent.Provider.GetWorkflowPosition(agent.Session)!.Step);
    }

    [Fact]
    public async Task A_tool_outside_the_workflow_runs_untouched_and_advances_nothing()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        agent.Model.Enqueue([new TextContent("Starting."), Call("LaunchWorkflow", """{"workflow":"PlaceOrder"}""")]);
        agent.Model.Enqueue([Call("Stock", """{"item":"rose"}""")]);
        agent.Model.Enqueue(Closing("Twelve in stock."));

        await agent.TurnAsync("I want a rose");

        Assert.Contains("Stock:rose", InventoryTools.Calls);
        Assert.Equal("Quote", agent.Provider.GetWorkflowPosition(agent.Session)!.Step);
        Assert.Contains("""{"quantity":12}""", agent.FunctionResults());
    }

    [Fact]
    public async Task A_launch_for_an_unknown_workflow_changes_nothing()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        agent.Model.Enqueue([new TextContent("Starting."), Call("LaunchWorkflow", """{"workflow":"Nothing"}""")]);
        agent.Model.Enqueue(Closing("I cannot."));

        await agent.TurnAsync("do it");

        Assert.Contains("LaunchWorkflow is not available at this step of Nothing.", agent.FunctionResults());
        Assert.Null(agent.Provider.GetWorkflowPosition(agent.Session));
    }

    [Fact]
    public async Task An_approved_call_runs_with_the_bound_values_and_ends_the_workflow_across_a_saved_session()
    {
        AgentUnderTest first = await AgentUnderTest.CreateAsync(PlaceOrder());
        await first.RunQuoteTurnAsync();

        // The model asks to confirm without passing a single value: the framework holds them.
        first.Model.Enqueue([new TextContent("Shall I confirm?"), Call("ConfirmOrder", "{}"), Call("Reply", """{"awaits":"action_choice","userIsLeaving":false}""")]);
        await first.TurnAsync("confirm");
        List<ToolApprovalRequestContent> requests = [.. first.History.GetMessages(first.Session).SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>()];
        Assert.Single(requests);
        Assert.DoesNotContain(InventoryTools.Calls, call => call.StartsWith("ConfirmOrder", StringComparison.Ordinal));

        // A fresh agent over the saved session: new engine and new provider, nothing but the session carried over.
        AgentUnderTest second = await AgentUnderTest.CreateAsync(PlaceOrder());
        await second.RestoreAsync(first);
        second.Model.Enqueue(Closing("Done."));
        await second.TurnAsync("go ahead", [.. requests.Select(request => (AIContent)request.CreateResponse(true))]);

        Assert.Contains("ConfirmOrder:ORD-1:fern", InventoryTools.Calls);
        Assert.Null(second.Provider.GetWorkflowPosition(second.Session));
        Assert.Contains(second.FunctionResults(), result => result.StartsWith("[WORKFLOW PlaceOrder — ENDED]\n{", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workflow_resumes_on_a_fresh_agent_at_the_step_that_was_pending()
    {
        AgentUnderTest first = await AgentUnderTest.CreateAsync(PlaceOrder());
        await first.RunQuoteTurnAsync();

        AgentUnderTest second = await AgentUnderTest.CreateAsync(PlaceOrder());
        await second.RestoreAsync(first);
        second.Model.Enqueue([new TextContent("Cancelling."), Call("CancelOrder", "{}")]);
        second.Model.Enqueue(Closing("Cancelled."));
        await second.TurnAsync("cancel it");

        // Offered the step it stood at and bound from the quote of the earlier agent.
        Assert.Equal(["CancelOrder", "ConfirmOrder", "Reply", "Stock"], Names(second.Model.ToolsPerCall[0]));
        Assert.Contains("CancelOrder:ORD-1", InventoryTools.Calls);
        Assert.Null(second.Provider.GetWorkflowPosition(second.Session));
    }

    [Fact]
    public async Task A_declined_call_never_reaches_the_tool_loop_and_leaves_the_workflow_on_its_step()
    {
        AgentUnderTest first = await AgentUnderTest.CreateAsync(PlaceOrder());
        await first.RunQuoteTurnAsync();
        first.Model.Enqueue([new TextContent("Shall I confirm?"), Call("ConfirmOrder", "{}"), Call("Reply", """{"awaits":"action_choice","userIsLeaving":false}""")]);
        await first.TurnAsync("confirm");
        List<ToolApprovalRequestContent> requests = [.. first.History.GetMessages(first.Session).SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>()];

        AgentUnderTest second = await AgentUnderTest.CreateAsync(PlaceOrder());
        await second.RestoreAsync(first);
        second.Model.Enqueue([new TextContent("Understood."), Call("Reply", ExactReply)]);
        await second.TurnAsync("no", [.. requests.Select(request => (AIContent)request.CreateResponse(false))]);

        // The tool loop answers a declined call itself: the tool is not run and the workflow does not move.
        Assert.DoesNotContain(InventoryTools.Calls, call => call.StartsWith("ConfirmOrder", StringComparison.Ordinal));
        Assert.Equal("Decide", second.Provider.GetWorkflowPosition(second.Session)!.Step);
    }

    [Fact]
    public async Task A_carried_property_binds_the_tool_parameter_that_spells_its_name_otherwise()
    {
        AgentUnderTest agent = await AgentUnderTest.CreateAsync(PlaceOrder());
        await agent.RunQuoteTurnAsync();

        // The workflow carries OrderId and the tool declares orderId: the value is written under the schema's spelling.
        agent.Model.Enqueue([new TextContent("Cancelling."), Call("CancelOrder", "{}")]);
        agent.Model.Enqueue(Closing("Cancelled."));
        await agent.TurnAsync("cancel it");

        Assert.Contains("CancelOrder:ORD-1", InventoryTools.Calls);
    }

    // =========================================================================
    // MCP TOOLS INSIDE WORKFLOWS
    // =========================================================================

    [Fact]
    public void An_MCP_result_is_read_from_its_structured_content_and_fails_on_the_envelope_or_the_error_field()
    {
        Records.StepOutcome succeeded = WorkflowEngine.ReadOutcome("ReserveStock", """{"content":[],"structuredContent":{"orderId":"RSV-rose","quantity":1}}""", isMCPTool: true);
        Records.StepOutcome domainFailure = WorkflowEngine.ReadOutcome("ReserveStock", """{"content":[],"structuredContent":{"error":"out of stock","quantity":0}}""", isMCPTool: true);
        Records.StepOutcome envelopeFailure = WorkflowEngine.ReadOutcome("FailHard", """{"content":[{"type":"text","text":"boom"}],"isError":true}""", isMCPTool: true);
        Records.StepOutcome plainText = WorkflowEngine.ReadOutcome("ReadNote", "noted", isMCPTool: true);

        Assert.False(succeeded.Failed);
        Assert.Equal("""{"orderId":"RSV-rose","quantity":1}""", succeeded.FieldsJson);
        Assert.True(domainFailure.Failed);
        Assert.True(envelopeFailure.Failed);
        Assert.Null(envelopeFailure.FieldsJson);
        Assert.False(plainText.Failed);
        Assert.Null(plainText.FieldsJson);
    }

    [Fact]
    public void The_origin_of_a_tool_decides_how_its_result_is_read_never_the_shape()
    {
        // A native record that happens to have the envelope's property names is still a native record.
        Records.StepOutcome native = WorkflowEngine.ReadOutcome("CreatePurchaseOrder", """{"isError":true,"structuredContent":{"error":"x"},"orderId":"ORD-1"}""", isMCPTool: false);

        Assert.False(native.Failed);
        Assert.Equal("\"ORD-1\"", WorkflowEngine.ReadField(native.FieldsJson!, "orderId"));
        Assert.True(WorkflowEngine.ReadOutcome("CreatePurchaseOrder", """{"error":"No such plant"}""", isMCPTool: false).Failed);
    }

    [Fact]
    public async Task A_native_step_hands_its_value_to_an_MCP_step()
    {
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(MCPAgentMarker), NullLogger.Instance, Mixed());
        agent.Model.Enqueue([Call("LaunchWorkflow", """{"workflow":"Mixed"}""")]);
        agent.Model.Enqueue([Call("CreatePurchaseOrder", """{"item":"rose"}""")]);
        agent.Model.Enqueue([Call("ShipOrder", "{}")]);
        agent.Model.Enqueue(Closing("Shipped."));

        await agent.TurnAsync("ship a rose");

        Assert.Contains(agent.FunctionResults(), result => result.Contains("TRK-ORD-1", StringComparison.Ordinal));
        Assert.Null(agent.Provider.GetWorkflowPosition(agent.Session));
    }

    [Fact]
    public async Task An_MCP_step_binds_the_field_of_its_structured_content_into_the_next_step()
    {
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(MCPAgentMarker), NullLogger.Instance, Reserve());
        agent.Model.Enqueue([Call("LaunchWorkflow", """{"workflow":"Reserve"}""")]);
        agent.Model.Enqueue([Call("ReserveStock", """{"item":"rose"}""")]);
        agent.Model.Enqueue([Call("ShipOrder", "{}")]);
        agent.Model.Enqueue(Closing("Shipped."));

        await agent.TurnAsync("reserve a rose and ship it");

        Assert.Contains(agent.FunctionResults(), result => result.Contains("TRK-RSV-rose", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_MCP_step_whose_record_holds_an_error_follows_the_failure_edge()
    {
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(MCPAgentMarker), NullLogger.Instance, Reserve());
        agent.Model.Enqueue([Call("LaunchWorkflow", """{"workflow":"Reserve"}""")]);
        agent.Model.Enqueue([Call("ReserveStock", """{"item":"unavailable"}""")]);
        agent.Model.Enqueue(Closing("It is out of stock."));

        await agent.TurnAsync("reserve the unavailable item");

        Assert.Equal(("Reserve", "Reserve"), (agent.Provider.GetWorkflowPosition(agent.Session)!.Workflow, agent.Provider.GetWorkflowPosition(agent.Session)!.Step));
    }

    [Fact]
    public async Task An_MCP_step_that_the_server_reports_as_an_error_follows_the_failure_edge_and_a_plain_text_result_is_read_as_text()
    {
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(MCPAgentMarker), NullLogger.Instance, Fragile());
        agent.Model.Enqueue([Call("LaunchWorkflow", """{"workflow":"Fragile"}""")]);
        agent.Model.Enqueue([Call("FailHard", "{}")]);
        agent.Model.Enqueue([Call("ReadNote", "{}")]);
        agent.Model.Enqueue(Closing("Done."));

        await agent.TurnAsync("try it");

        // FailHard led to Note, where ReadNote ran: a failure that no edge followed would have ended the workflow before it.
        string[] results = agent.FunctionResults();
        Assert.Contains(results, result => result.Contains("noted", StringComparison.Ordinal));
        Assert.DoesNotContain(results, result => result.Contains("$type", StringComparison.Ordinal));
        Assert.Null(agent.Provider.GetWorkflowPosition(agent.Session));
    }

    [Fact]
    public async Task A_workflow_carrying_a_field_out_of_a_tool_with_no_output_schema_is_withdrawn_and_LaunchWorkflow_is_not_offered()
    {
        CapturingLogger logger = new CapturingLogger();
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(MCPAgentMarker), logger, Blind());
        agent.Model.Enqueue(Closing("Hello."));

        await agent.TurnAsync("hi");

        Assert.DoesNotContain(agent.Model.ToolsPerCall[0], tool => tool.Name == "LaunchWorkflow");
        Assert.Contains(logger.Errors, error => error.Contains("Blind", StringComparison.Ordinal) && error.Contains("withdrawn", StringComparison.Ordinal)
            && error.Contains("does not declare the returned field 'OrderId'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workflow_citing_a_tool_that_no_server_offers_is_withdrawn_and_a_sound_one_beside_it_stays()
    {
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(MCPAgentMarker), NullLogger.Instance, Phantom(), Mixed(), Blind());
        agent.Model.Enqueue(Closing("Hello."));

        await agent.TurnAsync("hi");

        AIFunction launch = Assert.IsAssignableFrom<AIFunction>(Assert.Single(agent.Model.ToolsPerCall[0], tool => tool.Name == "LaunchWorkflow"));
        Assert.Equal(["Mixed"], launch.JsonSchema.GetProperty("properties").GetProperty("workflow").GetProperty("enum").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public async Task An_agent_whose_server_is_down_lives_on_with_its_native_tools_and_without_the_workflows_that_cite_the_server()
    {
        CapturingLogger logger = new CapturingLogger();
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(DownServerAgentMarker), logger, Mixed());
        agent.Model.Enqueue(Closing("Hello."));

        await agent.TurnAsync("hi");

        string[] offered = Names(agent.Model.ToolsPerCall[0]);
        Assert.Contains("CreatePurchaseOrder", offered);
        Assert.DoesNotContain("LaunchWorkflow", offered);
        Assert.DoesNotContain("ShipOrder", offered);
        Assert.Contains(logger.Errors, error => error.Contains("the agent has no tool 'ShipOrder'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_MCP_tool_named_like_a_native_one_is_dropped_and_the_native_one_answers()
    {
        CapturingLogger logger = new CapturingLogger();
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(ClashingServerAgentMarker), logger);
        agent.Model.Enqueue([Call("Stock", """{"item":"rose"}""")]);
        agent.Model.Enqueue(Closing("Twelve."));

        await agent.TurnAsync("how many roses");

        Assert.Single(agent.Model.ToolsPerCall[0], tool => tool.Name == "Stock");
        Assert.Contains("ReadNote", Names(agent.Model.ToolsPerCall[0]));
        Assert.Contains("Stock:rose", InventoryTools.Calls);
        Assert.Contains(logger.Errors, error => error.Contains("'Stock'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_tool_of_an_MCP_server_asks_for_approval_even_when_the_server_declares_it_destructive()
    {
        await using AgentUnderTest agent = await AgentUnderTest.CreateAsync(typeof(MCPAgentMarker), NullLogger.Instance);
        agent.Model.Enqueue(Closing("Hello."));

        await agent.TurnAsync("hi");

        foreach (string name in new[] { "ReserveStock", "ShipOrder", "ReadNote", "FailHard", "WipeCatalog" })
        {
            AITool tool = Assert.Single(agent.Model.ToolsPerCall[0], offered => offered.Name == name);
            Assert.IsNotType<ApprovalRequiredAIFunction>(tool);
        }
    }

    // =========================================================================
    // THE CLASS
    // =========================================================================

    [Fact]
    public void A_workflow_class_projects_into_a_definition_named_without_its_suffix()
    {
        Records.WorkflowDefinition definition = new PlaceOrderWorkflow().ToDefinition();

        Assert.Equal("PlaceOrder", definition.Name);
        Assert.Equal("Placing an order.", definition.Description);
        Assert.Equal(["Quote", "Decide"], definition.Steps.Select(step => step.Name));
        Assert.Equal(["OrderId", "SealWord"], definition.Parameters);
        Assert.Equal(
            [("Quote", "Decide", "CreatePurchaseOrder", false), ("Decide", "Quote", "ConfirmOrder", true)],
            definition.Edges.Select(edge => (edge.Source, edge.Target, edge.Tool, edge.OnFailure)));
        Assert.Equal(["OrderId", "SealWord"], definition.Edges[0].Carrying);
    }

    [Fact]
    public void The_start_step_comes_first_whatever_edge_was_declared_first()
    {
        Records.WorkflowDefinition definition = new BackwardsWorkflow().ToDefinition();

        Assert.Equal("Backwards", definition.Name);
        Assert.Equal(["Quote", "Decide"], definition.Steps.Select(step => step.Name));
    }

    [Fact]
    public void A_definition_lists_only_the_properties_that_the_class_declares_itself()
    {
        Records.WorkflowDefinition definition = new DerivedWorkflow().ToDefinition();

        Assert.Equal(["Own"], definition.Parameters);
        Assert.Equal(string.Empty, definition.Description);
    }

    // =========================================================================
    // THE STARTUP CHECK
    // =========================================================================

    [Fact]
    public void A_sound_declaration_is_accepted()
        => Assert.Empty(Validate([PlaceOrder(), Settle()]));

    [Fact]
    public void A_workflow_name_declared_twice_is_refused()
        => AssertRefused([PlaceOrder(), PlaceOrder()], "declared more than once");

    [Fact]
    public void A_workflow_without_a_description_is_refused()
        => AssertRefused([PlaceOrder() with { Description = " " }], "has no description");

    [Fact]
    public void A_workflow_without_a_step_is_refused()
        => AssertRefused([PlaceOrder() with { Steps = [] }], "has no step");

    [Fact]
    public void A_step_name_used_twice_is_refused()
        => AssertRefused([Ad("PlaceOrder", [QuoteStep, QuoteStep], [], [])], "step 'Quote': the name is declared more than once");

    [Fact]
    public void A_step_naming_no_tool_is_refused()
        => AssertRefused([Ad("PlaceOrder", [new("Quote", [])], [], [])], "the step offers no tool");

    [Fact]
    public void A_step_naming_an_undeclared_tool_is_refused_unless_the_agent_uses_an_MCP_server()
    {
        Records.WorkflowDefinition workflow = Ad("PlaceOrder", [new("Quote", ["CreatePurchaseOrder", "Mystery"])], [], []);

        AssertRefused([workflow], "the agent has no tool 'Mystery'");
        Assert.Empty(Validate([workflow], usesMcpServer: true));
    }

    [Theory]
    [InlineData("Reply")]
    [InlineData("LaunchWorkflow")]
    [InlineData("consult_billing")]
    public void A_step_naming_a_framework_tool_or_a_colleague_is_refused(string tool)
        => AssertRefused([Ad("PlaceOrder", [new("Quote", ["CreatePurchaseOrder", tool])], [], [])], "belongs to the framework");

    [Fact]
    public void An_edge_followed_by_a_tool_the_source_step_does_not_hold_is_refused()
    {
        AssertRefused([Ad("PlaceOrder", [QuoteStep], [new("Quote", "Quote", "CancelOrder", false, [])], [])], "'CancelOrder' is not a tool of step 'Quote'");
        AssertRefused([Ad("PlaceOrder", [QuoteStep], [new("Quote", "Quote", "CancelOrder", true, [])], [])], "'CancelOrder' is not a tool of step 'Quote'");
    }

    [Fact]
    public void A_step_that_no_path_from_the_first_reaches_is_refused()
        => AssertRefused([Ad("PlaceOrder", [QuoteStep, DecideStep], [], [])], "step 'Decide': no path leads to it from 'Quote'");

    [Fact]
    public void A_transition_declared_twice_is_refused()
        => AssertRefused(
            [Ad("PlaceOrder", [QuoteStep, DecideStep],
                [new("Quote", "Decide", "CreatePurchaseOrder", false, ["OrderId"]), new("Quote", "Quote", "CreatePurchaseOrder", false, [])],
                ["OrderId"])],
            "the same transition is declared more than once");

    [Fact]
    public void A_carried_name_that_is_not_a_public_property_of_the_workflow_is_refused()
        => AssertRefused(
            [Ad("PlaceOrder", [QuoteStep, DecideStep], [new("Quote", "Decide", "CreatePurchaseOrder", false, ["colour"])], [])],
            "'colour' is not a public property of the workflow");

    [Fact]
    public void A_carried_name_that_the_source_tool_does_not_return_is_refused()
        => AssertRefused(
            [Ad("PlaceOrder", [QuoteStep, DecideStep], [new("Quote", "Decide", "CreatePurchaseOrder", false, ["OrderId", "Status"])], ["OrderId", "Status"])],
            "tool 'CreatePurchaseOrder' does not declare the returned field 'Status'");

    [Fact]
    public void A_carried_name_that_no_tool_of_the_target_step_takes_is_refused()
        => AssertRefused(
            [Ad("PlaceOrder", [QuoteStep, DecideStep], [new("Quote", "Decide", "CreatePurchaseOrder", false, ["Total"])], ["Total"])],
            "no tool of step 'Decide' takes a parameter 'Total'");

    [Fact]
    public void A_property_that_no_edge_carries_is_refused()
        => AssertRefused(
            [Ad("PlaceOrder", [QuoteStep, DecideStep], [new("Quote", "Decide", "CreatePurchaseOrder", false, ["OrderId"])], ["OrderId", "SealWord"])],
            "property 'SealWord' is carried by no edge");

    [Fact]
    public void A_tool_of_an_MCP_server_may_be_a_step_and_the_source_of_an_edge()
    {
        Records.WorkflowDefinition workflow = Ad(
            "PlaceOrder",
            [new("Quote", ["McpQuote"]), DecideStep],
            [new("Quote", "Decide", "McpQuote", false, ["OrderId"])],
            ["OrderId"]);

        List<string> errors = Validate([workflow], usesMcpServer: true);

        Assert.DoesNotContain(errors, error => error.Contains("the agent has no tool", StringComparison.Ordinal));
        Assert.Contains(Validate([workflow]), error => error.Contains("the agent has no tool 'McpQuote'", StringComparison.Ordinal));
    }

    // =========================================================================
    // DECLARATIONS AND HELPERS
    // =========================================================================

    /// <summary>The names of the tools that a model call was offered, in a stable order.</summary>
    private static string[] Names(IEnumerable<AITool> tools)
        => [.. tools.Select(tool => tool.Name).Order(StringComparer.Ordinal)];

    /// <summary>A Reply offering exactly the decision step's two tools.</summary>
    private const string ExactReply = """{"awaits":"action_choice","userIsLeaving":false,"actions":[{"tool":"ConfirmOrder","label":"Confirm","value":"confirm"},{"tool":"CancelOrder","label":"Cancel","value":"cancel"}]}""";

    /// <summary>The schema of Reply among the tools of a call.</summary>
    private static JsonElement ReplySchema(IEnumerable<AITool> tools)
        => ((AIFunction)Assert.Single(tools, tool => tool.Name == "Reply")).JsonSchema;

    /// <summary>The closure that the turn recorded.</summary>
    private static Records.TurnReply ClosureOf(AgentUnderTest agent)
    {
        string closure = Assert.IsType<string>(agent.Provider.GetVariable(agent.Session, Constants.ContextKeys.TurnReply));
        return JsonSerializer.Deserialize<Records.TurnReply>(closure, new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true })!;
    }

    /// <summary>One model answer closing the turn: text first and then Reply.</summary>
    private static List<AIContent> Closing(string text)
        => [new TextContent(text), Call("Reply", """{"awaits":"nothing","userIsLeaving":false}""")];

    /// <summary>One call of a tool, with its arguments as a model sends them.</summary>
    private static FunctionCallContent Call(string name, string json)
        => new FunctionCallContent(
            Guid.NewGuid().ToString("N"),
            name,
            JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

    /// <summary>The outcome of a call that went well.</summary>
    private static Records.StepOutcome Succeeded(string tool, string resultJson) => new(tool, false, resultJson);

    /// <summary>Launches PlaceOrder and takes it through its first step.</summary>
    private static async Task<Records.WorkflowPosition> ReachDecideAsync(WorkflowEngine engine)
    {
        Records.WorkflowPosition quote = await engine.LaunchAsync("PlaceOrder");
        return (await engine.AdvanceAsync(quote, Succeeded("CreatePurchaseOrder", """{"orderId":"ORD-1","sealWord":"fern"}""")))!;
    }

    /// <summary>The quote step of a definition written out by hand.</summary>
    private static readonly Records.WorkflowStep QuoteStep = new("Quote", ["CreatePurchaseOrder"]);

    /// <summary>The decision step of a definition written out by hand.</summary>
    private static readonly Records.WorkflowStep DecideStep = new("Decide", ["ConfirmOrder", "CancelOrder"]);

    /// <summary>The workflow of the order as its class declares it.</summary>
    private static Records.WorkflowDefinition PlaceOrder() => new PlaceOrderWorkflow().ToDefinition();

    /// <summary>A workflow carrying the order and its price straight from the quote to the payment.</summary>
    private static Records.WorkflowDefinition Settle() => new SettleWorkflow().ToDefinition();

    /// <summary>A workflow handing a native tool's order to an MCP tool.</summary>
    private static Records.WorkflowDefinition Mixed() => new MixedWorkflow().ToDefinition();

    /// <summary>A workflow handing an MCP tool's order to another MCP tool.</summary>
    private static Records.WorkflowDefinition Reserve() => new ReserveWorkflow().ToDefinition();

    /// <summary>A workflow whose first tool fails on the server.</summary>
    private static Records.WorkflowDefinition Fragile() => new FragileWorkflow().ToDefinition();

    /// <summary>A workflow carrying a field that its source tool does not declare.</summary>
    private static Records.WorkflowDefinition Blind() => new BlindWorkflow().ToDefinition();

    /// <summary>A workflow citing a tool that does not exist.</summary>
    private static Records.WorkflowDefinition Phantom() => new PhantomWorkflow().ToDefinition();

    /// <summary>A definition written out by hand, for the shapes that a class cannot declare.</summary>
    private static Records.WorkflowDefinition Ad(string name, Records.WorkflowStep[] steps, Records.WorkflowEdge[] edges, string[] parameters)
        => new(name, "Placing an order.", steps, edges, parameters);

    /// <summary>The tools of the sample agent as the catalog projects them.</summary>
    private static Records.ToolDefinition[] DeclaredTools() =>
    [
        new("CreatePurchaseOrder", "Quotes an order.", [new("item", "The plant.", true, Constants.Scopes.Request)],
            Returns: [new("error", "Why it failed.", Failure: true), new("orderId", "The order."), new("sealWord", "The seal."), new("total", "The price.")]),
        new("ConfirmOrder", "Confirms an order.", [new("orderId", "The order.", true, Constants.Scopes.Request), new("sealWord", "The seal.", true, Constants.Scopes.Request)],
            RequiresExecutionApproval: true, Returns: [new("error", "Why it failed.", Failure: true), new("status", "The status.")]),
        new("CancelOrder", "Cancels an order.", [new("orderId", "The order.", true, Constants.Scopes.Request)],
            Returns: [new("error", "Why it failed.", Failure: true), new("status", "The status.")]),
        new("Stock", "Reads a stock level.", [new("item", "The plant.", true, Constants.Scopes.Request)],
            Returns: [new("quantity", "How many.")]),
        new("Refund", "Refunds an order.", [new("orderId", "The order.", true, Constants.Scopes.Request), new("total", "The amount.", true, Constants.Scopes.Request)],
            RequiresExecutionApproval: true, Returns: [new("status", "The status.")])
    ];

    /// <summary>Runs the startup check on the sample agent's tools.</summary>
    private static List<string> Validate(Records.WorkflowDefinition[] workflows, bool usesMcpServer = false)
        => HandlesIntentAgentRegistryService.ValidateWorkflows("sample", workflows, DeclaredTools(), usesMcpServer);

    /// <summary>Asserts that the check refuses the declaration with a message holding the given text and naming the intent.</summary>
    private static void AssertRefused(Records.WorkflowDefinition[] workflows, string expected)
        => Assert.Contains(Validate(workflows), error => error.Contains(expected, StringComparison.Ordinal) && error.Contains("'sample'", StringComparison.Ordinal));

    // =========================================================================
    // THE SAMPLE WORKFLOWS
    // =========================================================================

    /// <summary>Quote, then decide: a refused confirmation goes back to the quote.</summary>
    [Description("Placing an order.")]
    public sealed class PlaceOrderWorkflow : MorganaWorkflow
    {
        public string? OrderId { get; init; }
        public string? SealWord { get; init; }

        private static readonly Records.WorkflowStep Quote = new("Quote", [nameof(InventoryTools.CreatePurchaseOrder)]);
        private static readonly Records.WorkflowStep Decide = new("Decide", [nameof(InventoryTools.ConfirmOrder), nameof(InventoryTools.CancelOrder)]);

        public PlaceOrderWorkflow() : base(start: Quote)
        {
            AddEdge(Quote, Decide, nameof(InventoryTools.CreatePurchaseOrder), carrying: [nameof(OrderId), nameof(SealWord)]);
            AddFailureEdge(Decide, Quote, nameof(InventoryTools.ConfirmOrder));
        }
    }

    /// <summary>Quote, then pay with the order and its price taken from the quote.</summary>
    [Description("Settling an order.")]
    public sealed class SettleWorkflow : MorganaWorkflow
    {
        public string? OrderId { get; init; }
        public string? Total { get; init; }

        private static readonly Records.WorkflowStep Quote = new("Quote", [nameof(InventoryTools.CreatePurchaseOrder)]);
        private static readonly Records.WorkflowStep Pay = new("Pay", [nameof(InventoryTools.Refund)]);

        public SettleWorkflow() : base(start: Quote)
            => AddEdge(Quote, Pay, nameof(InventoryTools.CreatePurchaseOrder), carrying: [nameof(OrderId), nameof(Total)]);
    }

    /// <summary>Quote with a native tool, then ship the order with an MCP tool.</summary>
    [Description("Quoting then shipping.")]
    public sealed class MixedWorkflow : MorganaWorkflow
    {
        public string? OrderId { get; init; }

        private static readonly Records.WorkflowStep Quote = new("Quote", [nameof(InventoryTools.CreatePurchaseOrder)]);
        private static readonly Records.WorkflowStep Ship = new("Ship", ["ShipOrder"]);

        public MixedWorkflow() : base(start: Quote)
            => AddEdge(Quote, Ship, nameof(InventoryTools.CreatePurchaseOrder), carrying: [nameof(OrderId)]);
    }

    /// <summary>Reserve with an MCP tool, then ship what it reserved; a failed reservation stays at the reservation.</summary>
    [Description("Reserving then shipping.")]
    public sealed class ReserveWorkflow : MorganaWorkflow
    {
        public string? OrderId { get; init; }

        private static readonly Records.WorkflowStep Reserve = new("Reserve", ["ReserveStock"]);
        private static readonly Records.WorkflowStep Ship = new("Ship", ["ShipOrder"]);

        public ReserveWorkflow() : base(start: Reserve)
        {
            AddEdge(Reserve, Ship, "ReserveStock", carrying: [nameof(OrderId)]);
            AddFailureEdge(Reserve, Reserve, "ReserveStock");
        }
    }

    /// <summary>A tool that always fails, then a note read once it has.</summary>
    [Description("Trying then noting.")]
    public sealed class FragileWorkflow : MorganaWorkflow
    {
        private static readonly Records.WorkflowStep Try = new("Try", ["FailHard"]);
        private static readonly Records.WorkflowStep Note = new("Note", ["ReadNote"]);

        public FragileWorkflow() : base(start: Try)
            => AddFailureEdge(Try, Note, "FailHard");
    }

    /// <summary>Carries a field out of a tool whose server declares no output schema.</summary>
    [Description("Reading then shipping.")]
    public sealed class BlindWorkflow : MorganaWorkflow
    {
        public string? OrderId { get; init; }

        private static readonly Records.WorkflowStep Read = new("Read", ["ReadNote"]);
        private static readonly Records.WorkflowStep Ship = new("Ship", ["ShipOrder"]);

        public BlindWorkflow() : base(start: Read)
            => AddEdge(Read, Ship, "ReadNote", carrying: [nameof(OrderId)]);
    }

    /// <summary>Cites a tool that no server offers.</summary>
    [Description("Calling a ghost.")]
    public sealed class PhantomWorkflow : MorganaWorkflow
    {
        private static readonly Records.WorkflowStep Haunt = new("Haunt", ["NoSuchTool"]);

        public PhantomWorkflow() : base(start: Haunt)
        {
        }
    }

    /// <summary>Declares its edge from the later step first, so that the start has to be put in front.</summary>
    public sealed class BackwardsWorkflow : MorganaWorkflow
    {
        private static readonly Records.WorkflowStep Quote = new("Quote", [nameof(InventoryTools.CreatePurchaseOrder)]);
        private static readonly Records.WorkflowStep Decide = new("Decide", [nameof(InventoryTools.CancelOrder)]);

        public BackwardsWorkflow() : base(start: Quote)
            => AddEdge(Decide, Quote, nameof(InventoryTools.CancelOrder));
    }

    /// <summary>Holds the property that a derived workflow inherits.</summary>
    public abstract class InheritedPropertyWorkflow : MorganaWorkflow
    {
        public string? Inherited { get; init; }

        protected InheritedPropertyWorkflow() : base(start: new Records.WorkflowStep("Quote", [nameof(InventoryTools.CreatePurchaseOrder)]))
        {
        }
    }

    /// <summary>Declares one property of its own beside the inherited one.</summary>
    public sealed class DerivedWorkflow : InheritedPropertyWorkflow
    {
        public string? Own { get; init; }
    }

    // =========================================================================
    // THE AGENT UNDER TEST
    // =========================================================================

    /// <summary>The records that the sample tools return.</summary>
    public sealed record QuoteResult(string? Error = null, string? OrderId = null, string? SealWord = null, double? Total = null);

    /// <summary>What confirming or cancelling returns.</summary>
    public sealed record StatusResult(string? Error = null, string? Status = null);

    /// <summary>What reading a stock level returns.</summary>
    public sealed record StockResult(int Quantity);

    /// <summary>What a refund returns.</summary>
    public sealed record RefundResult(string? Status = null);

    /// <summary>
    /// The sample agent's tools; every call is written to <see cref="Calls"/>, which each test starts empty.
    /// </summary>
    public sealed class InventoryTools(ILogger logger, Func<MorganaTool.ToolContext> context) : MorganaTool(logger, context)
    {
        /// <summary>What the tools were called with, as <c>Tool:argument:argument</c>.</summary>
        public static List<string> Calls { get; } = [];

        public Task<QuoteResult> CreatePurchaseOrder(string item)
        {
            Calls.Add($"CreatePurchaseOrder:{item}");
            return Task.FromResult(item == "unknown" ? new QuoteResult(Error: "No such plant") : new QuoteResult(OrderId: "ORD-1", SealWord: "fern", Total: 12.5));
        }

        public Task<StatusResult> ConfirmOrder(string orderId, string sealWord)
        {
            Calls.Add($"ConfirmOrder:{orderId}:{sealWord}");
            return Task.FromResult(new StatusResult(Status: "confirmed"));
        }

        public Task<StatusResult> CancelOrder(string orderId)
        {
            Calls.Add($"CancelOrder:{orderId}");
            return Task.FromResult(new StatusResult(Status: "cancelled"));
        }

        public Task<StockResult> Stock(string item)
        {
            Calls.Add($"Stock:{item}");
            return Task.FromResult(new StockResult(12));
        }

        public Task<RefundResult> Refund(string orderId, string total)
        {
            Calls.Add($"Refund:{orderId}:{total}");
            return Task.FromResult(new RefundResult("refunded"));
        }
    }

    /// <summary>The class that carries the attributes of the sample agent.</summary>
    [HandlesIntent("inventory")]
    [RequiresLLMTier(Records.LLMTier.Efficiency)]
    private sealed class InventoryAgentMarker;

    /// <summary>The sample agent that also acquires tools from the deterministic MCP server.</summary>
    [HandlesIntent("inventory")]
    [RequiresLLMTier(Records.LLMTier.Efficiency)]
    [UsesMCPServer(Records.MCPTransport.Stdio, "mcp-test-server/MCPTestServer")]
    private sealed class MCPAgentMarker;

    /// <summary>The sample agent whose server also offers a tool named like a native one.</summary>
    [HandlesIntent("inventory")]
    [RequiresLLMTier(Records.LLMTier.Efficiency)]
    [UsesMCPServer(Records.MCPTransport.Stdio, "mcp-test-server/MCPTestServer", "--clash")]
    private sealed class ClashingServerAgentMarker;

    /// <summary>The sample agent whose server does not exist.</summary>
    [HandlesIntent("inventory")]
    [RequiresLLMTier(Records.LLMTier.Efficiency)]
    [UsesMCPServer(Records.MCPTransport.Stdio, "mcp-test-server/NoSuchServer")]
    private sealed class DownServerAgentMarker;

    /// <summary>
    /// The sample agent as the adapter really builds it: its prompt and tools resolved from a declaration made
    /// here, its model replaced by a script that records the tools each call was offered.
    /// </summary>
    private sealed class AgentUnderTest : IAsyncDisposable
    {
        /// <summary>The registry of MCP clients that this agent alone uses, closed with it.</summary>
        private readonly MCPClientRegistryService mcpRegistry = new MCPClientRegistryService(NullLogger.Instance);

        /// <summary>The script standing where the model would.</summary>
        public ScriptedModel Model { get; } = new ScriptedModel();

        /// <summary>The agent's context store, where the workflow position is kept.</summary>
        public MorganaAIContextProvider Provider { get; private set; } = null!;

        /// <summary>Morgana's history provider, where every message is filed before the run reads it.</summary>
        public MorganaChatHistoryProvider History { get; private set; } = null!;

        /// <summary>The session that the agent runs on.</summary>
        public AgentSession Session { get; private set; } = null!;

        /// <summary>The agent under test.</summary>
        private AIAgent agent = null!;

        /// <summary>
        /// Builds the sample agent declaring the given workflows; none builds an agent without any.
        /// </summary>
        public static Task<AgentUnderTest> CreateAsync(params Records.WorkflowDefinition[] workflows)
            => CreateAsync(typeof(InventoryAgentMarker), NullLogger.Instance, workflows);

        /// <summary>
        /// Builds the sample agent as the given marker declares it, logging to the given logger.
        /// </summary>
        public static async Task<AgentUnderTest> CreateAsync(Type agentMarker, ILogger logger, params Records.WorkflowDefinition[] workflows)
        {
            InventoryTools.Calls.Clear();
            AgentUnderTest under = new AgentUnderTest();

            Records.Prompt prompt = new("inventory", "Sell plants.", "Answer plainly.", "Plain text.", null, null, "en-US", "1");

            ConfigurationPromptResolverService resolver = new ConfigurationPromptResolverService(new SampleDomain(prompt));
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Morgana:HistoryReducer:Enabled"] = "false", ["Morgana:AgentToAgent:Enabled"] = "false" })
                .Build();

            MorganaAgentAdapter adapter = new MorganaAgentAdapter(
                new SampleLlm(under.Model),
                resolver,
                new ConfigurationPromptComposerService(resolver),
                new SampleToolRegistry(workflows),
                under.mcpRegistry,
                new HistoryReducerService(configuration, NullLogger.Instance),
                new NoDust(),
                null!,
                configuration,
                logger);

            (AIAgent builtAgent, MorganaAIContextProvider provider, MorganaChatHistoryProvider history) =
                await adapter.CreateAgentAsync(agentMarker, "conversation-1", () => under.Session);

            under.agent = builtAgent;
            under.Provider = provider;
            under.History = history;
            under.Session = await builtAgent.CreateSessionAsync();

            return under;
        }

        /// <summary>Closes the MCP clients, which ends the server processes they started.</summary>
        public async ValueTask DisposeAsync() => await mcpRegistry.DisposeAsync();

        /// <summary>
        /// Replaces this agent's session by the saved and reloaded copy of another's, as a restart does.
        /// </summary>
        public async Task RestoreAsync(AgentUnderTest earlier)
            => Session = await agent.DeserializeSessionAsync(await earlier.agent.SerializeSessionAsync(earlier.Session));

        /// <summary>Files the user's message the way MorganaAgent does and runs one turn on the session.</summary>
        public async Task TurnAsync(string userText, params AIContent[] approvalAnswers)
        {
            History.AppendMessage(Session, new ChatMessage(ChatRole.User, [new TextContent(userText), .. approvalAnswers]));
            await agent.RunAsync(Session);
        }

        /// <summary>
        /// Runs the turn that launches PlaceOrder and creates its quote, which leaves the workflow at its decision.
        /// The turn closes with each given Reply in turn, a refused one being answered by the next; none closes with the exact pair of actions.
        /// </summary>
        public async Task RunQuoteTurnAsync(params string[] replies)
        {
            Model.Enqueue([new TextContent("Starting."), Call("LaunchWorkflow", """{"workflow":"PlaceOrder"}""")]);
            Model.Enqueue([Call("CreatePurchaseOrder", """{"item":"rose"}""")]);
            Model.Enqueue([new TextContent("Here is the quote."), Call("Reply", replies.Length > 0 ? replies[0] : ExactReply)]);
            foreach (string reply in replies.Skip(1))
                Model.Enqueue([Call("Reply", reply)]);

            await TurnAsync("I want a rose");
        }

        /// <summary>The text of every tool result in the session's history, as the model reads it.</summary>
        public string[] FunctionResults()
            => [.. History.GetMessages(Session).SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                .Select(result => result.Result is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString()! : result.Result?.ToString() ?? string.Empty)];
    }

    /// <summary>
    /// Stands where the model would, answering each request with the next scripted contents and keeping the
    /// tools that the request was offered.
    /// </summary>
    private sealed class ScriptedModel : IChatClient
    {
        /// <summary>The scripted answers still to give.</summary>
        private readonly Queue<List<AIContent>> pending = new();

        /// <summary>The tools each request was offered, in order of request.</summary>
        public List<List<AITool>> ToolsPerCall { get; } = [];

        /// <summary>Adds the answer to give to a later request.</summary>
        public void Enqueue(List<AIContent> contents) => pending.Enqueue(contents);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ToolsPerCall.Add([.. options?.Tools ?? []]);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                pending.Count > 0 ? pending.Dequeue() : throw new InvalidOperationException("The script has no answer left for this request"))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No streaming is scripted in this group");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A logger that keeps the text of every error it is given.</summary>
    private sealed class CapturingLogger : ILogger
    {
        /// <summary>The formatted errors, in order.</summary>
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }

    /// <summary>The model of an agent that is only ever asked for a session.</summary>
    private sealed class SilentModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No model is reached by this group");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No model is reached by this group");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A deployment whose one domain agent is the sample.</summary>
    private sealed class SampleDomain(Records.Prompt prompt) : IAgentConfigurationService
    {
        public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

        public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt> { prompt });
    }

    /// <summary>Hands the adapter the scripted model on whichever tier it asks.</summary>
    private sealed class SampleLlm(IChatClient model) : ILLMService
    {
        public bool CanForceToolCall => false;

        public IReadOnlyCollection<Records.LLMTier> ConfiguredTiers => [Records.LLMTier.Efficiency];

        public IChatClient GetChatClient(Records.LLMTier tier) => model;

        public Records.MagicDustPricing GetPricing(Records.LLMTier tier) => new();

        public Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt)
            => throw new InvalidOperationException("No model is reached by this group");
    }

    /// <summary>Finds the sample tools for the sample intent and declares them as the catalog would project them.</summary>
    private sealed class SampleToolRegistry(IReadOnlyList<Records.WorkflowDefinition> workflows) : IToolRegistryService
    {
        public Type? FindToolTypeForIntent(string intent) => intent == "inventory" ? typeof(InventoryTools) : null;

        public IReadOnlyDictionary<string, Type> GetAllRegisteredTools() => new Dictionary<string, Type> { ["inventory"] = typeof(InventoryTools) };

        public IReadOnlyList<Records.ToolDefinition> GetToolDefinitions(string intent) => intent == "inventory" ? DeclaredTools() : [];

        public IReadOnlyList<Records.WorkflowDefinition> GetWorkflowDefinitions(string intent) => intent == "inventory" ? workflows : [];

        public IReadOnlyDictionary<string, IReadOnlyList<Records.WorkflowDefinition>> GetAllRegisteredWorkflows()
            => new Dictionary<string, IReadOnlyList<Records.WorkflowDefinition>> { ["inventory"] = workflows };
    }

    /// <summary>A budget that is never spent.</summary>
    private sealed class NoDust : IDustLimitService
    {
        public Task ChargeAsync(string conversationId, double dust, string llmRole) => Task.CompletedTask;

        public Task<bool> IsOverBudgetAsync(string conversationId) => Task.FromResult(false);

        public Task<double> GetConsumedAsync(string conversationId) => Task.FromResult(0.0);

        public Task<double> GetConsumedSinceAsync(string conversationId, double baseline) => Task.FromResult(0.0);

        public Task<double> GetUsageRatioAsync(string conversationId) => Task.FromResult(0.0);

        public Task<double?> GetRemainingLevelAsync(string conversationId) => Task.FromResult<double?>(null);

        public Task<(bool Send70, bool Send90)> CheckAndMarkWarningsAsync(string conversationId) => Task.FromResult((false, false));
    }
}
