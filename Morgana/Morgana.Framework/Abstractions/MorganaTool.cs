using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.Framework.Providers;

namespace Morgana.Framework.Abstractions;

/// <summary>
/// Base class for agent tools. Provides built-in context variable access (Get/Set/Drop)
/// and UI output tools (quick replies, rich cards). Domain-specific tools extend this class.
/// </summary>
/// <remarks>
/// <para>Tools access the session's conversation context through a <see cref="ToolContext"/> instance
/// returned by the <c>getToolContext</c> factory supplied at construction. The factory is evaluated
/// lazily on each invocation so it always captures the in-flight <see cref="AgentSession"/>
/// from <see cref="MorganaAgent.CurrentSession"/>.</para>
///
/// <para>Keeping <see cref="AgentSession"/> out of tool method signatures is intentional:
/// tool methods are inspected by <c>AIFunctionFactory</c> via reflection to generate LLM tool schemas.
/// Session must not appear as a parameter or it would be exposed to the LLM.</para>
///
/// <para><strong>Integration overview:</strong></para>
/// <code>
/// MorganaTool (base — context + UI tools)
///   └── BillingTool / ContractTool / ... (domain-specific tools)
///
/// Wiring in concrete agent constructor:
///   new BillingTool(
///       logger,
///       () => new ToolContext(aiContextProvider, CurrentSession!));
/// </code>
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
    /// Typically wired as <c>() =&gt; new ToolContext(aiContextProvider, CurrentSession!)</c>
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
    // CONTEXT SYSTEM TOOLS
    // =========================================================================

    /// <summary>
    /// Retrieves a context variable from the agent's conversation context.
    /// Agents should call this before asking the user for any piece of information
    /// that may already be available.
    /// </summary>
    /// <param name="variableName">Name of the variable to retrieve (e.g. "userId", "invoiceId").</param>
    /// <returns>
    /// The variable value if found, or an instructional message directing the LLM
    /// to call <see cref="SetContextVariable"/> or ask the user.
    /// </returns>
    /// <remarks>
    /// <para><strong>Skill rule:</strong> Before asking for ANY information from the user,
    /// ALWAYS attempt to retrieve it from context using <see cref="GetContextVariable"/>.
    /// Ask the user only if this tool indicates the information is missing.</para>
    /// </remarks>
    public Task<object> GetContextVariable(string variableName)
    {
        ToolContext ctx = getToolContext();
        object? value = ctx.Provider.GetVariable(ctx.Session, variableName);

        if (value != null)
        {
            toolLogger.LogInformation(
                $"{nameof(MorganaTool)} ({GetType().Name}) HIT variable '{variableName}' from agent context. Value is: {value}");

            return Task.FromResult(value);
        }

        toolLogger.LogInformation(
            $"{nameof(MorganaTool)} ({GetType().Name}) MISS variable '{variableName}' from agent context.");

        return Task.FromResult<object>(
            $"Information {variableName} not available in context: you need to engage SetContextVariable to set it.");
    }

    /// <summary>
    /// Stores a variable in the agent's conversation context.
    /// If the variable is declared as shared in configuration, it is automatically broadcast to sibling agents.
    /// </summary>
    /// <param name="variableName">Name of the variable to set (e.g. "userId", "invoiceId").</param>
    /// <param name="variableValue">Value to store.</param>
    /// <returns>Confirmation message for the LLM.</returns>
    public Task<object> SetContextVariable(string variableName, string variableValue)
    {
        ToolContext ctx = getToolContext();
        ctx.Provider.SetVariable(ctx.Session, variableName, variableValue);

        return Task.FromResult<object>(
            $"Information {variableName} inserted in context with value: {variableValue}");
    }

    // =========================================================================
    // QUICK REPLY SYSTEM TOOL
    // =========================================================================

    /// <summary>
    /// Sets quick reply buttons to be rendered in the user interface below the agent's response.
    /// </summary>
    /// <param name="quickReplies">
    /// JSON array of quick reply definitions. Each object requires:
    /// <c>id</c> (identifier), <c>label</c> (display text, may include emoji), <c>value</c> (message sent on tap).
    /// </param>
    /// <returns>Confirmation message for the LLM.</returns>
    /// <remarks>
    /// <para><strong>Expected JSON format:</strong></para>
    /// <code>
    /// [
    ///   {
    ///     "id": "no-internet",
    ///     "label": "🔴 No Internet Connection",
    ///     "value": "Show me the no-internet assistance guide"
    ///   }
    /// ]
    /// </code>
    /// </remarks>
    public Task<object> SetQuickReplies(string quickReplies)
    {
        try
        {
            List<Records.QuickReply>? parsedQuickReplies = JsonSerializer.Deserialize<List<Records.QuickReply>>(quickReplies);
            if (parsedQuickReplies == null || !parsedQuickReplies.Any())
            {
                toolLogger.LogWarning("SetQuickReplies called with empty or invalid JSON");
                return Task.FromResult<object>("Warning: No quick replies were set (empty or invalid data).");
            }

            ToolContext ctx = getToolContext();
            ctx.Provider.SetVariable(ctx.Session, "quick_replies", quickReplies);

            toolLogger.LogInformation($"LLM set {parsedQuickReplies.Count} quick reply buttons via SetQuickReplies tool");

            return Task.FromResult<object>(
                $"Quick reply buttons set successfully. The user will see {parsedQuickReplies.Count} interactive options. " +
                $"Now provide your text response to the user - the quick reply buttons will appear below your message.");
        }
        catch (JsonException ex)
        {
            toolLogger.LogError(ex, "Failed to parse quick replies JSON in SetQuickReplies");
            return Task.FromResult<object>(
                "Error: Quick replies JSON format is invalid. Expected format: " +
                "[{\"id\": \"option1\", \"label\": \"🔧 Option 1\", \"value\": \"User message for option 1\"}]");
        }
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
    /// JSON string containing the rich card structure with title, subtitle, and components array.
    /// </param>
    /// <returns>Confirmation message for the LLM.</returns>
    /// <remarks>
    /// <para><strong>Constraints:</strong></para>
    /// <list type="bullet">
    /// <item>Maximum nesting depth: 3 levels</item>
    /// <item>Maximum 50 components total</item>
    /// </list>
    /// </remarks>
    public Task<object> SetRichCard(string richCard)
    {
        try
        {
            Records.RichCard? parsedRichCard = JsonSerializer.Deserialize<Records.RichCard>(
                richCard, new JsonSerializerOptions
                {
                    AllowOutOfOrderMetadataProperties = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
                    PropertyNameCaseInsensitive = true
                });
            if (parsedRichCard == null)
            {
                toolLogger.LogWarning("SetRichCard called with invalid JSON structure");
                return Task.FromResult<object>("Error: Rich card JSON structure is invalid.");
            }

            int depth = CalculateMaxDepth(parsedRichCard.Components, 1);
            if (depth > 3)
            {
                toolLogger.LogWarning($"SetRichCard called with excessive nesting depth: {depth} (max 3)");
                return Task.FromResult<object>(
                    $"Error: Rich card exceeds maximum nesting depth of 3 (found: {depth}). " +
                    $"Please simplify the card structure.");
            }

            int totalComponents = CountComponents(parsedRichCard.Components);
            if (totalComponents > 50)
            {
                toolLogger.LogWarning($"SetRichCard called with too many components: {totalComponents} (max 50)");
                return Task.FromResult<object>(
                    $"Error: Rich card has too many components: {totalComponents} (max 50). " +
                    $"Please create a more focused card.");
            }

            ToolContext ctx = getToolContext();
            ctx.Provider.SetVariable(ctx.Session, "rich_card", richCard);

            toolLogger.LogInformation(
                $"LLM set rich card '{parsedRichCard.Title}' with {totalComponents} components " +
                $"(depth: {depth}) via SetRichCard tool");

            return Task.FromResult<object>(
                $"Rich card set successfully. The user will see a structured visual card titled '{parsedRichCard.Title}'. " +
                $"You can now provide additional context or explanation in text if needed.");
        }
        catch (JsonException ex)
        {
            toolLogger.LogError(ex, "Failed to parse rich card JSON in SetRichCard");
            return Task.FromResult<object>(
                "Error: Rich card JSON format is invalid. Please check the structure and try again.");
        }
    }

    /// <summary>
    /// Calculates the maximum nesting depth of components in a card.
    /// Used by SetRichCard to enforce 3-level depth constraint.
    /// </summary>
    /// <param name="components">List of card components to analyze</param>
    /// <param name="currentDepth">Current depth level (starts at 1)</param>
    /// <returns>Maximum depth found in the component tree</returns>
    private int CalculateMaxDepth(List<Records.CardComponent> components, int currentDepth)
    {
        int maxDepth = currentDepth;

        foreach (Records.CardComponent component in components)
        {
            if (component is Records.SectionComponent section)
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
    private int CountComponents(List<Records.CardComponent> components)
    {
        int count = components.Count;

        foreach (Records.CardComponent component in components)
        {
            if (component is Records.SectionComponent section)
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

        public ToolContext(MorganaAIContextProvider provider, AgentSession session)
        {
            Provider = provider;
            Session = session;
        }
    }
}
