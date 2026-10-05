using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Providers;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting how the framework resolves a tool's context-scoped parameters: a value the
/// model passes is stored, one it omits is read from the session and one nobody holds keeps the
/// tool from running.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: no host turn, no model. What the model does with the outcome is the
/// <c>ContextHandlingTests</c> group's business; this one asserts the outcome itself, which no prompt
/// can bend.</para>
///
/// <para>The tool is the adapter's real wrapper around a real delegate, over a real session and the
/// real context provider. Only the model is absent, replaced by the arguments it would have sent.</para>
/// </remarks>
public sealed class ContextResolutionTests
{
    /// <summary>The context-scoped parameter under test, shared across agents like the examples' own.</summary>
    private const string CustomerCode = "customerCode";

    /// <summary>A request-scoped parameter beside it, which the framework never touches.</summary>
    private const string InvoiceId = "invoiceId";

    [Fact]
    public async Task Schema_releases_the_model_from_context_scoped_parameters_only()
    {
        ToolUnderTest tool = await ToolUnderTest.CreateAsync();

        // Still offered, so a value the user has just typed can be passed; no longer demanded.
        JsonElement schema = tool.Function.JsonSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty(CustomerCode, out _));
        string[] required = [.. schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()!)];
        Assert.DoesNotContain(CustomerCode, required);
        Assert.Contains(InvoiceId, required);
    }

    [Fact]
    public async Task Passed_value_is_used_stored_and_shared()
    {
        ToolUnderTest tool = await ToolUnderTest.CreateAsync();

        await tool.Function.InvokeAsync(new AIFunctionArguments { [CustomerCode] = "P994E", [InvoiceId] = "INV-0247" });

        Assert.Equal(("P994E", "INV-0247"), tool.ReceivedCall);
        Assert.Equal("P994E", tool.Provider.GetVariable(tool.Session, CustomerCode));
        Assert.Equal([(CustomerCode, (object)"P994E")], tool.SharedWrites);
    }

    [Fact]
    public async Task Omitted_value_is_read_from_the_session()
    {
        ToolUnderTest tool = await ToolUnderTest.CreateAsync();
        tool.Provider.MergeSharedContext(tool.Session, new Dictionary<string, object> { [CustomerCode] = "P994E" });

        await tool.Function.InvokeAsync(new AIFunctionArguments { [InvoiceId] = "INV-0247" });

        // Read, never written back: a value already held is not a new fact for the registry.
        Assert.Equal(("P994E", "INV-0247"), tool.ReceivedCall);
        Assert.Empty(tool.SharedWrites);
    }

    [Fact]
    public async Task Value_restored_from_a_persisted_session_reaches_the_tool_as_text()
    {
        ToolUnderTest tool = await ToolUnderTest.CreateAsync();
        tool.Provider.MergeSharedContext(tool.Session,
            new Dictionary<string, object> { [CustomerCode] = JsonSerializer.SerializeToElement("P994E") });

        await tool.Function.InvokeAsync(new AIFunctionArguments { [InvoiceId] = "INV-0247" });

        Assert.Equal(("P994E", "INV-0247"), tool.ReceivedCall);
    }

    [Fact]
    public async Task Missing_value_keeps_the_tool_from_running_and_names_what_is_missing()
    {
        ToolUnderTest tool = await ToolUnderTest.CreateAsync();

        // A blank argument is the model naming the parameter with nothing in it: still missing.
        object? result = await tool.Function.InvokeAsync(new AIFunctionArguments { [CustomerCode] = " ", [InvoiceId] = "INV-0247" });

        Assert.Null(tool.ReceivedCall);
        Assert.Contains(CustomerCode, result?.ToString());
        Assert.Null(tool.Provider.GetVariable(tool.Session, CustomerCode));
    }

    [Fact]
    public async Task Colleague_answering_a_consultation_uses_the_value_and_stores_nothing()
    {
        ToolUnderTest tool = await ToolUnderTest.CreateAsync();
        await tool.Provider.SetVariableAsync(tool.Session, Constants.ContextKeys.ServingConsultation, true);

        await tool.Function.InvokeAsync(new AIFunctionArguments { [CustomerCode] = "P994E", [InvoiceId] = "INV-0247" });

        // The exchange leaves the conversation as it found it: answered, with nothing kept.
        Assert.Equal(("P994E", "INV-0247"), tool.ReceivedCall);
        Assert.Null(tool.Provider.GetVariable(tool.Session, CustomerCode));
        Assert.Empty(tool.SharedWrites);
    }

    /// <summary>
    /// One context-scoped tool as an agent would hold it: declared, wrapped by the adapter and bound
    /// to a live session whose shared writes are recorded instead of persisted.
    /// </summary>
    private sealed class ToolUnderTest
    {
        /// <summary>The function the model would call.</summary>
        public AIFunction Function { get; private set; } = null!;

        /// <summary>The agent's context store, holding what the session knows.</summary>
        public MorganaAIContextProvider Provider { get; } =
            new MorganaAIContextProvider(NullLogger.Instance, [CustomerCode]);

        /// <summary>The in-flight session the tool resolves against.</summary>
        public AgentSession Session { get; private set; } = null!;

        /// <summary>The arguments the tool method received; null when it never ran.</summary>
        public (string CustomerCode, string InvoiceId)? ReceivedCall { get; private set; }

        /// <summary>What reached the conversation-scoped shared registry.</summary>
        public List<(string Name, object Value)> SharedWrites { get; } = [];

        /// <summary>
        /// Builds the tool over a session of an agent that never talks to a model.
        /// </summary>
        public static async Task<ToolUnderTest> CreateAsync()
        {
            ToolUnderTest tool = new ToolUnderTest();

            // The registry write a real agent persists, captured so the test can see it happened.
            tool.Provider.OnSharedContextUpdate = (name, value) =>
            {
                tool.SharedWrites.Add((name, value));
                return Task.CompletedTask;
            };

            tool.Session = await new ChatClientAgent(new SilentChatClient()).CreateSessionAsync();

            Records.ToolDefinition definition = new Records.ToolDefinition(
                "GetInvoiceDetails",
                "Returns one invoice of the customer.",
                [
                    new Records.ToolParameter(CustomerCode, "The customer's code.", true, Constants.Scopes.Context, Shared: true),
                    new Records.ToolParameter(InvoiceId, "The invoice to show.", true, Constants.Scopes.Request)
                ]);

            Func<string, string, Task<object>> getInvoiceDetails = (customerCode, invoiceId) =>
            {
                tool.ReceivedCall = (customerCode, invoiceId);
                return Task.FromResult<object>("invoice");
            };

            MorganaToolAdapter adapter = new MorganaToolAdapter(
                NullLogger.Instance,
                () => new MorganaTool.ToolContext(tool.Provider, tool.Session, "context-resolution"));
            adapter.AddTool(definition.Name, getInvoiceDetails, definition);
            tool.Function = await adapter.CreateFunctionAsync(definition.Name);

            return tool;
        }
    }

    /// <summary>
    /// The model of an agent that is only ever asked for a session. Any call to it is a test fault.
    /// </summary>
    private sealed class SilentChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No model is reached by this group");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No model is reached by this group");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
