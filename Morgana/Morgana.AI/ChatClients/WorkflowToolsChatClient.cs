using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Providers;

namespace Morgana.AI.ChatClients;

/// <summary>
/// DelegatingChatClient sitting between the model and the tool loop. On every model call it presents the
/// tools that the session's workflow state allows: it is what keeps the steps of a workflow in order by the
/// absence of the tools that do not belong to the current one.
/// </summary>
/// <remarks>
/// Below the tool loop because the tools that a single run is given add to the agent's own instead of
/// replacing them. Read on every call, so a step reached halfway through a turn is offered from the very next call.
/// </remarks>
public sealed class WorkflowToolsChatClient : DelegatingChatClient
{
    /// <summary>Returns the agent's current session, the one whose workflow state decides the tools.</summary>
    private readonly Func<AgentSession?> sessionAccessor;

    /// <summary>The store that the workflow position and the consultation mark are read from.</summary>
    private readonly MorganaAIContextProvider contextProvider;

    /// <summary>The workflows the agent declares.</summary>
    private readonly IReadOnlyList<Records.WorkflowDefinition> workflows;

    /// <summary>
    /// Wraps the model the agent's tool loop calls.
    /// </summary>
    /// <param name="innerClient">The client below, which sends the tools to the model.</param>
    /// <param name="sessionAccessor">Returns the agent's current session.</param>
    /// <param name="contextProvider">The agent's context store.</param>
    /// <param name="workflows">The workflows the agent declares; none leaves every call untouched.</param>
    public WorkflowToolsChatClient(
        IChatClient innerClient,
        Func<AgentSession?> sessionAccessor,
        MorganaAIContextProvider contextProvider,
        IReadOnlyList<Records.WorkflowDefinition> workflows) : base(innerClient)
    {
        this.sessionAccessor = sessionAccessor;
        this.contextProvider = contextProvider;
        this.workflows = workflows;
    }

    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(chatMessages, Present(options), cancellationToken);

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(chatMessages, Present(options), cancellationToken);

    /// <summary>
    /// Rewrites the options' tools for the workflow state of the session; the caller's options are never mutated.
    /// </summary>
    private ChatOptions? Present(ChatOptions? options)
    {
        // An agent declaring no workflow is offered exactly the tools it was given.
        if (workflows.Count == 0 || options?.Tools is not { Count: > 0 } tools)
            return options;

        AgentSession? session = sessionAccessor();
        Records.WorkflowPosition? position = session is null ? null : contextProvider.GetWorkflowPosition(session);
        (Records.WorkflowDefinition Definition, Records.WorkflowStep Step)? running = position?.Resolve(workflows);

        List<AITool> presented;
        if (running is null)
        {
            // A consultation answers a colleague and never takes the user's turn, so it never opens a wizard.
            bool servingConsultation = session is not null
                && contextProvider.GetVariable(session, Constants.ContextKeys.ServingConsultation) is not null;
            if (!servingConsultation)
                return options;

            presented = [.. tools.Where(tool => tool.Name != Constants.Tools.LaunchWorkflow)];
        }
        else
        {
            HashSet<string> signature = running.Value.Definition.ToolSignature();
            presented = [];

            foreach (AITool tool in tools)
            {
                // Closing the turn and consulting a colleague belong to no step: they are always offered.
                if (tool.Name == Constants.Tools.Reply
                    || tool.Name.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal))
                    presented.Add(tool);
                else if (tool.Name == Constants.Tools.LaunchWorkflow)
                    continue;
                else if (signature.Contains(tool.Name))
                {
                    // Of the workflow's own tools only the current step's are offered, without what the framework binds.
                    if (running.Value.Step.Tools.Contains(tool.Name))
                        presented.Add(WithoutBoundParameters(tool, position!.Arguments.Keys));
                }

                // Outside the signature a tool is a private method of the workflow: offered for answers
                // on the side, except where it needs approval, since a binding act there would be a second workflow.
                else if (tool is not ApprovalRequiredAIFunction)
                    presented.Add(tool);
            }
        }

        ChatOptions presentedOptions = options.Clone();
        presentedOptions.Tools = presented;

        return presentedOptions;
    }

    /// <summary>
    /// Takes the parameters that the framework fills in out of the schema the model reads.
    /// </summary>
    /// <param name="tool">A tool of the current step.</param>
    /// <param name="boundParameters">The parameters that the step binds.</param>
    private static AITool WithoutBoundParameters(AITool tool, IEnumerable<string> boundParameters)
    {
        if (tool is not AIFunction function)
            return tool;

        JsonObject schema = JsonNode.Parse(function.JsonSchema.GetRawText())!.AsObject();
        string[] hidden = [.. boundParameters.Where(parameter => schema["properties"] is JsonObject properties && properties.ContainsKey(parameter))];
        if (hidden.Length == 0)
            return tool;

        // Only a declaration reaches the model from here: the tool loop above runs the original function.
        // The approval wrapper is put back on top because the client below recognizes a tool needing approval by it.
        AIFunction trimmed = new SchemaTrimmedFunction(function, schema, hidden);
        return function is ApprovalRequiredAIFunction ? new ApprovalRequiredAIFunction(trimmed) : trimmed;
    }

    /// <summary>
    /// A tool presented with some of its parameters removed from the properties and from the required list of its schema.
    /// </summary>
    private sealed class SchemaTrimmedFunction : DelegatingAIFunction
    {
        /// <summary>The schema without the hidden parameters.</summary>
        private readonly JsonElement jsonSchema;

        /// <summary>
        /// Wraps a tool of the current step.
        /// </summary>
        /// <param name="innerFunction">The tool as the agent holds it.</param>
        /// <param name="schema">The tool's schema, parsed for editing.</param>
        /// <param name="hidden">The parameters to remove.</param>
        public SchemaTrimmedFunction(AIFunction innerFunction, JsonObject schema, IReadOnlyCollection<string> hidden) : base(innerFunction)
        {
            foreach (string parameter in hidden)
                schema["properties"]!.AsObject().Remove(parameter);

            if (schema["required"] is JsonArray required)
                schema["required"] = new JsonArray([.. required
                    .Where(name => !hidden.Contains(name!.GetValue<string>(), StringComparer.Ordinal))
                    .Select(name => name!.DeepClone())]);

            jsonSchema = JsonSerializer.SerializeToElement(schema);
        }

        /// <inheritdoc />
        public override JsonElement JsonSchema => jsonSchema;
    }
}
