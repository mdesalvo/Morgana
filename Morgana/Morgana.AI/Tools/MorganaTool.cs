using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Providers;
using Morgana.Contracts;

namespace Morgana.AI.Tools;

/// <summary>Base class of every tool class, holding the base tools every agent has ahead of its own and the session of the turn in flight.</summary>
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
    /// Wired by <see cref="Adapters.MorganaAgentAdapter"/> when it builds the agent the tool serves.
    /// </param>
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

    /// <summary>
    /// Retrieves a context variable from the agent's conversation context.
    /// Agents should call this before asking the user for any piece of information that may already be available.
    /// </summary>
    /// <param name="variableName">Name of the variable to retrieve (e.g. "customerCode", "invoiceId").</param>
    /// <returns>
    /// The variable value if found, or an instructional message directing the LLM to call <see cref="SetContextVariable"/> or ask the user.
    /// </returns>
    [Description("Reads a value from a CLOSED vocabulary of agent-context keys. Legal variable names are ONLY names a tool's own description flags as a context-scoped input — its 'CONTEXT-SCOPED INPUTS OF THIS TOOL' line (for example 'customerCode') — or reserved ephemeral keys used by the framework. Call this tool BEFORE asking the user for any such value — if it returns a value, USE IT; ask the user only if it reports missing. While the lookup is pending you have learned nothing yet, so do NOT write text asking the user for the very value you are looking up: resolve it first, then write once, against what you then know. A question your own next tool call answers reaches the user as a demand for something you already had. NEVER call this tool with a name taken from the user's message (a species, product code, entity, or topic the user is asking ABOUT) — those are query subjects, not variable names; use your domain tool to look them up instead.")]
    public Task<object> GetContextVariable(
        [Description("The name of the variable for which you are requesting read access to the agent's context (for example: customerCode)")] string variableName)
    {
        ToolContext ctx = getToolContext();
        object? value = ctx.Provider.GetVariable(ctx.Session, variableName);

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

        return Task.FromResult<object>(
            $"Information {variableName} not available in context: you need to engage SetContextVariable to set it.");
    }

    /// <summary>
    /// Stores a variable in the agent's conversation context.
    /// If the variable is declared as shared in configuration, it is automatically persisted in the conversation-scoped
    /// <c>shared_context</c> registry so other agents of the same conversation can hydrate it on their next turn.
    /// </summary>
    /// <param name="variableName">Name of the variable to set (e.g. "customerCode", "invoiceId").</param>
    /// <param name="variableValue">Value to store.</param>
    /// <returns>Confirmation message for the LLM.</returns>
    [Description("Writes a value to a CLOSED vocabulary of agent-context keys. Legal variable names are ONLY names a tool's own description flags as a context-scoped input — its 'CONTEXT-SCOPED INPUTS OF THIS TOOL' line (for example 'customerCode') — or reserved ephemeral keys managed by the framework. The only legitimate writes are values for such an input — and every such value you obtain MUST be written, whether the user gave it in answer to your question or volunteered it unprompted in their very first message. Write it as soon as you have it, before or alongside the tool call that consumes it; a value you merely pass to a tool without persisting is gone for every later turn. NEVER invent a variable name, NEVER store user input under a new key, NEVER use this tool to 'save progress', 'grant access', 'remember what the user said', or mirror the subject of the user's question. If you feel the urge to invent a key, STOP: call your domain tool or answer the user in plain text instead.")]
    public async Task<object> SetContextVariable(
        [Description("The name of the variable for which you are requesting write access to the agent's context (for example: 'customerCode')")] string variableName,
        [Description("The value of the variable for which you are requesting write access to the agent's context (for example: 'P994E')")] string variableValue)
    {
        ToolContext ctx = getToolContext();
        await ctx.Provider.SetVariableAsync(ctx.Session, variableName, variableValue);

        toolLogger.LogInformation(
            Constants.ObservableLogs.ContextSet,
            Constants.ObservableLogs.ToolName, GetType().Name, Constants.ObservableLogs.Set, variableName, variableValue);

        return $"Information {variableName} inserted in context with value: {variableValue}";
    }

    // =========================================================================
    // TURN ENDING SYSTEM TOOL
    // =========================================================================

    /// <summary>
    /// Hands the conversation back to Morgana once the current turn ends. An agent stays in service
    /// by default, so this out-of-band signal, kept in the ephemeral context beside quick replies and
    /// rich cards, is the one declaration a turn makes about where the conversation goes next.
    /// </summary>
    /// <returns>Confirmation message for the LLM.</returns>
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
    /// </summary>
    /// <remarks>
    /// A presentation tool is called before the text it accompanies, so its result is the last thing
    /// the model reads before writing. Told as a fact, it lets the model see that the words are still
    /// owed. A turn that already wrote them reads no such line, so nothing invites it to write them twice.
    /// </remarks>
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

    /// <summary>
    /// Sets quick reply buttons to be rendered in the user interface below the agent's response.
    /// </summary>
    /// <param name="quickReplies">
    /// Array of quick reply definitions. Each object requires:
    /// <c>id</c> (identifier), <c>label</c> (display text, may include emoji), <c>value</c> (message sent on tap),
    /// plus the optional <c>termination</c> flag.
    /// </param>
    /// <returns>Confirmation message for the LLM.</returns>
    [Description("Draws the interactive buttons the user can click. The payload is a JSON array; each button carries 'id' (unique identifier), 'label' (the display text, emoji encouraged) and 'value' (the complete natural message sent on the user's behalf when clicked), plus the optional 'termination' flag that only the exit button carries. Call this tool BEFORE writing your text response — the buttons render below the message. WHETHER a turn emits buttons at all and which ones is decided entirely by the Quick Reply Doctrine; this tool only draws what that rule has already settled.")]
    public async Task<object> SetQuickReplies(
        [Description("Array of button definitions. Each button: 'id' (unique, e.g. 'invoice-001'), 'label' (display text, e.g. '📄 Invoice INV-001'), 'value' (the message sent on click, e.g. 'Show details for invoice INV-001') and optionally 'termination' (boolean, exit button only). Format: [{\"id\":\"option1\",\"label\":\"🔧 Option 1\",\"value\":\"User message for option 1\"}]. Labels are action-oriented; values read as something the user would actually type.")] List<QuickReply> quickReplies)
    {
        // Validate input — empty list is a no-op
        if (quickReplies == null || quickReplies.Count == 0)
        {
            toolLogger.LogWarning("SetQuickReplies called with no quick replies");
            return "Warning: No quick replies were set (empty data).";
        }

        // Store serialized quick replies in ephemeral context; agents retrieve them via ExecuteAgentAsync before sending response
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

    /// <summary>
    /// Sets a rich card for structured visual presentation of complex data in the user interface.
    /// Use when presenting invoices, profiles, reports, or any content that benefits from
    /// visual hierarchy over plain text.
    /// </summary>
    /// <param name="richCard">
    /// JSON string containing the rich card structure with title, subtitle and components array.
    /// </param>
    /// <returns>Confirmation message for the LLM.</returns>
    /// <remarks>
    /// <para><strong>Constraints:</strong></para>
    /// <list type="bullet">
    /// <item>Maximum nesting depth: 3 levels</item>
    /// <item>Maximum 50 components total</item>
    /// </list>
    /// </remarks>
    [Description("Render structured information as a visual card instead of a wall of text. Use for invoices, profiles, reports, comparisons — any data that benefits from visual hierarchy. Call this tool before writing your text response: the card renders below your message.\n\n=== ROOT STRUCTURE (MANDATORY) ===\n{\"title\": \"string (required)\", \"subtitle\": \"string (optional)\", \"components\": [array (required)]}\nRoot MUST carry 'title' and 'components'. NEVER 'sections', 'data' or any other wrapper.\n\n=== THE 8 COMPONENT TYPES ===\nCompose these like building blocks: pick each one by the SEMANTIC NATURE of the data (labeled field? prose? list? status? picture?), group related ones with 'section', separate groups with 'divider'.\n\n1. text_block — narrative prose: explanations, warnings, notes. Not for labeled data (use key_value) or item lists (use list).\n   content (string, required) · style (optional): \"normal\" (default) | \"bold\" | \"muted\" | \"small\"\n   {\"type\": \"text_block\", \"content\": \"⚠️ Payment is 53 days overdue. Late fees may apply.\", \"style\": \"bold\"}\n\n2. key_value — one labeled field: any \"Label: Value\" datum. For 2+ homogeneous fields side by side use grid instead.\n   key (string, required) · value (string, required) · emphasize (bool, optional, default false)\n   {\"type\": \"key_value\", \"key\": \"Due Date\", \"value\": \"15/12/2025\", \"emphasize\": true}\n\n3. divider — a horizontal rule between logical groups. Not between every component.\n   no properties\n   {\"type\": \"divider\"}\n\n4. list — an enumeration of related items: line items, steps, features. For labeled pairs use key_value or grid.\n   items (array of strings, required) · style (optional): \"bullet\" (default) | \"numbered\" | \"plain\"\n   {\"type\": \"list\", \"items\": [\"Premium 100Mbps Plan: €90.00\", \"Static IP: €5.00\"], \"style\": \"bullet\"}\n\n5. section — a nestable container grouping components under a heading.\n   title (string, required) · subtitle (string, optional) · components (array, required)\n   Max depth 3 from root: components > section > section > section is allowed, a fourth level is not.\n   {\"type\": \"section\", \"title\": \"Billing Details\", \"components\": [{\"type\": \"key_value\", \"key\": \"Period\", \"value\": \"October 2025\"}]}\n\n6. grid — a compact multi-column layout for homogeneous key-value pairs (profile fields, quarterly figures). Items are simple pairs only, never nested content.\n   columns (integer, required, 2-4 recommended) · items (array of {\"key\", \"value\"}, required)\n   {\"type\": \"grid\", \"columns\": 3, \"items\": [{\"key\": \"Q1\", \"value\": \"€45K\"}, {\"key\": \"Q2\", \"value\": \"€52K\"}, {\"key\": \"Q3\", \"value\": \"€48K\"}]}\n\n7. badge — a short status or category tag: Paid, Overdue, Priority, Draft.\n   text (string, required) · variant (optional): \"success\" (positive: Paid, Active) | \"warning\" (attention: Pending, Due Soon) | \"error\" (negative: Overdue, Failed) | \"info\" (New, Beta) | \"neutral\" (default, no semantics)\n   {\"type\": \"badge\", \"text\": \"Overdue\", \"variant\": \"error\"}\n\n8. image — a picture from a URL. Whenever tool output carries an image URL, use this component and NEVER a markdown link inside a text_block.\n   src (string, required) · alt (string, recommended) · caption (string, optional) · size (optional): \"small\" (200px) | \"medium\" (400px, default) | \"large\" (600px) | \"full\" (container width)\n   {\"type\": \"image\", \"src\": \"https://example.com/product.jpg\", \"alt\": \"Premium Widget Model X\", \"caption\": \"In store - €49.99\", \"size\": \"medium\"}\n\n=== RULES ===\nEvery component object carries 'type' as its FIRST property and that type is exactly one of the eight above. Serialize the whole structure to a JSON string before passing it to the tool.\n\n=== EXAMPLES ===\n\nFlat (user profile):\n{\"title\": \"User Profile\", \"subtitle\": \"Account #USR-123\", \"components\": [{\"type\": \"image\", \"src\": \"https://example.com/avatar.jpg\", \"alt\": \"User avatar\", \"size\": \"small\"}, {\"type\": \"key_value\", \"key\": \"Name\", \"value\": \"[User Name]\"}, {\"type\": \"key_value\", \"key\": \"Member Since\", \"value\": \"January 2023\"}, {\"type\": \"divider\"}, {\"type\": \"badge\", \"text\": \"Active\", \"variant\": \"success\"}]}\n\nNested (invoice with sections):\n{\"title\": \"Invoice INV-2025-001\", \"subtitle\": \"Due 15/12/2025\", \"components\": [{\"type\": \"section\", \"title\": \"Billing Details\", \"components\": [{\"type\": \"key_value\", \"key\": \"Customer\", \"value\": \"[Company Name]\"}, {\"type\": \"key_value\", \"key\": \"Status\", \"value\": \"Pending\", \"emphasize\": true}]}, {\"type\": \"divider\"}, {\"type\": \"section\", \"title\": \"Line Items\", \"components\": [{\"type\": \"list\", \"items\": [\"Premium 100Mbps Plan: €90.00\", \"Installation Fee: €35.00\"], \"style\": \"bullet\"}]}, {\"type\": \"divider\"}, {\"type\": \"key_value\", \"key\": \"TOTAL\", \"value\": \"€130.00\", \"emphasize\": true}]}")]
    public async Task<object> SetRichCard(
        [Description("JSON string carrying the complete card structure — see this tool's description for the root shape and the eight component types. It MUST be strictly valid JSON, deserializable on the first attempt: a malformed payload is silently dropped and the user sees nothing. If uncertain, simplify the card rather than risk an unclosed brace.")] string richCard)
    {
        try
        {
            // Parse JSON string into strongly-typed RichCard structure; gracefully reject invalid JSON
            RichCard? parsedRichCard = JsonSerializer.Deserialize<RichCard>(
                richCard, Records.DefaultJsonSerializerOptions);
            if (parsedRichCard == null)
            {
                toolLogger.LogWarning("SetRichCard called with invalid JSON structure");
                return "Error: Rich card JSON structure is invalid.";
            }

            // Enforce depth constraint (max 3 levels: card → section → nested content)
            int depth = CalculateMaxDepth(parsedRichCard.Components, 1);
            if (depth > 3)
            {
                toolLogger.LogWarning("SetRichCard called with excessive nesting depth: {Depth} (max 3)", depth);
                return $"Error: Rich card exceeds maximum nesting depth of 3 (found: {depth}). Please simplify the card structure.";
            }

            // Enforce component limit (max 50 total, including nested components in sections)
            int totalComponents = CountComponents(parsedRichCard.Components);
            if (totalComponents > 50)
            {
                toolLogger.LogWarning("SetRichCard called with too many components: {TotalComponents} (max 50)", totalComponents);
                return $"Error: Rich card has too many components: {totalComponents} (max 50). Please create a more focused card.";
            }

            // Store original JSON string (not the parsed object) in ephemeral context; agents retrieve it via ExecuteAgentAsync
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
            // JSON parsing failed; return error message the LLM can act on
            toolLogger.LogError(ex, "Failed to parse rich card JSON in SetRichCard");
            return "Error: Rich card JSON format is invalid. Please check the structure and try again.";
        }
    }

    /// <summary>
    /// Calculates the maximum nesting depth of components in a card.
    /// Used by SetRichCard to enforce 3-level depth constraint.
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
    /// Used by SetRichCard to enforce 50-component limit.
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