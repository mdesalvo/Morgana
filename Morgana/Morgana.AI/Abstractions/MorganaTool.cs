using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Providers;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base class for agent tools: context-scoped parameters are resolved by <c>MorganaToolAdapter</c> before a tool
/// method is reached. Domain agents extend this class. Uses ToolContext factory
/// (lazy-evaluated per tool invocation) to access in-flight AgentSession without exposing
/// it to LLM schema inspection (session never appears in method signatures).
/// </summary>
/// <remarks>
/// <para>A domain tool is declared in one place: its method on the subclass. Every public instance method
/// that the subclass declares is a tool: <c>[Description]</c> on the method is what the model reads,
/// <c>[RequiresApproval]</c> says whether the user must approve each call. Every parameter carries
/// <c>[Description]</c> and <c>[ToolParameter]</c>. A parameter is required when it has no default value.
/// A <c>Context</c> parameter must be a required <c>string</c>, since the context holds untyped text. Only
/// a <c>Context</c> parameter may be shared. The method returns a typed record whose properties carry
/// <c>[Description]</c> and whose nullable <c>Error</c> property, when present, marks a failed call.
/// A helper is not public.</para>
/// <para>What a tool RETURNS is read by the model as the fourth voice in its prompt, after the framework
/// layer, the domain layer and the tool descriptions. It is the one voice with no declared
/// precedence, because it arrives mid-turn from outside the composed prompt. So a return value
/// states FACTS about the data and the record: what was written, what was not, what this response
/// does and does not carry. It never instructs behaviour ("tell the user to…", "offer to…", "call X
/// only after…"): behaviour is settled above and a tool restating it is a second author of a rule
/// it does not own — where the two ever drift apart, the model has no way to tell which one binds.
/// A tool that needs the agent to behave a certain way is asking for a line of domain
/// <c>Instructions</c>, or for a global policy where it holds for every domain. Free-text in tool
/// output is also never a grant of capability: see the <c>ToolGrounding</c> policy in morgana.json.</para>
/// </remarks>
public class MorganaTool
{
    /// <summary>Logger for tool-level diagnostics.</summary>
    protected readonly ILogger toolLogger;

    /// <summary>
    /// Factory that returns the provider + session pair for the current turn.
    /// Evaluated lazily on each tool invocation so it always reflects the active <see cref="AgentSession"/>.
    /// </summary>
    protected readonly Func<ToolContext> getToolContext;

    /// <summary>
    /// Initializes a new instance of <see cref="MorganaTool"/>.
    /// </summary>
    /// <param name="toolLogger">Logger for tool diagnostics.</param>
    /// <param name="getToolContext">
    /// Factory returning the <see cref="ToolContext"/> for the current turn.
    /// Typically wired as <c>() =&gt; new ToolContext(aiContextProvider, CurrentSession!, conversationId)</c>
    /// inside the concrete agent constructor.
    /// </param>
    public MorganaTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext)
    {
        // The factory is kept unevaluated: the session it reads exists only while a turn runs.
        this.toolLogger = toolLogger;
        this.getToolContext = getToolContext;
    }

    // =========================================================================
    // TOOL CONTEXT
    // =========================================================================

    /// <summary>
    /// Pairs the <see cref="MorganaAIContextProvider"/> singleton with the current <see cref="AgentSession"/>.
    /// Returned by the <c>getToolContext</c> factory on each tool invocation.
    /// </summary>
    /// <remarks>
    /// <para>Bundling provider and session in a struct keeps tool method signatures clean:
    /// the LLM sees only the declared parameters, not the infrastructure objects required
    /// to execute the call.</para>
    /// <para>The factory is evaluated lazily at call time, so <see cref="Session"/> always
    /// reflects the in-flight turn (see <see cref="MorganaAgent.CurrentSession"/>).</para>
    /// </remarks>
    public readonly struct ToolContext
    {
        /// <summary>The singleton context provider for this agent.</summary>
        public MorganaAIContextProvider Provider { get; }

        /// <summary>The active session for the current turn.</summary>
        public AgentSession Session { get; }

        /// <summary>
        /// Conversation identifier this agent instance is scoped to. Unlike <see cref="Session"/>
        /// (an opaque Microsoft.Agents.AI object with no conversation concept of its own), this is
        /// the actual Akka-assigned conversationId — never writable by the LLM, unlike context
        /// variables such as customerCode. Domain tools that need to scope persisted state to "this
        /// conversation" (as opposed to a user-supplied, LLM-writable identifier) should use this,
        /// not a context variable.
        /// </summary>
        public string ConversationId { get; }

        /// <summary>
        /// The agent's tools that a user action may lead to: its own and its MCP tools, never Reply nor a
        /// colleague. Null when nobody declared them, which leaves every action as the model wrote it.
        /// </summary>
        public IReadOnlyCollection<string>? ActionableToolNames { get; }

        /// <summary>
        /// The running workflow's step when it names two or more tools, with those tools in declared order.
        /// Null outside a workflow and at a step with a single tool.
        /// </summary>
        public (string Workflow, string Step, IReadOnlyList<string> Tools)? ChoiceStep { get; }

        /// <summary>
        /// Initializes a new <see cref="ToolContext"/> pairing the agent's context provider
        /// with the session and conversationId for the in-flight turn.
        /// </summary>
        /// <param name="provider">The singleton context provider for the agent.</param>
        /// <param name="session">The active agent session for the current turn.</param>
        /// <param name="conversationId">The Akka conversation identifier this agent instance is scoped to.</param>
        /// <param name="actionableToolNames">The agent's tools that a user action may lead to, if known.</param>
        /// <param name="choiceStep">The running workflow's choice step, if it stands at one.</param>
        public ToolContext(
            MorganaAIContextProvider provider,
            AgentSession session,
            string conversationId,
            IReadOnlyCollection<string>? actionableToolNames = null,
            (string Workflow, string Step, IReadOnlyList<string> Tools)? choiceStep = null)
        {
            // The pair is fixed for the turn that built it, so a tool never sees the session of another turn.
            Provider = provider;
            Session = session;
            ConversationId = conversationId;
            ActionableToolNames = actionableToolNames;
            ChoiceStep = choiceStep;
        }
    }
}