using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.Contracts;

namespace Morgana.AI.Tools;

/// <summary>
/// The framework's own tool: <c>Reply</c>, which every agent holds and which closes each of its turns.
/// </summary>
/// <remarks>
/// Declared the way a domain tool is, so the model reads its description from the method and the startup contract check weighs it with the others.
/// </remarks>
public class ReplyTool : MorganaTool
{
    /// <summary>Deepest card nesting the channels lay out: card, section, nested section.</summary>
    private const int MaxCardDepth = 3;

    /// <summary>Most components, nested ones included, a card may carry.</summary>
    private const int MaxCardComponents = 50;

    /// <summary>
    /// Initializes the tool for one agent.
    /// </summary>
    /// <param name="toolLogger">Logger for tool diagnostics.</param>
    /// <param name="getToolContext">Factory returning the <see cref="MorganaTool.ToolContext"/> for the current turn.</param>
    public ReplyTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
    }

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
    [Description("Closes your turn. Call it exactly once, as your very last action, after your text to the user is written: nothing you write after it reaches the user. It carries everything of the turn that is not text: what it awaits from the user, whether the user is leaving, the actions offered as buttons and the card.")]
    [RequiresApproval(false)]
    public async Task<Records.FrameworkToolResult> Reply(
        [Description("What the turn ends waiting for from the user. 'nothing': the request is answered. 'typed_answer': you asked for a value the user types in their own words. 'action_choice': you offer actions to pick from. A turn inviting the user to continue awaits them. A request naming one of several items without saying which is not answered yet: ask which, as a typed_answer or as an action_choice.")]
        [ToolParameter(Records.ToolScope.Request)] Records.AwaitedFromUser awaits,
        [Description("True when the user's own message was a goodbye: thanks, we're done, declining further help, wanting to return to the main assistant.")]
        [ToolParameter(Records.ToolScope.Request)] bool userIsLeaving,
        [Description("The actions offered as buttons; omit when none. 'tool' is the name of your own tool the action leads to, 'label' the button text (an emoji is welcome), 'value' the message sent on the user's behalf when the button is pressed, written as the user would type it.")]
        [ToolParameter(Records.ToolScope.Request)] List<Records.ReplyAction>? actions = null,
        [Description("A card presenting the turn's structured data; omit when none. Pick each component by the nature of the datum: key_value for one labeled field, grid for several homogeneous pairs, list for an enumeration, text_block for prose, badge for a status, image for a picture URL, section to group related components, divider between groups. At most 50 components and 3 levels of nesting.")]
        [ToolParameter(Records.ToolScope.Request)] RichCard? card = null)
    {
        // The context of the turn under way holds the session where the closure is recorded.
        ToolContext ctx = getToolContext();

        // A turn closes once. A second Reply in the same turn is a stray duplicate: the closure already
        // recorded stands and this call is only told so, which still gives it the result its call is owed.
        if (ctx.Provider.GetVariable(ctx.Session, Constants.ContextKeys.TurnReply) is not null)
        {
            toolLogger.LogWarning("Reply called again in a turn already closed: the first closure stands");
            EndToolLoopOnceResponseIsAnswered();

            // The duplicate is answered with the turn-closed note, so the model learns that the first closure stands.
            return new Records.FrameworkToolResult(Constants.ToolInjections.TurnClosed);
        }

        // Closed before a word of it was written, the turn would end mute: the model is sent back to
        // write first. Absent when the framework records a closure itself, which it does only after text.
        if (FunctionInvokingChatClient.CurrentContext is { } invocation && !HasTurnText(invocation.Messages))
            return new Records.FrameworkToolResult(Constants.ToolInjections.ReplyWithoutText);

        // A card the channel could not lay out is refused and the turn stays open, so the model calls
        // Reply again with a card that fits.
        if (card is not null)
        {
            // Nesting beyond what the channels lay out is refused with the limit stated.
            int depth = CalculateMaxDepth(card.Components, 1);
            if (depth > MaxCardDepth)
                return new Records.FrameworkToolResult(Constants.ToolInjections.CardTooDeep, new Dictionary<string, string>
                {
                    [Constants.Placeholders.CardDepth] = depth.ToString(CultureInfo.InvariantCulture),
                    [Constants.Placeholders.Limit] = MaxCardDepth.ToString(CultureInfo.InvariantCulture)
                });

            // A card with more components than the channels lay out is refused the same way.
            int totalComponents = CountComponents(card.Components);
            if (totalComponents > MaxCardComponents)
                return new Records.FrameworkToolResult(Constants.ToolInjections.CardTooLarge, new Dictionary<string, string>
                {
                    [Constants.Placeholders.CardComponents] = totalComponents.ToString(CultureInfo.InvariantCulture),
                    [Constants.Placeholders.Limit] = MaxCardComponents.ToString(CultureInfo.InvariantCulture)
                });
        }

        // An omitted list means the turn offers no button.
        List<Records.ReplyAction> offeredActions = actions ?? [];

        // At a choice step the buttons are the framework's proposal: one per tool of the step, each once.
        // The model words them and nothing else, so any other set is refused and the turn stays open for repair.
        // A user who is leaving gets no button, so there is nothing to hold to the step.
        if (ctx.ChoiceStep is { } choice && !userIsLeaving)
        {
            // The set is exact when each tool of the step is offered once and nothing else is.
            bool isExactSet = offeredActions.Count == choice.Tools.Count
                && choice.Tools.All(tool => offeredActions.Count(action => string.Equals(action.Tool, tool, StringComparison.Ordinal)) == 1);
            if (!isExactSet)
                return new Records.FrameworkToolResult(Constants.ToolInjections.StepActionsRequired, new Dictionary<string, string>
                {
                    [Constants.Placeholders.Step] = choice.Step,
                    [Constants.Placeholders.Workflow] = choice.Workflow,
                    [Constants.Placeholders.Tools] = string.Join(", ", choice.Tools)
                });

            // Recorded in the order the step declares, whatever order the model wrote them in.
            List<Records.ReplyAction> actionsInModelOrder = offeredActions;
            offeredActions = [.. choice.Tools.Select(tool => actionsInModelOrder.First(action => string.Equals(action.Tool, tool, StringComparison.Ordinal)))];
            awaits = Records.AwaitedFromUser.ActionChoice;
        }

        // An action is a button that the user presses to have something done: one leading to no tool of this
        // agent would be a promise nothing keeps, so it never reaches the channel.
        if (ctx.ActionableToolNames is { } actionableToolNames)
        {
            // The model is not told about the discard: the log is where an agent that offers phantom buttons shows up.
            foreach (Records.ReplyAction discarded in offeredActions.Where(action => !actionableToolNames.Contains(action.Tool)))
                toolLogger.LogWarning("Reply discarded the action '{Label}': it leads to '{Tool}', which this agent does not have", discarded.Label, discarded.Tool);

            // Only the buttons that lead to a tool of this agent are delivered.
            offeredActions = [.. offeredActions.Where(action => actionableToolNames.Contains(action.Tool))];
        }

        // The closure as the channel will deliver it: the awaited input, the leaving flag and the buttons and card that survived the checks.
        Records.TurnReply turnReply = new Records.TurnReply(awaits, userIsLeaving, offeredActions, card);

        // The agent reads the closure from the session at the end of the turn: this is how Reply hands it over.
        await ctx.Provider.SetVariableAsync(ctx.Session, Constants.ContextKeys.TurnReply,
            JsonSerializer.Serialize(turnReply, Records.DefaultJsonSerializerOptions));

        // The turn is closed: the model gets no further call to talk after the closure.
        EndToolLoopOnceResponseIsAnswered();

        toolLogger.LogInformation(
            "LLM closed its turn via Reply: awaits={Awaits}, userIsLeaving={UserIsLeaving}, actions={Actions}, card={Card}",
            awaits, userIsLeaving, turnReply.Actions.Count, card?.Title ?? "(none)");

        // The model is told the turn is closed.
        return new Records.FrameworkToolResult(Constants.ToolInjections.TurnClosed);
    }

    /// <summary>
    /// Ends the tool loop of a closed turn without spending another call, once every call of the
    /// response that closed it has its result.
    /// </summary>
    private static void EndToolLoopOnceResponseIsAnswered()
    {
        // Ending the loop at an earlier call leaves the later ones of the same response unanswered and the
        // provider refuses the next turn's history over a call with no result. Absent when the framework
        // records a closure itself.
        if (FunctionInvokingChatClient.CurrentContext is { } invocation && invocation.FunctionCallIndex == invocation.FunctionCount - 1)
            invocation.Terminate = true;
    }

    /// <summary>
    /// True when the turn under way has already written text to the user: an assistant message with
    /// text after the message that opened the turn.
    /// </summary>
    internal static bool HasTurnText(IList<ChatMessage> messages)
    {
        // The turn opened at the last user message: text from earlier turns does not count.
        int turnStart = messages.Count - 1;
        while (turnStart >= 0 && messages[turnStart].Role != ChatRole.User)
            turnStart--;

        // Only an assistant message carrying words proves the user was written to.
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
        // A card without sections is as deep as the level it sits on.
        int maxDepth = currentDepth;

        // Only a section nests further components: the deepest branch sets the card's depth.
        foreach (CardComponent component in components)
        {
            // Only a section nests further components, so only a section is descended into.
            if (component is SectionComponent section)
            {
                // The depth below this section is measured one level deeper than the current one.
                int sectionDepth = CalculateMaxDepth(section.Components, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, sectionDepth);
            }
        }

        // The deepest branch is handed back as the depth of the card.
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
        // The components of this level count first.
        int count = components.Count;

        // A section adds its own components to the total.
        foreach (CardComponent component in components)
        {
            // A section counts as one component and the components nested in it count too.
            if (component is SectionComponent section)
                count += CountComponents(section.Components);
        }

        // The total is handed back to the check that refuses cards too large for the channels.
        return count;
    }
}
