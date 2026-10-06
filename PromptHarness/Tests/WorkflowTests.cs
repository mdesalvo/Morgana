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
        Assert.Equal("\"ORD-1\"", decide.Arguments["orderId"]);
        Assert.Equal("\"fern\"", decide.Arguments["sealWord"]);
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
    public async Task End_closes_the_workflow()
    {
        WorkflowEngine engine = new WorkflowEngine([PlaceOrder()]);
        Records.WorkflowPosition decide = await ReachDecideAsync(engine);

        Assert.Null(await engine.AdvanceAsync(decide, Succeeded("CancelOrder", """{"status":"cancelled"}""")));
    }

    [Fact]
    public async Task A_second_pass_through_a_step_binds_the_new_result()
    {
        WorkflowEngine engine = new WorkflowEngine([PlaceOrder()]);
        Records.WorkflowPosition decide = await ReachDecideAsync(engine);
        Records.WorkflowPosition quote = (await engine.AdvanceAsync(decide, new Records.StepOutcome("ConfirmOrder", true, """{"error":"declined"}""")))!;

        Records.WorkflowPosition? again = await engine.AdvanceAsync(quote, Succeeded("CreatePurchaseOrder", """{"orderId":"ORD-2","sealWord":"moss"}"""));

        Assert.Equal("\"ORD-2\"", again!.Arguments["orderId"]);
    }

    [Fact]
    public async Task A_value_is_bound_from_a_step_two_steps_back_exactly_as_the_result_wrote_it()
    {
        WorkflowEngine engine = new WorkflowEngine([Settle()]);
        Records.WorkflowPosition quote = await engine.LaunchAsync("Settle");
        Records.WorkflowPosition decide = (await engine.AdvanceAsync(quote, Succeeded("CreatePurchaseOrder", """{"orderId":"ORD-9","total":12.5}""")))!;

        Records.WorkflowPosition? pay = await engine.AdvanceAsync(decide, Succeeded("ConfirmOrder", """{"status":"confirmed"}"""));

        // The field is read by its declared name whatever the record cased it as, numbers staying numbers.
        Assert.Equal("Pay", pay!.Step);
        Assert.Equal("\"ORD-9\"", pay.Arguments["orderId"]);
        Assert.Equal("12.5", pay.Arguments["amount"]);
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

        string closure = Assert.IsType<string>(agent.Provider.GetVariable(agent.Session, Constants.ContextKeys.TurnReply));
        Records.TurnReply reply = JsonSerializer.Deserialize<Records.TurnReply>(closure, new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true })!;

        // Stock is the agent's own tool but not the step's: pressing it would lead nowhere that the workflow allows.
        Assert.Equal(["ConfirmOrder"], reply.Actions.Select(action => action.Tool));
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
        second.Model.Enqueue(Closing("Understood."));
        await second.TurnAsync("no", [.. requests.Select(request => (AIContent)request.CreateResponse(false))]);

        // The tool loop answers a declined call itself: the tool is not run and the workflow does not move.
        Assert.DoesNotContain(InventoryTools.Calls, call => call.StartsWith("ConfirmOrder", StringComparison.Ordinal));
        Assert.Equal("Decide", second.Provider.GetWorkflowPosition(second.Session)!.Step);
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
        => AssertRefused([WithSteps(Quote(), Quote())], "the step 'Quote' more than once");

    [Fact]
    public void A_step_named_End_is_refused()
        => AssertRefused([WithSteps(Quote() with { Name = "End", Next = new Dictionary<string, string>() })], "reserved target");

    [Fact]
    public void A_step_naming_no_tool_is_refused()
        => AssertRefused([WithSteps(Quote() with { Tools = [], Next = new Dictionary<string, string>() })], "names no tool");

    [Fact]
    public void A_step_naming_an_undeclared_tool_is_refused_unless_the_agent_uses_an_MCP_server()
    {
        Records.WorkflowDefinition workflow = WithSteps(Quote() with { Tools = ["CreatePurchaseOrder", "Mystery"], Next = new Dictionary<string, string> { ["Mystery"] = "End" } });

        AssertRefused([workflow], "'Mystery', which the agent does not declare");
        Assert.Empty(Validate([workflow], usesMcpServer: true));
    }

    [Theory]
    [InlineData("Reply")]
    [InlineData("LaunchWorkflow")]
    [InlineData("consult_billing")]
    public void A_step_naming_a_framework_tool_or_a_colleague_is_refused(string tool)
        => AssertRefused([WithSteps(Quote() with { Tools = ["CreatePurchaseOrder", tool] })], "may never name");

    [Fact]
    public void A_link_keyed_by_a_tool_the_step_does_not_hold_is_refused()
    {
        AssertRefused([WithSteps(Quote() with { Next = new Dictionary<string, string> { ["CancelOrder"] = "End" } })], "\"Next\" names 'CancelOrder'");
        AssertRefused([WithSteps(Quote() with { OnFailure = new Dictionary<string, string> { ["CancelOrder"] = "End" } })], "\"OnFailure\" names 'CancelOrder'");
    }

    [Fact]
    public void A_link_to_a_step_that_does_not_exist_is_refused()
        => AssertRefused([WithSteps(Quote() with { Next = new Dictionary<string, string> { ["CreatePurchaseOrder"] = "Nowhere" } })], "'Nowhere', which is neither a step");

    [Fact]
    public void A_step_that_no_path_from_the_first_reaches_is_refused()
        => AssertRefused([WithSteps(Quote() with { Next = new Dictionary<string, string> { ["CreatePurchaseOrder"] = "End" } }, Decide())], "'Decide', which no path");

    [Fact]
    public void A_bound_key_that_is_a_parameter_of_none_of_the_step_tools_is_refused()
        => AssertRefused([WithSteps(Quote(), Decide() with { Arguments = new Dictionary<string, string> { ["colour"] = "Quote.orderId" } })], "'colour', which is a parameter of none");

    [Fact]
    public void A_binding_that_is_not_of_the_form_Step_field_is_refused()
        => AssertRefused([WithSteps(Quote(), Decide() with { Arguments = new Dictionary<string, string> { ["orderId"] = "orderId" } })], "not of the form Step.field");

    [Fact]
    public void A_binding_to_a_step_the_workflow_does_not_have_is_refused()
        => AssertRefused([WithSteps(Quote(), Decide() with { Arguments = new Dictionary<string, string> { ["orderId"] = "Nowhere.orderId" } })], "the step 'Nowhere', which the workflow does not have");

    [Fact]
    public void A_binding_to_the_step_itself_or_to_a_later_one_is_refused()
    {
        AssertRefused([WithSteps(Quote(), Decide() with { Arguments = new Dictionary<string, string> { ["orderId"] = "Decide.status" } })], "does not run before this one");

        // Decide leads back to Quote on a failure, so only without that link is it a step that never runs before it.
        AssertRefused([WithSteps(Quote() with { Arguments = new Dictionary<string, string> { ["item"] = "Decide.status" } }, Decide() with { OnFailure = null })], "'Decide', which does not run before this one");
    }

    [Fact]
    public void A_binding_to_a_field_the_source_tool_does_not_declare_is_refused()
        => AssertRefused([WithSteps(Quote(), Decide() with { Arguments = new Dictionary<string, string> { ["orderId"] = "Quote.colour" } })], "does not declare the returned field 'colour'");

    [Fact]
    public void A_tool_of_an_MCP_server_may_be_a_step_but_never_the_source_of_a_binding()
    {
        Records.WorkflowDefinition workflow = WithSteps(Quote() with { Tools = ["McpQuote"], Next = new Dictionary<string, string> { ["McpQuote"] = "Decide" } }, Decide());

        List<string> errors = Validate([workflow], usesMcpServer: true);

        Assert.Contains(errors, error => error.Contains("the tool 'McpQuote' of step 'Quote' does not declare the returned field", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("does not declare", StringComparison.Ordinal) && error.Contains("'McpQuote', which the agent", StringComparison.Ordinal));
    }

    // =========================================================================
    // DECLARATIONS AND HELPERS
    // =========================================================================

    /// <summary>The names of the tools that a model call was offered, in a stable order.</summary>
    private static string[] Names(IEnumerable<AITool> tools)
        => [.. tools.Select(tool => tool.Name).Order(StringComparer.Ordinal)];

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

    /// <summary>The quote step: one tool leading to the decision.</summary>
    private static Records.WorkflowStep Quote() => new(
        "Quote",
        ["CreatePurchaseOrder"],
        new Dictionary<string, string> { ["CreatePurchaseOrder"] = "Decide" });

    /// <summary>The decision step: confirm or cancel, with the order and its seal bound from the quote.</summary>
    private static Records.WorkflowStep Decide() => new(
        "Decide",
        ["ConfirmOrder", "CancelOrder"],
        new Dictionary<string, string> { ["ConfirmOrder"] = "End", ["CancelOrder"] = "End" },
        new Dictionary<string, string> { ["ConfirmOrder"] = "Quote" },
        new Dictionary<string, string> { ["orderId"] = "Quote.orderId", ["sealWord"] = "Quote.sealWord" });

    /// <summary>The workflow of the order: quote then decide, a refused confirmation going back to the quote.</summary>
    private static Records.WorkflowDefinition PlaceOrder() => new("PlaceOrder", "Placing an order.", [Quote(), Decide()]);

    /// <summary>A workflow whose last step binds a value from the first, with a step between.</summary>
    private static Records.WorkflowDefinition Settle() => new(
        "Settle",
        "Settling an order.",
        [
            Quote(),
            new Records.WorkflowStep("Decide", ["ConfirmOrder"], new Dictionary<string, string> { ["ConfirmOrder"] = "Pay" },
                Arguments: new Dictionary<string, string> { ["orderId"] = "Quote.orderId" }),
            new Records.WorkflowStep("Pay", ["Refund"], new Dictionary<string, string> { ["Refund"] = "End" },
                Arguments: new Dictionary<string, string> { ["orderId"] = "Quote.orderId", ["amount"] = "Quote.total" })
        ]);

    /// <summary>A workflow of the given steps under the order's name.</summary>
    private static Records.WorkflowDefinition WithSteps(params Records.WorkflowStep[] steps)
        => new("PlaceOrder", "Placing an order.", steps);

    /// <summary>The tools of the sample agent as agents.json declares them.</summary>
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
        new("Refund", "Refunds an order.", [new("orderId", "The order.", true, Constants.Scopes.Request), new("amount", "The amount.", true, Constants.Scopes.Request)],
            RequiresExecutionApproval: true, Returns: [new("status", "The status.")])
    ];

    /// <summary>Runs the startup check on the sample agent's tools.</summary>
    private static List<string> Validate(Records.WorkflowDefinition[] workflows, bool usesMcpServer = false)
        => HandlesIntentAgentRegistryService.ValidateWorkflows("sample", workflows, DeclaredTools(), usesMcpServer);

    /// <summary>Asserts that the check refuses the declaration with a message holding the given text and naming the intent.</summary>
    private static void AssertRefused(Records.WorkflowDefinition[] workflows, string expected)
        => Assert.Contains(Validate(workflows), error => error.Contains(expected, StringComparison.Ordinal) && error.Contains("'sample'", StringComparison.Ordinal));

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

        public Task<RefundResult> Refund(string orderId, string amount)
        {
            Calls.Add($"Refund:{orderId}:{amount}");
            return Task.FromResult(new RefundResult("refunded"));
        }
    }

    /// <summary>The class that carries the attributes of the sample agent.</summary>
    [HandlesIntent("inventory")]
    [RequiresLLMTier(Records.LLMTier.Efficiency)]
    private sealed class InventoryAgentMarker;

    /// <summary>
    /// The sample agent as the adapter really builds it: its prompt and tools resolved from a declaration made
    /// here, its model replaced by a script that records the tools each call was offered.
    /// </summary>
    private sealed class AgentUnderTest
    {
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
        public static async Task<AgentUnderTest> CreateAsync(params Records.WorkflowDefinition[] workflows)
        {
            InventoryTools.Calls.Clear();
            AgentUnderTest under = new AgentUnderTest();

            List<Dictionary<string, object>> properties = [new() { [Constants.PromptProperties.Tools] = JsonSerializer.SerializeToElement(DeclaredTools()) }];
            if (workflows.Length > 0)
                properties.Add(new() { [Constants.PromptProperties.Workflows] = JsonSerializer.SerializeToElement(workflows) });
            Records.Prompt prompt = new("inventory", "INTENT", "AGENT", "Sell plants.", "Answer plainly.", "Plain text.", null, "en-US", "1", properties);

            ConfigurationPromptResolverService resolver = new ConfigurationPromptResolverService(new SampleDomain(prompt));
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Morgana:HistoryReducer:Enabled"] = "false", ["Morgana:AgentToAgent:Enabled"] = "false" })
                .Build();

            MorganaAgentAdapter adapter = new MorganaAgentAdapter(
                new SampleLlm(under.Model),
                resolver,
                new ConfigurationPromptComposerService(resolver),
                new SampleToolRegistry(),
                null!,
                new HistoryReducerService(configuration, NullLogger.Instance),
                new NoDust(),
                null!,
                configuration,
                NullLogger.Instance);

            (AIAgent builtAgent, MorganaAIContextProvider provider, MorganaChatHistoryProvider history) =
                await adapter.CreateAgentAsync(typeof(InventoryAgentMarker), "conversation-1", () => under.Session);

            under.agent = builtAgent;
            under.Provider = provider;
            under.History = history;
            under.Session = await builtAgent.CreateSessionAsync();

            return under;
        }

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
        /// </summary>
        public async Task RunQuoteTurnAsync()
        {
            Model.Enqueue([new TextContent("Starting."), Call("LaunchWorkflow", """{"workflow":"PlaceOrder"}""")]);
            Model.Enqueue([Call("CreatePurchaseOrder", """{"item":"rose"}""")]);
            Model.Enqueue(
            [
                new TextContent("Here is the quote."),
                Call("Reply", """{"awaits":"action_choice","userIsLeaving":false,"actions":[{"tool":"ConfirmOrder","label":"Confirm","value":"confirm"},{"tool":"Stock","label":"Stock","value":"stock"}]}""")
            ]);

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

    /// <summary>Finds the sample tools for the sample intent.</summary>
    private sealed class SampleToolRegistry : IToolRegistryService
    {
        public Type? FindToolTypeForIntent(string intent) => intent == "inventory" ? typeof(InventoryTools) : null;

        public IReadOnlyDictionary<string, Type> GetAllRegisteredTools() => new Dictionary<string, Type> { ["inventory"] = typeof(InventoryTools) };
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
