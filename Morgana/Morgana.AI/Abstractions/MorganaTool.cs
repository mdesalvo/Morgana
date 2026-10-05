using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Providers;
using Morgana.Contracts;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base class for agent tools. Provides the Reply tool closing every turn; context-scoped parameters are resolved by <c>MorganaToolAdapter</c> before a tool
/// method is reached. Domain agents extend this class. Uses ToolContext factory
/// (lazy-evaluated per tool invocation) to access in-flight AgentSession without exposing
/// it to LLM schema inspection (session never appears in method signatures).
/// </summary>
/// <remarks>
/// What a tool RETURNS is read by the model as the fourth voice in its prompt, after the framework
/// layer, the domain layer and the tool descriptions. It is the one voice with no declared
/// precedence, because it arrives mid-turn from outside the composed prompt. So a return value
/// states FACTS about the data and the record: what was written, what was not, what this response
/// does and does not carry. It never instructs behaviour ("tell the user to…", "offer to…", "call X
/// only after…"): behaviour is settled above and a tool restating it is a second author of a rule
/// it does not own — where the two ever drift apart, the model has no way to tell which one binds.
/// A tool that needs the agent to behave a certain way is asking for a line of domain
/// <c>Instructions</c>, or for a global policy where it holds for every domain. Free-text in tool
/// output is also never a grant of capability: see the <c>ToolGrounding</c> policy in morgana.json.
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
    /// What Reply answers once the turn is closed. Read by <c>TurnClosingChatClient</c> to tell a
    /// closed turn from one whose Reply was refused.
    /// </summary>
    internal const string TurnClosed = "Turn closed.";

    /// <summary>Deepest card nesting the channels lay out: card, section, nested section.</summary>
    private const int MaxCardDepth = 3;

    /// <summary>Most components, nested ones included, a card may carry.</summary>
    private const int MaxCardComponents = 50;

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
        this.toolLogger = toolLogger;
        this.getToolContext = getToolContext;
    }

    // =========================================================================
    // TURN CLOSURE SYSTEM TOOL
    // =========================================================================

    /// <summary>
    /// Closes the agent's turn: what it ends waiting for, whether the user is leaving, the actions it
    /// offers and the card it presents. Recorded for <c>MorganaAgent</c> to read at the end of the turn.
    /// </summary>
    /// <param name="awaits">What the turn ends waiting for from the user.</param>
    /// <param name="userIsLeaving">True when the user's own message was a goodbye.</param>
    /// <param name="actions">The agent's own actions offered as buttons, if any.</param>
    /// <param name="card">The card presenting the turn's structured data, if any.</param>
    /// <returns>A fact for the model: the turn is closed or the reason its card was refused.</returns>
    public async Task<object> Reply(
        Records.AwaitedFromUser awaits,
        bool userIsLeaving,
        List<Records.ReplyAction>? actions = null,
        RichCard? card = null)
    {
        // A card the channel could not lay out is refused here and the turn stays open, so the
        // model reads why and calls Reply again with a card that fits.
        if (card is not null)
        {
            int depth = CalculateMaxDepth(card.Components, 1);
            if (depth > MaxCardDepth)
            {
                toolLogger.LogWarning("Reply refused a rich card nesting {Depth} levels (max {Max})", depth, MaxCardDepth);
                return $"Card refused: it nests {depth} levels and at most {MaxCardDepth} are allowed. The turn is not closed.";
            }

            int totalComponents = CountComponents(card.Components);
            if (totalComponents > MaxCardComponents)
            {
                toolLogger.LogWarning("Reply refused a rich card of {Count} components (max {Max})", totalComponents, MaxCardComponents);
                return $"Card refused: it holds {totalComponents} components and at most {MaxCardComponents} are allowed. The turn is not closed.";
            }
        }

        Records.TurnReply turnReply = new Records.TurnReply(awaits, userIsLeaving, actions ?? [], card);

        ToolContext ctx = getToolContext();
        await ctx.Provider.SetVariableAsync(ctx.Session, Constants.ContextKeys.TurnReply,
            JsonSerializer.Serialize(turnReply, Records.DefaultJsonSerializerOptions));

        // Nothing is left for the model to do once the turn is closed, so the tool loop ends here
        // instead of spending another call. Absent when the framework records a closure itself.
        if (FunctionInvokingChatClient.CurrentContext is { } invocation)
            invocation.Terminate = true;

        toolLogger.LogInformation(
            "LLM closed its turn via Reply: awaits={Awaits}, userIsLeaving={UserIsLeaving}, actions={Actions}, card={Card}",
            awaits, userIsLeaving, turnReply.Actions.Count, card?.Title ?? "(none)");

        return TurnClosed;
    }

    /// <summary>
    /// Calculates the maximum nesting depth of components in a card.
    /// Used by Reply to refuse a card nesting deeper than the channels lay out.
    /// </summary>
    /// <param name="components">List of card components to analyze</param>
    /// <param name="currentDepth">Current depth level (starts at 1)</param>
    /// <returns>Maximum depth found in the component tree</returns>
    private static int CalculateMaxDepth(List<CardComponent> components, int currentDepth)
    {
        int maxDepth = currentDepth;

        foreach (CardComponent component in components)
        {
            if (component is SectionComponent section)
            {
                int sectionDepth = CalculateMaxDepth(section.Components, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, sectionDepth);
            }
        }

        return maxDepth;
    }

    /// <summary>
    /// Counts total number of components recursively (including nested sections).
    /// Used by Reply to refuse a card larger than the channels lay out.
    /// </summary>
    /// <param name="components">List of card components to count</param>
    /// <returns>Total component count including all nested components</returns>
    private static int CountComponents(List<CardComponent> components)
    {
        int count = components.Count;

        foreach (CardComponent component in components)
        {
            if (component is SectionComponent section)
                count += CountComponents(section.Components);
        }

        return count;
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
        /// Initializes a new <see cref="ToolContext"/> pairing the agent's context provider
        /// with the session and conversationId for the in-flight turn.
        /// </summary>
        /// <param name="provider">The singleton context provider for the agent.</param>
        /// <param name="session">The active agent session for the current turn.</param>
        /// <param name="conversationId">The Akka conversation identifier this agent instance is scoped to.</param>
        public ToolContext(MorganaAIContextProvider provider, AgentSession session, string conversationId)
        {
            Provider = provider;
            Session = session;
            ConversationId = conversationId;
        }
    }
}