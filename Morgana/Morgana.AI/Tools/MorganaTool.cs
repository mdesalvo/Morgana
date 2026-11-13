using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Providers;
using Morgana.Contracts;

namespace Morgana.AI.Tools;

/// <summary>
/// Base class of every tool class, holding the base tools every agent has ahead of its own and the session of the turn in flight.
/// What a tool returns states facts (what was written, what the response does and does not carry) and never instructs:
/// it reaches the model mid-turn from outside the composed prompt, the one layer with no declared precedence.
/// A tool that needs the agent to behave a certain way is asking for a line of domain Instructions or for a global policy.
/// </summary>
public class MorganaTool
{
    /// <summary>
    /// How deep sections may nest below a card's own components, which sit at level 0: the card plus three levels of sections.
    /// It is measured as the RichCardUsage policy and the tool's description state it, so the refusal speaks the model's own terms.
    /// </summary>
    private const int MaxCardDepth = 3;

    /// <summary>The most components a card may hold, those inside sections included.</summary>
    private const int MaxCardComponents = 50;

    /// <summary>
    /// Writes the tool's diagnostics, the context-access lines among them.
    /// Those lines are the only trace of a context variable's name, which the PromptHarness parses.
    /// </summary>
    protected readonly ILogger toolLogger;

    /// <summary>
    /// Returns the context provider, the session and the conversation of the turn in flight.
    /// It is called on every tool invocation, because a tool instance outlives the turn whose session it serves.
    /// </summary>
    protected readonly Func<ToolContext> getToolContext;

    /// <summary>Binds the tool to its logger and to the source of the turn in flight, as <see cref="Adapters.MorganaAgentAdapter"/> wires every tool class.</summary>
    /// <param name="toolLogger">Writes the tool's diagnostics.</param>
    /// <param name="getToolContext">Returns the <see cref="ToolContext"/> of the turn in flight.</param>
    public MorganaTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext)
    {
        this.toolLogger = toolLogger;
        this.getToolContext = getToolContext;
    }

    // =========================================================================
    // CONTEXT SYSTEM TOOLS
    // =========================================================================

    /// <summary>Reads a variable from the agent's context, answering its value or the fact that it is not held.</summary>
    /// <param name="variableName">The variable to read, such as <c>customerCode</c>.</param>
    /// <returns>The held value; otherwise the line saying the variable is not in context.</returns>
    [Description("Reads a value from a CLOSED vocabulary of agent-context keys. Legal variable names are ONLY names a tool's own description flags as a context-scoped input — its 'CONTEXT-SCOPED INPUTS OF THIS TOOL' line (for example 'customerCode') — or reserved ephemeral keys used by the framework. Call this tool BEFORE asking the user for any such value — if it returns a value, USE IT; ask the user only if it reports missing. While the lookup is pending you have learned nothing yet, so do NOT write text asking the user for the very value you are looking up: resolve it first, then write once, against what you then know. A question your own next tool call answers reaches the user as a demand for something you already had. NEVER call this tool with a name taken from the user's message (a species, product code, entity, or topic the user is asking ABOUT) — those are query subjects, not variable names; use your domain tool to look them up instead.")]
    public Task<object> GetContextVariable(
        [Description("The name of the variable for which you are requesting read access to the agent's context (for example: customerCode)")] string variableName)
    {
        // This agent's own context, into which the shared registry was merged at the start of the turn:
        // a value the user gave a colleague answers here too.
        ToolContext ctx = getToolContext();
        object? value = ctx.Provider.GetVariable(ctx.Session, variableName);

        // A held value answers the lookup and the user is not asked for it.
        if (value != null)
        {
            toolLogger.LogInformation(
                Constants.ObservableLogs.ContextHit,
                Constants.ObservableLogs.ToolName, GetType().Name, Constants.ObservableLogs.Hit, variableName, value);

            return Task.FromResult(value);
        }

        toolLogger.LogInformation(
            Constants.ObservableLogs.ContextMiss,
            Constants.ObservableLogs.ToolName, GetType().Name, Constants.ObservableLogs.Miss, variableName);

        // The variable is not held: from here on asking the user for it is legitimate.
        return Task.FromResult<object>(
            $"Information {variableName} not available in context: you need to engage SetContextVariable to set it.");
    }

    /// <summary>
    /// Writes a variable to the agent's context, answering what was written.
    /// A variable that a tool parameter declares as shared also reaches the conversation's <c>shared_context</c>, where every other agent reads it.
    /// </summary>
    /// <param name="variableName">The variable to write, such as <c>customerCode</c>.</param>
    /// <param name="variableValue">The value to store.</param>
    /// <returns>The line naming the variable and the value written.</returns>
    [Description("Writes a value to a CLOSED vocabulary of agent-context keys. Legal variable names are ONLY names a tool's own description flags as a context-scoped input — its 'CONTEXT-SCOPED INPUTS OF THIS TOOL' line (for example 'customerCode') — or reserved ephemeral keys managed by the framework. The only legitimate writes are values for such an input — and every such value you obtain MUST be written, whether the user gave it in answer to your question or volunteered it unprompted in their very first message. Write it as soon as you have it, before or alongside the tool call that consumes it; a value you merely pass to a tool without persisting is gone for every later turn. NEVER invent a variable name, NEVER store user input under a new key, NEVER use this tool to 'save progress', 'grant access', 'remember what the user said', or mirror the subject of the user's question. If you feel the urge to invent a key, STOP: call your domain tool or answer the user in plain text instead.")]
    public async Task<object> SetContextVariable(
        [Description("The name of the variable for which you are requesting write access to the agent's context (for example: 'customerCode')")] string variableName,
        [Description("The value of the variable for which you are requesting write access to the agent's context (for example: 'P994E')")] string variableValue)
    {
        // Written to the session of the turn in flight, which also forwards a shared variable to the registry.
        ToolContext ctx = getToolContext();
        await ctx.Provider.SetVariableAsync(ctx.Session, variableName, variableValue);

        toolLogger.LogInformation(
            Constants.ObservableLogs.ContextSet,
            Constants.ObservableLogs.ToolName, GetType().Name, Constants.ObservableLogs.Set, variableName, variableValue);

        // The write confirmed with the value stored, so the model sees what it now holds.
        return $"Information {variableName} inserted in context with value: {variableValue}";
    }

    // =========================================================================
    // TURN ENDING SYSTEM TOOL
    // =========================================================================

    /// <summary>
    /// Hands the conversation back to Morgana once the current turn ends.
    /// An agent stays in service by default, so this out-of-band signal is the one declaration a turn makes about where the conversation goes next.
    /// </summary>
    /// <returns>The line confirming the hand-back.</returns>
    [Description("Hands the conversation back to Morgana once this turn ends. You remain in service by default and need no call to stay: call this tool only when the user takes their leave of you. Call it before writing your text response: your farewell is the last thing in the turn. This tool call is the ONLY way to hand the conversation back: never write a marker, token, tag or symbol in your text to mean it. Quick replies and a rich card keep you in service whatever this tool says: a turn offering a choice awaits the answer.")]
    public async Task<object> SetTurnEnding()
    {
        // Read by the agent at the end of the turn, which is when the conversation actually returns to Morgana.
        ToolContext ctx = getToolContext();
        await ctx.Provider.SetVariableAsync(ctx.Session, Constants.ContextKeys.TurnEnding, true);

        toolLogger.LogInformation("LLM handed the conversation back to Morgana via SetTurnEnding tool");

        // The hand-back confirmed, with the fact that the farewell is still to be written while it is.
        return "Turn ending set: once this turn ends, the conversation returns to Morgana." + NothingReachedUserFact();
    }

    /// <summary>
    /// States that the turn has shown the user nothing yet; once the turn has written some text it states nothing.
    /// A presentation tool is called before the text it accompanies, so its result is the last thing the model reads before writing:
    /// told as a fact, it shows that the words are still owed, while a turn that already wrote them reads nothing inviting it to write them twice.
    /// </summary>
    private static string NothingReachedUserFact()
    {
        // The messages the tool loop runs on: the history, the turn's phrase and what the model answered since.
        // Outside a tool loop nobody knows what the turn has written, so no fact is claimed.
        IList<ChatMessage>? loopMessages = FunctionInvokingChatClient.CurrentContext?.Messages;
        if (loopMessages is null)
            return string.Empty;

        // Back from the newest message to the turn's phrase: whatever precedes it the user read in an earlier turn.
        for (int index = loopMessages.Count - 1; index >= 0 && loopMessages[index].Role != ChatRole.User; index--)
        {
            // Only the model's own text reaches the user: a tool call or a thinking block carries none.
            if (loopMessages[index].Role == ChatRole.Assistant
                && loopMessages[index].Contents.OfType<TextContent>().Any(textContent => !string.IsNullOrWhiteSpace(textContent.Text)))
                return string.Empty;
        }

        // The turn has written nothing yet: its text is what the user will read.
        return " Nothing has reached the user in this turn yet.";
    }

    // =========================================================================
    // QUICK REPLY SYSTEM TOOL
    // =========================================================================

    /// <summary>Attaches the buttons the channel draws below the turn's text.</summary>
    /// <param name="quickReplies">The buttons, each with its id, label and value and the optional termination flag.</param>
    /// <returns>The line confirming how many buttons the user will see.</returns>
    [Description("Draws the interactive buttons the user can click. The payload is a JSON array; each button carries 'id' (unique identifier), 'label' (the display text, emoji encouraged) and 'value' (the complete natural message sent on the user's behalf when clicked), plus the optional 'termination' flag that only the exit button carries. Call this tool BEFORE writing your text response — the buttons render below the message. WHETHER a turn emits buttons at all and which ones is decided entirely by the Quick Reply Doctrine; this tool only draws what that rule has already settled.")]
    public async Task<object> SetQuickReplies(
        [Description("Array of button definitions. Each button: 'id' (unique, e.g. 'invoice-001'), 'label' (display text, e.g. '📄 Invoice INV-001'), 'value' (the message sent on click, e.g. 'Show details for invoice INV-001') and optionally 'termination' (boolean, exit button only). Format: [{\"id\":\"option1\",\"label\":\"🔧 Option 1\",\"value\":\"User message for option 1\"}]. Labels are action-oriented; values read as something the user would actually type.")] List<QuickReply> quickReplies)
    {
        // No buttons leaves the turn's context untouched: the model reads that nothing was set.
        if (quickReplies == null || quickReplies.Count == 0)
        {
            toolLogger.LogWarning("SetQuickReplies called with no quick replies");
            return "Warning: No quick replies were set (empty data).";
        }

        // Kept in the turn's ephemeral context, where the agent reads them once the turn ends and attaches them to its answer.
        ToolContext ctx = getToolContext();
        await ctx.Provider.SetVariableAsync(ctx.Session, Constants.ContextKeys.QuickReplies,
            JsonSerializer.Serialize(quickReplies, Records.DefaultJsonSerializerOptions));

        toolLogger.LogInformation("LLM set {Count} quick reply buttons via SetQuickReplies tool", quickReplies.Count);

        // The buttons confirmed, with the fact that the text they sit under is still to be written while it is.
        return $"Quick reply buttons set successfully. The user will see {quickReplies.Count} interactive options. Now provide your text response to the user - the quick reply buttons will appear below your message." + NothingReachedUserFact();
    }

    // =========================================================================
    // RICH CARD SYSTEM TOOL
    // =========================================================================

    /// <summary>Attaches the card the channel draws below the turn's text, refusing one that does not parse or exceeds the card's limits.</summary>
    /// <param name="richCard">The card as a JSON string: title, optional subtitle and components.</param>
    /// <returns>The line confirming the card; otherwise the reason it was refused.</returns>
    [Description("Render structured information as a visual card instead of a wall of text. Use for invoices, profiles, reports, comparisons — any data that benefits from visual hierarchy. Call this tool before writing your text response: the card renders below your message.\n\n=== ROOT STRUCTURE (MANDATORY) ===\n{\"title\": \"string (required)\", \"subtitle\": \"string (optional)\", \"components\": [array (required)]}\nRoot MUST carry 'title' and 'components'. NEVER 'sections', 'data' or any other wrapper.\n\n=== THE 8 COMPONENT TYPES ===\nCompose these like building blocks: pick each one by the SEMANTIC NATURE of the data (labeled field? prose? list? status? picture?), group related ones with 'section', separate groups with 'divider'.\n\n1. text_block — narrative prose: explanations, warnings, notes. Not for labeled data (use key_value) or item lists (use list).\n   content (string, required) · style (optional): \"normal\" (default) | \"bold\" | \"muted\" | \"small\"\n   {\"type\": \"text_block\", \"content\": \"⚠️ Payment is 53 days overdue. Late fees may apply.\", \"style\": \"bold\"}\n\n2. key_value — one labeled field: any \"Label: Value\" datum. For 2+ homogeneous fields side by side use grid instead.\n   key (string, required) · value (string, required) · emphasize (bool, optional, default false)\n   {\"type\": \"key_value\", \"key\": \"Due Date\", \"value\": \"15/12/2025\", \"emphasize\": true}\n\n3. divider — a horizontal rule between logical groups. Not between every component.\n   no properties\n   {\"type\": \"divider\"}\n\n4. list — an enumeration of related items: line items, steps, features. For labeled pairs use key_value or grid.\n   items (array of strings, required) · style (optional): \"bullet\" (default) | \"numbered\" | \"plain\"\n   {\"type\": \"list\", \"items\": [\"Premium 100Mbps Plan: €90.00\", \"Static IP: €5.00\"], \"style\": \"bullet\"}\n\n5. section — a nestable container grouping components under a heading.\n   title (string, required) · subtitle (string, optional) · components (array, required)\n   Max depth 3 from root: components > section > section > section is allowed, a fourth level is not.\n   {\"type\": \"section\", \"title\": \"Billing Details\", \"components\": [{\"type\": \"key_value\", \"key\": \"Period\", \"value\": \"October 2025\"}]}\n\n6. grid — a compact multi-column layout for homogeneous key-value pairs (profile fields, quarterly figures). Items are simple pairs only, never nested content.\n   columns (integer, required, 2-4 recommended) · items (array of {\"key\", \"value\"}, required)\n   {\"type\": \"grid\", \"columns\": 3, \"items\": [{\"key\": \"Q1\", \"value\": \"€45K\"}, {\"key\": \"Q2\", \"value\": \"€52K\"}, {\"key\": \"Q3\", \"value\": \"€48K\"}]}\n\n7. badge — a short status or category tag: Paid, Overdue, Priority, Draft.\n   text (string, required) · variant (optional): \"success\" (positive: Paid, Active) | \"warning\" (attention: Pending, Due Soon) | \"error\" (negative: Overdue, Failed) | \"info\" (New, Beta) | \"neutral\" (default, no semantics)\n   {\"type\": \"badge\", \"text\": \"Overdue\", \"variant\": \"error\"}\n\n8. image — a picture from a URL. Whenever tool output carries an image URL, use this component and NEVER a markdown link inside a text_block.\n   src (string, required) · alt (string, recommended) · caption (string, optional) · size (optional): \"small\" (200px) | \"medium\" (400px, default) | \"large\" (600px) | \"full\" (container width)\n   {\"type\": \"image\", \"src\": \"https://example.com/product.jpg\", \"alt\": \"Premium Widget Model X\", \"caption\": \"In store - €49.99\", \"size\": \"medium\"}\n\n=== RULES ===\nEvery component object carries 'type' as its FIRST property and that type is exactly one of the eight above. Serialize the whole structure to a JSON string before passing it to the tool.\n\n=== EXAMPLES ===\n\nFlat (user profile):\n{\"title\": \"User Profile\", \"subtitle\": \"Account #USR-123\", \"components\": [{\"type\": \"image\", \"src\": \"https://example.com/avatar.jpg\", \"alt\": \"User avatar\", \"size\": \"small\"}, {\"type\": \"key_value\", \"key\": \"Name\", \"value\": \"[User Name]\"}, {\"type\": \"key_value\", \"key\": \"Member Since\", \"value\": \"January 2023\"}, {\"type\": \"divider\"}, {\"type\": \"badge\", \"text\": \"Active\", \"variant\": \"success\"}]}\n\nNested (invoice with sections):\n{\"title\": \"Invoice INV-2025-001\", \"subtitle\": \"Due 15/12/2025\", \"components\": [{\"type\": \"section\", \"title\": \"Billing Details\", \"components\": [{\"type\": \"key_value\", \"key\": \"Customer\", \"value\": \"[Company Name]\"}, {\"type\": \"key_value\", \"key\": \"Status\", \"value\": \"Pending\", \"emphasize\": true}]}, {\"type\": \"divider\"}, {\"type\": \"section\", \"title\": \"Line Items\", \"components\": [{\"type\": \"list\", \"items\": [\"Premium 100Mbps Plan: €90.00\", \"Installation Fee: €35.00\"], \"style\": \"bullet\"}]}, {\"type\": \"divider\"}, {\"type\": \"key_value\", \"key\": \"TOTAL\", \"value\": \"€130.00\", \"emphasize\": true}]}")]
    public async Task<object> SetRichCard(
        [Description("JSON string carrying the complete card structure — see this tool's description for the root shape and the eight component types. It MUST be strictly valid JSON, deserializable on the first attempt: a malformed payload is silently dropped and the user sees nothing. If uncertain, simplify the card rather than risk an unclosed brace.")] string richCard)
    {
        try
        {
            // The card the model wrote, read into the shape every channel renders.
            RichCard? parsedRichCard = JsonSerializer.Deserialize<RichCard>(
                richCard, Records.DefaultJsonSerializerOptions);

            // A JSON null is no card: the model reads the refusal and the turn keeps no card.
            if (parsedRichCard == null)
            {
                toolLogger.LogWarning("SetRichCard called with invalid JSON structure");
                return "Error: Rich card JSON structure is invalid.";
            }

            // The card's own components are level 0 and each section opens one more, up to the three levels every channel renders.
            int depth = CalculateMaxDepth(parsedRichCard.Components, 0);
            if (depth > MaxCardDepth)
            {
                toolLogger.LogWarning("SetRichCard called with excessive nesting depth: {Depth} (max 3)", depth);

                // Refused with the depth found, so the model can write a flatter card.
                return $"Error: Rich card exceeds maximum nesting depth of 3 (found: {depth}). Please simplify the card structure.";
            }

            // Every component counts, those inside sections included, against the size a card may reach.
            int totalComponents = CountComponents(parsedRichCard.Components);
            if (totalComponents > MaxCardComponents)
            {
                toolLogger.LogWarning("SetRichCard called with too many components: {TotalComponents} (max 50)", totalComponents);

                // Refused with the count found, so the model can write a more focused card.
                return $"Error: Rich card has too many components: {totalComponents} (max 50). Please create a more focused card.";
            }

            // Kept in the turn's ephemeral context as the model wrote it, where the agent reads it once the turn ends.
            ToolContext ctx = getToolContext();
            await ctx.Provider.SetVariableAsync(ctx.Session, Constants.ContextKeys.RichCard, richCard);

            toolLogger.LogInformation(
                "LLM set rich card '{Title}' with {TotalComponents} components (depth: {Depth}) via SetRichCard tool",
                parsedRichCard.Title, totalComponents, depth);

            // The card confirmed, with the fact that the text it renders under is still to be written while it is.
            return $"Rich card set successfully. The user will see a structured visual card titled '{parsedRichCard.Title}'. You can now provide additional context or explanation in text if needed." + NothingReachedUserFact();
        }
        catch (JsonException ex)
        {
            // A card that is not valid JSON is refused: the model reads the fact and the turn keeps no card.
            toolLogger.LogError(ex, "Failed to parse rich card JSON in SetRichCard");
            return "Error: Rich card JSON format is invalid. Please check the structure and try again.";
        }
    }

    /// <summary>The deepest level a card's components reach, starting from <paramref name="currentDepth"/> for <paramref name="components"/>.</summary>
    private static int CalculateMaxDepth(List<CardComponent> components, int currentDepth)
    {
        // Components without sections reach only their own level, which stays the depth until a section goes deeper.
        int maxDepth = currentDepth;

        // Only a section holds components of its own, one level further down.
        foreach (CardComponent component in components)
        {
            // The deepest branch decides the card's depth.
            if (component is SectionComponent section)
            {
                int sectionDepth = CalculateMaxDepth(section.Components, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, sectionDepth);
            }
        }

        // The depth SetRichCard holds against MaxCardDepth.
        return maxDepth;
    }

    /// <summary>Counts <paramref name="components"/> together with every component their sections hold.</summary>
    private static int CountComponents(List<CardComponent> components)
    {
        // The components at this level, each section counted as one.
        int count = components.Count;

        // A section's own components add to the total, at every level.
        foreach (CardComponent component in components)
        {
            if (component is SectionComponent section)
                count += CountComponents(section.Components);
        }

        // The count SetRichCard holds against MaxCardComponents.
        return count;
    }

    // =========================================================================
    // TOOL CONTEXT
    // =========================================================================

    /// <summary>
    /// The agent's context provider, the session of the turn in flight and the conversation, as a tool reads them on each call.
    /// They travel outside the tool's parameters, so the model sees only what it is asked to fill in.
    /// </summary>
    public readonly struct ToolContext
    {
        /// <summary>The agent's context provider, which reads and writes the session's variables.</summary>
        public MorganaAIContextProvider Provider { get; }

        /// <summary>The session of the turn in flight (see <see cref="MorganaAgent.CurrentSession"/>).</summary>
        public AgentSession Session { get; }

        /// <summary>
        /// The conversation the agent serves, which the model can never write.
        /// A domain tool scoping persisted state to this conversation uses it rather than a context variable the model could have set.
        /// </summary>
        public string ConversationId { get; }

        /// <summary>Pairs the agent's context provider with the session and the conversation of the turn in flight.</summary>
        /// <param name="provider">The agent's context provider.</param>
        /// <param name="session">The session of the turn in flight.</param>
        /// <param name="conversationId">The conversation the agent serves.</param>
        public ToolContext(MorganaAIContextProvider provider, AgentSession session, string conversationId)
        {
            Provider = provider;
            Session = session;
            ConversationId = conversationId;
        }
    }
}