using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.ChatClients;
using Morgana.Contracts;

namespace Morgana.AI.Tools;

/// <summary>
/// The framework's own tool: <c>Reply</c>, which every agent holds and which gives the answer of each of its turns.
/// </summary>
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
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext) { }

    /// <summary>
    /// Gives the agent's turn answer: its text, what it ends waiting for, whether the user is leaving, the actions
    /// that it offers and the card that it presents. Recorded for <c>MorganaAgent</c> to read at the end of the turn.
    /// </summary>
    /// <param name="text">The words of the turn's answer to the user.</param>
    /// <param name="awaits">What the turn ends waiting for from the user.</param>
    /// <param name="userIsLeaving">True when the user's own message was a goodbye.</param>
    /// <param name="actions">The agent's own actions offered as buttons, if any.</param>
    /// <param name="card">The card presenting the turn's structured data, if any.</param>
    /// <returns>
    /// The named result the tool loop turns into its authored text. A closure refused for what the model offered
    /// throws instead, with the fact that broke the rules as its message, which the tool loop hands back to the model.
    /// </returns>
    [Description("Gives your turn's answer. Its text is the only part of the turn that reaches your reader: anything you write outside Reply is never shown. Call it exactly once, as your last action. Beside the text it carries what the turn awaits from the user, whether the user is leaving, the actions offered as buttons and the card.")]
    [SystemTool]
    public async Task<Records.FrameworkToolResult> Reply(
        [Description("Your words to your reader, written against everything the tools returned.")]
        [ToolParameter(Records.ToolScope.Request)] string text,
        [Description("What the turn ends waiting for from the user. 'nothing': the request is answered. 'typed_answer': you asked for a value the user types in their own words. 'action_choice': you offer actions to pick from. With 'nothing', buttons to stay or leave follow your text by themselves: your text never asks whether the user needs anything else. A request naming one of several items without saying which is not answered yet: ask which, as a typed_answer or as an action_choice.")]
        [ToolParameter(Records.ToolScope.Request)] Records.AwaitedFromUser awaits,
        [Description("True when the user's own message was a goodbye: thanks, we're done, declining further help, wanting to return to the main assistant.")]
        [ToolParameter(Records.ToolScope.Request)] bool userIsLeaving,
        [Description("The actions offered as buttons; omit when none.")]
        [ToolParameter(Records.ToolScope.Request)] List<Records.OfferedAction>? actions = null,
        [Description("A card presenting the turn's structured data; omit when none. At most 50 components.")]
        [ToolParameter(Records.ToolScope.Request)] RichCard? card = null)
    {
        // The context of the turn under way holds the session where the closure is recorded.
        ToolContext ctx = getToolContext();

        // A turn is answered once. A second Reply in the same turn is a stray duplicate: the closure already
        // recorded stands and this call is only told so, which still gives it the result its call is owed.
        if (ctx.Provider.HoldsFrameworkKey(ctx.Session, Constants.ContextKeys.ClosingDeclaration))
        {
            toolLogger.LogWarning("Reply called again in a turn already answered: the first closure stands");
            EndToolLoopOnceResponseIsAnswered();

            // The duplicate is answered with the turn-closed note, so the model learns that the first closure stands.
            return new Records.FrameworkToolResult(Constants.ToolInjections.TurnClosed);
        }

        // A Reply with no words would end the turn mute: the call is refused and the model is sent back to write them.
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException(NoTextReason());

        // A hole in a list would fail later on the first member read, so it is refused here with its place.
        if (FindNullElement(actions, card) is { } nullElementPlace)
            throw new ArgumentException(NullElementReason(nullElementPlace));

        // A card the channel could not lay out is refused and the turn stays open, so the model calls
        // Reply again with a card that fits.
        if (card is not null)
        {
            // Nesting beyond what the channels lay out is refused with the limit stated.
            int depth = CalculateMaxDepth(card.Components, 1);
            if (depth > MaxCardDepth)
                throw new ArgumentException(CardTooDeepReason(depth));

            // A card with more components than the channels lay out is refused the same way.
            int totalComponents = CountComponents(card.Components);
            if (totalComponents > MaxCardComponents)
                throw new ArgumentException(CardTooLargeReason(totalComponents));
        }

        // An omitted list means the turn offers no button.
        List<Records.OfferedAction> offeredActions = actions ?? [];

        // At a choice step the buttons are the framework's proposal: one per tool of the step, each once.
        // The model words them and nothing else, so any other set is refused and the turn stays open for repair.
        // A user who is leaving gets no button, so there is nothing to hold to the step. Only the model's own call
        // is held to it: a Reply that the framework makes comes after MorganaAgent imposed the step's buttons.
        if (FunctionInvokingChatClient.CurrentContext is not null && ctx.ChoiceStep is { } choice && !userIsLeaving)
        {
            // The set is exact when each tool of the step is offered once and nothing else is.
            bool isExactSet = offeredActions.Count == choice.Tools.Count
                && choice.Tools.All(tool => offeredActions.Count(action => string.Equals(action.Tool, tool, StringComparison.Ordinal)) == 1);
            if (!isExactSet)
                throw new ArgumentException(StepActionsReason(choice, offeredActions));

            // Recorded in the order the step declares, whatever order the model wrote them in.
            List<Records.OfferedAction> actionsInModelOrder = offeredActions;
            offeredActions = [.. choice.Tools.Select(tool => actionsInModelOrder.First(action => string.Equals(action.Tool, tool, StringComparison.Ordinal)))];
            awaits = Records.AwaitedFromUser.ActionChoice;
        }

        // An action is a button that the user presses to have something done: one leading to no tool of this
        // agent would be a promise nothing keeps, so it is refused and the turn stays open for the model to repair.
        if (ctx.ActionableToolNames is { } actionableToolNames
             && offeredActions.FirstOrDefault(action => !actionableToolNames.Contains(action.Tool)) is { } unknownAction)
            throw new ArgumentException(ActionToolUnknownReason(unknownAction, actionableToolNames));

        // The arguments of an action are the call that its press makes: a parameter that the tool does not take or a value of
        // a type that it refuses would make a call that cannot run, so the closure is refused and the model repairs the action.
        if (ctx.ActionSignatures is { } actionSignatures
             && offeredActions.Select(action => ActionArgumentsFault(action, actionSignatures)).FirstOrDefault(fault => fault is not null) is { } argumentsFault)
            throw new ArgumentException(argumentsFault);

        // The answer as the channel will deliver it: the trimmed text, the awaited input, the leaving flag and the buttons and card that survived the checks.
        Records.ClosingDeclaration closingDeclaration = new Records.ClosingDeclaration(text.Trim(), awaits, userIsLeaving, offeredActions, card);

        // The agent reads the closure from the session at the end of the turn: this is how Reply hands it over.
        await ctx.Provider.SetVariableAsync(ctx.Session, Constants.ContextKeys.ClosingDeclaration,
            JsonSerializer.Serialize(closingDeclaration, Records.DefaultJsonSerializerOptions));

        // The turn is closed: the model gets no further call to talk after the closure.
        EndToolLoopOnceResponseIsAnswered();

        toolLogger.LogInformation(
            "LLM answered via Reply: textLength={TextLength}, awaits={Awaits}, userIsLeaving={UserIsLeaving}, actions={Actions}, card={Card}",
            closingDeclaration.Text.Length, awaits, userIsLeaving, closingDeclaration.Actions.Count, card?.Title ?? "(none)");

        // The model is told the turn is closed.
        return new Records.FrameworkToolResult(Constants.ToolInjections.TurnClosed);
    }

    /// <summary>The reason of a Reply whose text is empty.</summary>
    internal static string NoTextReason()
        => "the text is empty";

    /// <summary>The reason of a call needing approval that no Reply with text accompanies.</summary>
    internal static string ApprovalWithoutReplyReason()
        => "the call needs the user's approval and no Reply with text came in the same response to tell them what it will do";

    /// <summary>The reason of a card nesting beyond what the channels lay out.</summary>
    /// <param name="depth">The levels the refused card nests.</param>
    private static string CardTooDeepReason(int depth)
        => string.Create(CultureInfo.InvariantCulture, $"the card nests {depth} levels and at most {MaxCardDepth} are allowed");

    /// <summary>The reason of a card holding more components than the channels lay out.</summary>
    /// <param name="count">The components the refused card holds.</param>
    private static string CardTooLargeReason(int count)
        => string.Create(CultureInfo.InvariantCulture, $"the card holds {count} components and at most {MaxCardComponents} are allowed");

    /// <summary>The reason of a choice step offered with other actions than its own tools.</summary>
    /// <param name="choice">The choice step that the turn stands at.</param>
    /// <param name="offeredActions">The actions that the model offered.</param>
    private static string StepActionsReason(
        (string Workflow, string Step, IReadOnlyList<string> Tools) choice,
        List<Records.OfferedAction> offeredActions)
        => $"the actions offered lead to [{string.Join(", ", offeredActions.Select(action => action.Tool))}] while step '{choice.Step}' of workflow '{choice.Workflow}' takes exactly one action to each of: {string.Join(", ", choice.Tools)}";

    /// <summary>The reason of an action leading to a tool that the agent does not have.</summary>
    /// <param name="action">The refused action.</param>
    /// <param name="actionableToolNames">The tools that an action may lead to.</param>
    private static string ActionToolUnknownReason(Records.OfferedAction action, IReadOnlyCollection<string> actionableToolNames)
        => $"the action '{action.Label}' leads to '{action.Tool}', which is not one of your tools: {string.Join(", ", actionableToolNames)}";

    /// <summary>
    /// The reason why the arguments of an action cannot make the call of its tool; null when they can.
    /// </summary>
    /// <param name="action">The action as the model offered it.</param>
    /// <param name="actionSignatures">The signatures of the tools that an action may lead to.</param>
    private static string? ActionArgumentsFault(Records.OfferedAction action, IReadOnlyDictionary<string, Records.CallSignature> actionSignatures)
    {
        // An action carrying no argument leaves every value to the user, which is always a call that can be completed.
        // A tool without a signature is one whose arguments nobody declared, so they are taken as written.
        if (action.Arguments is not { Count: > 0 } arguments || !actionSignatures.TryGetValue(action.Tool, out Records.CallSignature? signature))
            return null;

        // Each argument is held to the parameter that it names.
        foreach ((string parameter, JsonElement value) in arguments)
        {
            // A name that the tool does not take could reach no parameter of the call.
            if (!signature.Parameters.ContainsKey(parameter))
                return signature.Parameters.Count == 0
                    ? $"the action '{action.Label}' passes '{parameter}' to '{action.Tool}', which takes no argument"
                    : $"the action '{action.Label}' passes '{parameter}' to '{action.Tool}', which takes: {string.Join(", ", signature.Parameters.Keys)}";

            // A value of a type that the parameter refuses would fail the call when the press makes it.
            if (!signature.Accepts(parameter, value))
                return $"the action '{action.Label}' passes '{parameter}' as {value.ValueKind.ToString().ToLowerInvariant()} while '{action.Tool}' takes it as {string.Join(" or ", signature.Parameters[parameter])}";
        }

        // Every argument reaches a parameter of the call with a value that it accepts.
        return null;
    }

    /// <summary>The reason of a list holding a null element.</summary>
    /// <param name="place">Where the null element sits, as a path into the call's arguments.</param>
    private static string NullElementReason(string place)
        => $"{place} is null and a list holds no null element";

    /// <summary>
    /// Finds the first null element among the actions and the card's lists, since the deserializer lets a null element through.
    /// </summary>
    /// <param name="actions">The actions as the model passed them.</param>
    /// <param name="card">The card as the model passed it.</param>
    /// <returns>The place of the null element as a path into the arguments; null when the lists hold none.</returns>
    private static string? FindNullElement(List<Records.OfferedAction>? actions, RichCard? card)
    {
        // An action list is checked by position.
        int nullAction = actions?.FindIndex(action => action is null) ?? -1;
        if (nullAction >= 0)
            return string.Create(CultureInfo.InvariantCulture, $"actions[{nullAction}]");

        // The card's components are checked level by level.
        return card is null ? null : FindNullComponent(card.Components, "card.components");
    }

    /// <summary>
    /// Finds the first null element among a list of components and everything the components hold.
    /// </summary>
    /// <param name="components">The components of one level.</param>
    /// <param name="place">The path of that list into the arguments.</param>
    private static string? FindNullComponent(List<CardComponent> components, string place)
    {
        // Each component is checked for itself first, then for what it holds.
        for (int index = 0; index < components.Count; index++)
        {
            // The path of this component into the arguments.
            string componentPlace = string.Create(CultureInfo.InvariantCulture, $"{place}[{index}]");

            // Only lists hold elements: a section holds components, a list holds texts and a grid holds pairs.
            string? nullElementPlace = components[index] switch
            {
                // A null component has nothing to read.
                null => componentPlace,
                // A section holds the next level down.
                SectionComponent section => FindNullComponent(section.Components, componentPlace + ".components"),
                // A list holds texts.
                ListComponent list when list.Items.FindIndex(item => item is null) is int nullItem and >= 0
                    => string.Create(CultureInfo.InvariantCulture, $"{componentPlace}.items[{nullItem}]"),
                // A grid holds pairs.
                GridComponent grid when grid.Items.FindIndex(item => item is null) is int nullPair and >= 0
                    => string.Create(CultureInfo.InvariantCulture, $"{componentPlace}.items[{nullPair}]"),
                // The remaining components hold no list.
                _ => null
            };

            // The first hole found ends the search.
            if (nullElementPlace is not null)
                return nullElementPlace;
        }

        // No component of this level holds a null element.
        return null;
    }

    /// <summary>
    /// Presents Reply under a schema that admits exactly what its validation accepts for one agent: the actions lead
    /// to the agent's own tools alone and the card nests no deeper than the channels lay out.
    /// </summary>
    /// <param name="reply">The Reply function as the adapter created it.</param>
    /// <param name="actionableToolNames">The agent's tools that an action may lead to.</param>
    public static AIFunction NarrowSchemaToAgent(AIFunction reply, IReadOnlyCollection<string> actionableToolNames)
    {
        // The schema is edited as a document, then handed back to the function that the tool loop runs.
        JsonObject schema = WorkflowToolsChatClient.SchemaOf(reply);
        JsonObject properties = schema["properties"]!.AsObject();

        // An agent with no tool to lead to offers no button at all.
        if (actionableToolNames.Count == 0)
            properties.Remove("actions");

        // Otherwise an action can only name one of the agent's tools, as the validation requires.
        else
            properties["actions"]!["items"]!["properties"]!["tool"]!.AsObject()["enum"] =
                new JsonArray([.. actionableToolNames.Select(JsonNode (tool) => JsonValue.Create(tool)!)]);

        // The card's recursive section is written out level by level, so that the schema stops at the depth the validation accepts.
        JsonObject components = properties["card"]!["properties"]!["components"]!.AsObject();
        components["items"] = UnrollComponentLevel(components["items"]!.AsObject(), 1);

        // The model reads Reply under the schema as shaped for this agent.
        return WorkflowToolsChatClient.PresentUnderSchema(reply, schema);
    }

    /// <summary>
    /// Writes one level of the card's components with the levels below it, offering <c>section</c> only where a deeper level is still allowed.
    /// </summary>
    /// <param name="recursiveItem">The schema of one component, whose section points back at it by reference.</param>
    /// <param name="depth">The level being written, starting at 1 for the card's own components.</param>
    private static JsonObject UnrollComponentLevel(JsonObject recursiveItem, int depth)
    {
        // Each level is a copy: the section of one level must not be the section of the next.
        JsonObject level = recursiveItem.DeepClone().AsObject();
        JsonArray alternatives = level["anyOf"]!.AsArray();
        JsonNode section = alternatives.First(alternative => alternative!["properties"]!["type"]!["const"]!.GetValue<string>() == "section")!;

        // The deepest level the channels lay out holds no section, so none is offered there.
        if (depth >= MaxCardDepth)
            alternatives.Remove(section);

        // Above it a section holds the components of the next level down.
        else
            section["properties"]!["components"]!["items"] = UnrollComponentLevel(recursiveItem, depth + 1);

        // The level as the model reads it.
        return level;
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