using System.Globalization;
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
    /// Closes the agent's turn: what it ends waiting for, whether the user is leaving, the actions that it
    /// offers and the card that it presents. Recorded for <c>MorganaAgent</c> to read at the end of the turn.
    /// </summary>
    /// <param name="awaits">What the turn ends waiting for from the user.</param>
    /// <param name="userIsLeaving">True when the user's own message was a goodbye.</param>
    /// <param name="actions">The agent's own actions offered as buttons, if any.</param>
    /// <param name="card">The card presenting the turn's structured data, if any.</param>
    /// <returns>
    /// The named result the tool loop turns into its authored text: the turn closed or, with the turn
    /// left open for repair, the reason the closure was refused.
    /// </returns>
    public async Task<object> Reply(
        Records.AwaitedFromUser awaits,
        bool userIsLeaving,
        List<Records.ReplyAction>? actions = null,
        RichCard? card = null)
    {
        // Closed before a word of it was written, the turn would end mute: the model is sent back to
        // write first. Absent when the framework records a closure itself, which it does only after text.
        if (FunctionInvokingChatClient.CurrentContext is { } invocation && !HasTurnText(invocation.Messages))
            return new Records.FrameworkToolResult(Constants.ToolResults.ReplyWithoutText);

        // A card the channel could not lay out is refused and the turn stays open, so the model calls
        // Reply again with a card that fits.
        if (card is not null)
        {
            int depth = CalculateMaxDepth(card.Components, 1);
            if (depth > MaxCardDepth)
                return new Records.FrameworkToolResult(Constants.ToolResults.CardTooDeep, new Dictionary<string, string>
                {
                    [Constants.Placeholders.CardDepth] = depth.ToString(CultureInfo.InvariantCulture),
                    [Constants.Placeholders.Limit] = MaxCardDepth.ToString(CultureInfo.InvariantCulture)
                });

            int totalComponents = CountComponents(card.Components);
            if (totalComponents > MaxCardComponents)
                return new Records.FrameworkToolResult(Constants.ToolResults.CardTooLarge, new Dictionary<string, string>
                {
                    [Constants.Placeholders.CardComponents] = totalComponents.ToString(CultureInfo.InvariantCulture),
                    [Constants.Placeholders.Limit] = MaxCardComponents.ToString(CultureInfo.InvariantCulture)
                });
        }

        ToolContext ctx = getToolContext();

        // An action is a button that the user presses to have something done: one leading to no tool of this
        // agent would be a promise nothing keeps, so it never reaches the channel.
        List<Records.ReplyAction> offeredActions = actions ?? [];
        if (ctx.ActionableToolNames is { } actionableToolNames)
        {
            foreach (Records.ReplyAction discarded in offeredActions.Where(action => !actionableToolNames.Contains(action.Tool)))
                toolLogger.LogWarning("Reply discarded the action '{Label}': it leads to '{Tool}', which this agent does not have", discarded.Label, discarded.Tool);

            offeredActions = [.. offeredActions.Where(action => actionableToolNames.Contains(action.Tool))];
        }

        Records.TurnReply turnReply = new Records.TurnReply(awaits, userIsLeaving, offeredActions, card);

        await ctx.Provider.SetVariableAsync(ctx.Session, Constants.ContextKeys.TurnReply,
            JsonSerializer.Serialize(turnReply, Records.DefaultJsonSerializerOptions));

        // Nothing is left for the model to do once the turn is closed, so the tool loop ends here
        // instead of spending another call. Absent when the framework records a closure itself.
        if (FunctionInvokingChatClient.CurrentContext is { } closingInvocation)
            closingInvocation.Terminate = true;

        toolLogger.LogInformation(
            "LLM closed its turn via Reply: awaits={Awaits}, userIsLeaving={UserIsLeaving}, actions={Actions}, card={Card}",
            awaits, userIsLeaving, turnReply.Actions.Count, card?.Title ?? "(none)");

        return new Records.FrameworkToolResult(Constants.ToolResults.TurnClosed);
    }

    /// <summary>
    /// True when the turn under way has already written text to the user: an assistant message with
    /// text after the message that opened the turn.
    /// </summary>
    private static bool HasTurnText(IList<ChatMessage> messages)
    {
        int turnStart = messages.Count - 1;
        while (turnStart >= 0 && messages[turnStart].Role != ChatRole.User)
            turnStart--;

        return messages.Skip(turnStart + 1)
            .Any(message => message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text));
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
        /// The agent's tools that a user action may lead to: its own and its MCP tools, never Reply nor a
        /// colleague. Null when nobody declared them, which leaves every action as the model wrote it.
        /// </summary>
        public IReadOnlyCollection<string>? ActionableToolNames { get; }

        /// <summary>
        /// Initializes a new <see cref="ToolContext"/> pairing the agent's context provider
        /// with the session and conversationId for the in-flight turn.
        /// </summary>
        /// <param name="provider">The singleton context provider for the agent.</param>
        /// <param name="session">The active agent session for the current turn.</param>
        /// <param name="conversationId">The Akka conversation identifier this agent instance is scoped to.</param>
        /// <param name="actionableToolNames">The agent's tools that a user action may lead to, if known.</param>
        public ToolContext(
            MorganaAIContextProvider provider,
            AgentSession session,
            string conversationId,
            IReadOnlyCollection<string>? actionableToolNames = null)
        {
            Provider = provider;
            Session = session;
            ConversationId = conversationId;
            ActionableToolNames = actionableToolNames;
        }
    }
}