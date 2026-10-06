using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Services;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting that what a native tool declares it returns in agents.json and what its method
/// returns are one contract. It also asserts that the domain shipped with the repository honours it.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: the startup check is exercised on tool types built for the purpose,
/// since a plugin that is wrong on purpose would be a different instrument from the one that boots.</para>
///
/// <para>The names are compared exactly as the model reads them, camelCase and case-sensitive: the
/// declaration spells the field <c>orderId</c> where the record spells it <c>OrderId</c>.</para>
/// </remarks>
public sealed class ToolContractTests
{
    /// <summary>The intent named in the messages of every case.</summary>
    private const string Intent = "sample";

    /// <summary>The declaration that matches <see cref="HonestTool"/> field for field.</summary>
    private static readonly Records.ToolReturn[] HonestReturns =
    [
        new("error", "Why the call failed", Failure: true),
        new("orderId", "The order"),
        new("quantity", "How many"),
        new("line", "The line ordered")
    ];

    [Fact]
    public void A_declaration_matching_the_record_is_accepted()
        => Assert.Empty(Validate(typeof(HonestTool), HonestReturns));

    [Fact]
    public void A_tool_returning_a_string_is_refused()
    {
        List<string> errors = Validate(typeof(StringTool), HonestReturns);

        Assert.Contains(errors, error => error.Contains("typed record", StringComparison.Ordinal) && error.Contains("Run", StringComparison.Ordinal) && error.Contains(Intent, StringComparison.Ordinal));
    }

    [Fact]
    public void A_tool_declaring_no_returns_is_refused()
    {
        Assert.Contains(Validate(typeof(HonestTool), null), error => error.Contains("Returns", StringComparison.Ordinal));
        Assert.Contains(Validate(typeof(HonestTool), []), error => error.Contains("Returns", StringComparison.Ordinal));
    }

    [Fact]
    public void A_declared_field_the_record_lacks_is_refused()
    {
        List<string> errors = Validate(typeof(HonestTool), [.. HonestReturns, new Records.ToolReturn("sealWord", "A word")]);

        Assert.Contains(errors, error => error.Contains("'sealWord'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_field_left_undeclared_is_refused()
    {
        List<string> errors = Validate(typeof(HonestTool), [.. HonestReturns.Where(field => field.Name != "quantity")]);

        Assert.Contains(errors, error => error.Contains("'quantity'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_field_is_compared_exactly_as_the_model_reads_it()
    {
        // The record's own spelling is not what the model reads: the serializer's camelCase is.
        List<string> errors = Validate(typeof(HonestTool), [.. HonestReturns.Where(field => field.Name != "orderId"), new Records.ToolReturn("OrderId", "The order")]);

        Assert.Contains(errors, error => error.Contains("'OrderId'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("'orderId'", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_failure_fields_are_refused()
    {
        List<string> errors = Validate(typeof(HonestTool), [.. HonestReturns.Select(field => field.Name == "orderId" ? field with { Failure = true } : field)]);

        Assert.Contains(errors, error => error.Contains("at most one", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failure_field_that_cannot_be_null_is_refused()
    {
        List<string> errors = Validate(typeof(NonNullableFailureTool), [new Records.ToolReturn("error", "Why", Failure: true), new Records.ToolReturn("orderId", "The order")]);

        Assert.Contains(errors, error => error.Contains("'error'", StringComparison.Ordinal) && error.Contains("null", StringComparison.Ordinal));
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
    public void The_shipped_domain_declares_what_every_tool_returns()
    {
        // A key spelled wrongly in agents.json would bind to null without a sound: read the parsed
        // definitions the way the framework does and check every one carries its declaration.
        List<Records.Prompt> prompts = new EmbeddedAgentConfigurationService(NullLogger.Instance).GetAgentPromptsAsync().GetAwaiter().GetResult();
        ProvidesToolForIntentRegistryService toolRegistry = new(NullLogger.Instance);
        int toolsChecked = 0;

        foreach (Records.Prompt prompt in prompts.Where(candidate => toolRegistry.FindToolTypeForIntent(candidate.ID) is not null))
        {
            Records.ToolDefinition[] tools = prompt.GetAdditionalPropertyOrDefault<Records.ToolDefinition[]>(Constants.PromptProperties.Tools, []);

            foreach (Records.ToolDefinition tool in tools)
            {
                toolsChecked++;
                Assert.True(tool.Returns is { Count: > 0 }, $"{prompt.ID}.{tool.Name} declares no Returns");
                Assert.All(tool.Returns!, field => Assert.False(string.IsNullOrWhiteSpace(field.Description), $"{prompt.ID}.{tool.Name}.{field.Name} has no description"));

                // The failure marker is a key like any other: one spelled wrongly would leave the flag false.
                Assert.Equal(tool.Returns!.Any(field => field.Name == "error"), tool.Returns!.Any(field => field.Failure));
            }

            Assert.Empty(HandlesIntentAgentRegistryService.ValidateToolContract(prompt.ID, toolRegistry.FindToolTypeForIntent(prompt.ID)!, tools));
        }

        Assert.True(toolsChecked > 0, "The shipped domain was not found: no tool was checked");
    }

    /// <summary>
    /// Runs the startup check on <paramref name="toolType"/>'s <c>Run</c> method against a declaration.
    /// </summary>
    private static List<string> Validate(Type toolType, IReadOnlyList<Records.ToolReturn>? returns)
        => HandlesIntentAgentRegistryService.ValidateToolContract(Intent, toolType,
            [new Records.ToolDefinition("Run", "Runs.", [], Returns: returns)]);

    /// <summary>What the honest tool returns: a failure that is null on success and a nested record.</summary>
    public sealed record SampleResult(string? Error = null, string? OrderId = null, long? Quantity = null, SampleLine? Line = null);

    /// <summary>The nested record of <see cref="SampleResult"/>.</summary>
    public sealed record SampleLine(string Sku, int Quantity);

    /// <summary>A record whose failure field cannot be left empty.</summary>
    public sealed record NonNullableFailureResult(string Error, string? OrderId = null);

    /// <summary>A tool that honours its contract.</summary>
    public sealed class HonestTool
    {
        public Task<SampleResult> Run(string code) => Task.FromResult(new SampleResult(OrderId: "ORD-1", Quantity: 2, Line: new SampleLine("RSE-100", 2)));
    }

    /// <summary>A tool that hands the model a hand-serialized document.</summary>
    public sealed class StringTool
    {
        public Task<string> Run(string code) => Task.FromResult("{}");
    }

    /// <summary>A tool whose failure field is not nullable.</summary>
    public sealed class NonNullableFailureTool
    {
        public Task<NonNullableFailureResult> Run(string code) => Task.FromResult(new NonNullableFailureResult("no"));
    }
}
