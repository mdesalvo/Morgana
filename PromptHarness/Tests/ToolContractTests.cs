using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Services;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting that a native tool is declared completely on its class and that the domain shipped
/// with the repository honours it.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: the startup check is exercised on tool types built for the purpose,
/// since a plugin that is wrong on purpose would be a different instrument from the one that boots.</para>
///
/// <para>The names the model reads are the serializer's camelCase, so the projected return fields are
/// compared as <c>orderId</c> where the record spells <c>OrderId</c>.</para>
/// </remarks>
public sealed class ToolContractTests
{
    /// <summary>The intent named in the messages of every case.</summary>
    private const string Intent = "sample";

    [Fact]
    public void A_completely_declared_class_is_accepted()
        => Assert.Empty(Validate(typeof(HonestTool)));

    [Fact]
    public void A_tool_returning_a_string_is_refused()
    {
        List<string> errors = Validate(typeof(StringTool));

        Assert.Contains(errors, error => error.Contains("typed record", StringComparison.Ordinal) && error.Contains("Run", StringComparison.Ordinal) && error.Contains(Intent, StringComparison.Ordinal));
    }

    [Fact]
    public void A_tool_returning_a_record_without_properties_is_refused()
        => Assert.Contains(Validate(typeof(EmptyRecordTool)), error => error.Contains("no properties", StringComparison.Ordinal));

    [Fact]
    public void A_tool_method_without_a_description_is_refused()
    {
        Assert.Contains(Validate(typeof(NoMethodDescriptionTool)), error => error.Contains("[Description]", StringComparison.Ordinal) && error.Contains("'Run'", StringComparison.Ordinal));
        Assert.Contains(Validate(typeof(BlankMethodDescriptionTool)), error => error.Contains("[Description]", StringComparison.Ordinal) && error.Contains("'Run'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tool_method_without_an_approval_declaration_is_refused()
        => Assert.Contains(Validate(typeof(NoApprovalTool)), error => error.Contains("[RequiresApproval]", StringComparison.Ordinal) && error.Contains("'Run'", StringComparison.Ordinal));

    [Fact]
    public void A_parameter_without_a_description_is_refused()
    {
        Assert.Contains(Validate(typeof(NoParameterDescriptionTool)), error => error.Contains("[Description]", StringComparison.Ordinal) && error.Contains("'code'", StringComparison.Ordinal));
        Assert.Contains(Validate(typeof(BlankParameterDescriptionTool)), error => error.Contains("[Description]", StringComparison.Ordinal) && error.Contains("'code'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_parameter_without_a_scope_declaration_is_refused()
        => Assert.Contains(Validate(typeof(NoScopeTool)), error => error.Contains("[ToolParameter]", StringComparison.Ordinal) && error.Contains("'code'", StringComparison.Ordinal));

    [Theory]
    [InlineData(typeof(RequestRequiredTool))]
    [InlineData(typeof(RequestOptionalTool))]
    [InlineData(typeof(ContextRequiredTool))]
    [InlineData(typeof(ContextSharedRequiredTool))]
    public void An_allowed_parameter_combination_is_accepted(Type toolType)
        => Assert.Empty(Validate(toolType));

    [Fact]
    public void A_shared_request_parameter_is_refused()
        => Assert.Contains(Validate(typeof(RequestSharedTool)), error => error.Contains("'code'", StringComparison.Ordinal) && error.Contains("shared", StringComparison.Ordinal));

    [Theory]
    [InlineData(typeof(ContextOptionalTool))]
    [InlineData(typeof(ContextSharedOptionalTool))]
    public void A_context_parameter_with_a_default_is_refused(Type toolType)
        => Assert.Contains(Validate(toolType), error => error.Contains("'code'", StringComparison.Ordinal) && error.Contains("default", StringComparison.Ordinal));

    [Fact]
    public void A_context_parameter_that_is_not_a_string_is_refused()
        => Assert.Contains(Validate(typeof(ContextIntegerTool)), error => error.Contains("'count'", StringComparison.Ordinal) && error.Contains("string", StringComparison.Ordinal));

    [Fact]
    public void Two_methods_under_one_tool_name_are_refused()
        => Assert.Contains(Validate(typeof(OverloadedTool)), error => error.Contains("'Run'", StringComparison.Ordinal) && error.Contains("more than one", StringComparison.Ordinal));

    [Fact]
    public void A_failure_field_that_cannot_be_null_is_refused()
        => Assert.Contains(Validate(typeof(NonNullableFailureTool)), error => error.Contains("'error'", StringComparison.Ordinal) && error.Contains("null", StringComparison.Ordinal));

    [Fact]
    public void A_prompt_still_declaring_tools_is_refused()
    {
        Records.Prompt leftover = PromptWith(new Dictionary<string, object> { [Constants.PromptProperties.Tools] = JsonSerializer.SerializeToElement(new[] { "any" }) });
        Records.Prompt leftoverEmpty = PromptWith(new Dictionary<string, object> { [Constants.PromptProperties.Tools] = JsonSerializer.SerializeToElement(Array.Empty<string>()) });
        Records.Prompt clean = PromptWith(new Dictionary<string, object>());

        List<string> errors = HandlesIntentAgentRegistryService.ValidateNoDeclarationsInJson([leftover, leftoverEmpty, clean]);

        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Contains("\"Tools\"", error, StringComparison.Ordinal));
        Assert.Empty(HandlesIntentAgentRegistryService.ValidateNoDeclarationsInJson([clean]));
    }

    [Fact]
    public void A_prompt_still_declaring_workflows_is_refused()
    {
        Records.Prompt leftover = PromptWith(new Dictionary<string, object> { [Constants.PromptProperties.Workflows] = JsonSerializer.SerializeToElement(new[] { "any" }) });
        Records.Prompt leftoverEmpty = PromptWith(new Dictionary<string, object> { [Constants.PromptProperties.Workflows] = JsonSerializer.SerializeToElement(Array.Empty<string>()) });

        List<string> errors = HandlesIntentAgentRegistryService.ValidateNoDeclarationsInJson([leftover, leftoverEmpty]);

        // An empty key is refused as well as a filled one: the key itself is what is retired.
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Contains("\"Workflows\"", error, StringComparison.Ordinal));
    }

    [Fact]
    public void A_prompt_declaring_both_keys_gets_one_message_for_each()
    {
        Records.Prompt leftover = PromptWith(new Dictionary<string, object>
        {
            [Constants.PromptProperties.Tools] = JsonSerializer.SerializeToElement(new[] { "any" }),
            [Constants.PromptProperties.Workflows] = JsonSerializer.SerializeToElement(new[] { "any" })
        });

        List<string> errors = HandlesIntentAgentRegistryService.ValidateNoDeclarationsInJson([leftover]);

        Assert.Equal(2, errors.Count);
        Assert.Single(errors, error => error.Contains("\"Tools\"", StringComparison.Ordinal));
        Assert.Single(errors, error => error.Contains("\"Workflows\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_projection_reads_the_class_in_declaration_order()
    {
        IReadOnlyList<Records.ToolDefinition> definitions = ProvidesToolForIntentRegistryService.ProjectToolDefinitions(typeof(ProjectedTool));

        Assert.Equal(["Second", "First"], definitions.Select(definition => definition.Name));

        Records.ToolDefinition first = definitions[1];
        Assert.Equal("Runs first.", first.Description);
        Assert.True(first.RequiresExecutionApproval);
        Assert.False(first.Reserved);
        Assert.Equal(
            [
                new Records.ToolParameter("code", "A code", true, Constants.Scopes.Context, true),
                new Records.ToolParameter("note", "A note", false, Constants.Scopes.Request)
            ],
            first.Parameters);
        Assert.Equal(
            [
                new Records.ToolReturn("error", "Why the call failed", Failure: true),
                new Records.ToolReturn("orderId", "The order"),
                new Records.ToolReturn("quantity", "")
            ],
            first.Returns);
    }

    [Fact]
    public void The_projection_leaves_out_inherited_overridden_special_and_non_public_members()
        => Assert.Equal(["Run"], ProvidesToolForIntentRegistryService.GetToolMethods(typeof(DerivedTool)).Select(method => method.Name));

    [Fact]
    public void The_projection_of_a_malformed_class_does_not_throw()
    {
        IReadOnlyList<Records.ToolDefinition> definitions = ProvidesToolForIntentRegistryService.ProjectToolDefinitions(typeof(NoScopeTool));

        Assert.Equal("code", Assert.Single(Assert.Single(definitions).Parameters).Name);
    }

    [Fact]
    public async Task The_derived_schema_is_the_one_the_function_publishes()
    {
        Func<string, Task<SampleResult>> run = new HonestTool().Run;
        Records.ToolDefinition definition = new("Run", "Runs.", [new Records.ToolParameter("code", "A code", true, Constants.Scopes.Request)], Returns: HonestReturns);
        MorganaToolAdapter adapter = new MorganaToolAdapter().AddTool(definition.Name, run, definition);

        AIFunction function = await adapter.CreateFunctionAsync(definition.Name);

        JsonElement derived = MorganaToolAdapter.CreateReturnSchema(typeof(SampleResult));
        Assert.Equal(function.ReturnJsonSchema!.Value.GetRawText(), derived.GetRawText());

        // The names the model reads, nested records included: camelCase whatever the C# spelling.
        Assert.Equal(["error", "orderId", "quantity", "line"], [.. derived.GetProperty("properties").EnumerateObject().Select(property => property.Name)]);
        JsonElement line = derived.GetProperty("properties").GetProperty("line");
        Assert.Contains("sku", line.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Sku", line.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_serialized_result_keeps_camelCase_names_nested_ones_included()
    {
        // MarshalResult writes the record as the text that the model reads: camelCase and compact, since
        // a provider passes a text result through exactly as it receives it.
        Func<string, Task<SampleResult>> run = new HonestTool().Run;
        Records.ToolDefinition definition = new("Run", "Runs.", [new Records.ToolParameter("code", "A code", true, Constants.Scopes.Request)], Returns: HonestReturns);
        AIFunction function = new MorganaToolAdapter().AddTool(definition.Name, run, definition).CreateFunctionAsync(definition.Name).GetAwaiter().GetResult();

        string result = Assert.IsType<string>(function.InvokeAsync(new AIFunctionArguments { ["code"] = "x" }).AsTask().GetAwaiter().GetResult());

        Assert.Equal("""{"orderId":"ORD-1","quantity":2,"line":{"sku":"RSE-100","quantity":2}}""", result);
    }

    [Fact]
    public void The_shipped_domain_declares_every_tool_on_its_class()
    {
        // Read the way the framework does: the prompts that the host loads and the catalog of each tool class.
        List<Records.Prompt> prompts = new EmbeddedAgentConfigurationService(NullLogger.Instance).GetAgentPromptsAsync().GetAwaiter().GetResult();
        ProvidesToolForIntentRegistryService toolRegistry = new(NullLogger.Instance);
        int toolsChecked = 0;

        Assert.Empty(HandlesIntentAgentRegistryService.ValidateNoDeclarationsInJson(prompts));

        foreach (Records.Prompt prompt in prompts.Where(candidate => toolRegistry.FindToolTypeForIntent(candidate.ID) is not null))
        {
            Assert.Empty(HandlesIntentAgentRegistryService.ValidateToolContract(prompt.ID, toolRegistry.FindToolTypeForIntent(prompt.ID)!));

            foreach (Records.ToolDefinition tool in toolRegistry.GetToolDefinitions(prompt.ID))
            {
                toolsChecked++;
                Assert.True(tool.Returns is { Count: > 0 }, $"{prompt.ID}.{tool.Name} projects no Returns");
                Assert.All(tool.Returns!, field => Assert.False(string.IsNullOrWhiteSpace(field.Description), $"{prompt.ID}.{tool.Name}.{field.Name} has no description"));

                // The failure marker is the wire name of the nullable property: one spelled differently would leave the flag false.
                Assert.Equal(tool.Returns!.Any(field => field.Name == "error"), tool.Returns!.Any(field => field.Failure));
            }
        }

        Assert.True(toolsChecked > 0, "The shipped domain was not found: no tool was checked");
    }

    /// <summary>Runs the startup check on a tool class.</summary>
    private static List<string> Validate(Type toolType)
        => HandlesIntentAgentRegistryService.ValidateToolContract(Intent, toolType);

    /// <summary>A domain prompt carrying the given additional properties and nothing else of note.</summary>
    private static Records.Prompt PromptWith(Dictionary<string, object> properties)
        => new("sample", "Target.", "Instructions.", "Formatting.", null, null, "en-US", "1")
        {
            AdditionalProperties = [properties]
        };

    /// <summary>The declaration that matches <see cref="HonestTool"/> field for field.</summary>
    private static readonly Records.ToolReturn[] HonestReturns =
    [
        new("error", "Why the call failed", Failure: true),
        new("orderId", "The order"),
        new("quantity", "How many"),
        new("line", "The line ordered")
    ];

    /// <summary>What the honest tool returns: a failure that is null on success and a nested record.</summary>
    public sealed record SampleResult(string? Error = null, string? OrderId = null, long? Quantity = null, SampleLine? Line = null);

    /// <summary>The nested record of <see cref="SampleResult"/>.</summary>
    public sealed record SampleLine(string Sku, int Quantity);

    /// <summary>A record whose failure field cannot be left empty.</summary>
    public sealed record NonNullableFailureResult(string Error, string? OrderId = null);

    /// <summary>A record with no property for the model to read.</summary>
    public sealed record EmptyResult;

    /// <summary>A record whose properties carry descriptions, one of them left bare.</summary>
    public sealed record DescribedResult(
        [Description("Why the call failed")] string? Error = null,
        [Description("The order")] string? OrderId = null,
        long? Quantity = null);

    /// <summary>A tool declared completely.</summary>
    public sealed class HonestTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Request)] string code)
            => Task.FromResult(new SampleResult(OrderId: "ORD-1", Quantity: 2, Line: new SampleLine("RSE-100", 2)));
    }

    /// <summary>A tool that hands the model a hand-serialized document.</summary>
    public sealed class StringTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<string> Run([Description("A code")] [ToolParameter(Records.ToolScope.Request)] string code) => Task.FromResult("{}");
    }

    /// <summary>A tool returning a record that has no property.</summary>
    public sealed class EmptyRecordTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<EmptyResult> Run() => Task.FromResult(new EmptyResult());
    }

    /// <summary>A tool whose method carries no description.</summary>
    public sealed class NoMethodDescriptionTool
    {
        [RequiresApproval(false)]
        public Task<SampleResult> Run() => Task.FromResult(new SampleResult());
    }

    /// <summary>A tool whose method description is blank.</summary>
    public sealed class BlankMethodDescriptionTool
    {
        [Description(" ")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run() => Task.FromResult(new SampleResult());
    }

    /// <summary>A tool that does not say whether it needs approval.</summary>
    public sealed class NoApprovalTool
    {
        [Description("Runs.")]
        public Task<SampleResult> Run() => Task.FromResult(new SampleResult());
    }

    /// <summary>A tool with a parameter that carries no description.</summary>
    public sealed class NoParameterDescriptionTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([ToolParameter(Records.ToolScope.Request)] string code) => Task.FromResult(new SampleResult());
    }

    /// <summary>A tool with a parameter whose description is blank.</summary>
    public sealed class BlankParameterDescriptionTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("")] [ToolParameter(Records.ToolScope.Request)] string code) => Task.FromResult(new SampleResult());
    }

    /// <summary>A tool with a parameter that does not declare its scope.</summary>
    public sealed class NoScopeTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] string code) => Task.FromResult(new SampleResult());
    }

    /// <summary>A request parameter the model must supply.</summary>
    public sealed class RequestRequiredTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Request)] string code) => Task.FromResult(new SampleResult());
    }

    /// <summary>A request parameter with a default.</summary>
    public sealed class RequestOptionalTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Request)] string code = "x") => Task.FromResult(new SampleResult());
    }

    /// <summary>A request parameter marked as shared.</summary>
    public sealed class RequestSharedTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Request, shared: true)] string code) => Task.FromResult(new SampleResult());
    }

    /// <summary>A required context string that no other agent reads.</summary>
    public sealed class ContextRequiredTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Context)] string code) => Task.FromResult(new SampleResult());
    }

    /// <summary>A required context string that every agent reads.</summary>
    public sealed class ContextSharedRequiredTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Context, shared: true)] string code) => Task.FromResult(new SampleResult());
    }

    /// <summary>A context parameter with a default.</summary>
    public sealed class ContextOptionalTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Context)] string code = "x") => Task.FromResult(new SampleResult());
    }

    /// <summary>A shared context parameter with a default.</summary>
    public sealed class ContextSharedOptionalTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Context, shared: true)] string code = "x") => Task.FromResult(new SampleResult());
    }

    /// <summary>A context parameter that is not text.</summary>
    public sealed class ContextIntegerTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A count")] [ToolParameter(Records.ToolScope.Context)] int count) => Task.FromResult(new SampleResult());
    }

    /// <summary>Two methods under one tool name.</summary>
    public sealed class OverloadedTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run() => Task.FromResult(new SampleResult());

        [Description("Runs with a code.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run([Description("A code")] [ToolParameter(Records.ToolScope.Request)] string code) => Task.FromResult(new SampleResult());
    }

    /// <summary>A tool whose failure field is not nullable.</summary>
    public sealed class NonNullableFailureTool
    {
        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<NonNullableFailureResult> Run() => Task.FromResult(new NonNullableFailureResult("no"));
    }

    /// <summary>Two tools declared out of alphabetical order, the first with every kind of parameter.</summary>
    public sealed class ProjectedTool
    {
        [Description("Runs second.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Second() => Task.FromResult(new SampleResult());

        [Description("Runs first.")]
        [RequiresApproval(true)]
        public Task<DescribedResult> First(
            [Description("A code")] [ToolParameter(Records.ToolScope.Context, shared: true)] string code,
            [Description("A note")] [ToolParameter(Records.ToolScope.Request)] string? note = null)
            => Task.FromResult(new DescribedResult());
    }

    /// <summary>A tool class inheriting <c>Reply</c> and carrying a helper and a property beside its one tool.</summary>
    public sealed class DerivedTool(ILogger logger, Func<MorganaTool.ToolContext> context) : MorganaTool(logger, context)
    {
        /// <summary>A property whose accessors are public methods.</summary>
        public string Label { get; set; } = "";

        [Description("Runs.")]
        [RequiresApproval(false)]
        public Task<SampleResult> Run() => Task.FromResult(new SampleResult());

        /// <summary>Not public, so not a tool.</summary>
        private Task<SampleResult> Helper() => Task.FromResult(new SampleResult());

        /// <inheritdoc />
        public override string ToString() => Label;
    }
}
