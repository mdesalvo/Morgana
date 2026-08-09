using Alembic.Interfaces;
using Alembic.Model;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IRecapService"/>: drives the framework's own composer over a Draft.
/// </summary>
/// <remarks>
/// There is deliberately almost nothing in this class. Every byte the client is shown comes out of
/// <see cref="IPromptComposerService"/>, and anything Alembic added on top would be a claim about
/// the prompt rather than the prompt.
/// </remarks>
public class RecapService : IRecapService
{
    /// <summary>
    /// The scope marking a parameter resolved from the session's context variables — the ones that
    /// would appear in a held-context declaration if the session held them.
    /// </summary>
    private const string ContextScope = "context";

    /// <summary>
    /// The framework's prompt composer.
    /// </summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>
    /// Initializes the recap service.
    /// </summary>
    /// <param name="promptComposerService">Assembles what the model reads.</param>
    public RecapService(IPromptComposerService promptComposerService)
    {
        this.promptComposerService = promptComposerService;
    }

    /// <inheritdoc />
    public async Task<AgentRecap> ComposeAsync(AgentDraft agent)
    {
        string systemPrompt = await promptComposerService.ComposeAgentInstructionsAsync(
            DraftProjection.ToPrompt(agent));

        List<ToolRecap> tools = [];

        foreach (ToolDraft tool in agent.Tools)
        {
            Records.ToolDefinition definition = DraftProjection.ToToolDefinition(tool);

            tools.Add(new ToolRecap(
                definition.Name,
                await promptComposerService.ComposeToolDescriptionAsync(definition),
                [.. definition.Parameters.Select(p => new ParameterRecap(
                    p.Name,
                    p.Description,
                    p.Required,
                    string.IsNullOrWhiteSpace(p.Scope) ? null : p.Scope))]));
        }

        // The supposition: every context-scoped parameter across the toolkit is populated at once.
        // Ordered ordinally because that is how MorganaAIContextProvider renders the names it hands
        // the template — the point of showing this at all is that it matches, not that it is likely.
        string[] heldVariables =
            [.. agent.Tools
                    .SelectMany(t => t.Parameters)
                    .Where(p => string.Equals(p.Scope, ContextScope, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Name)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)];

        string? heldContext = await promptComposerService.ComposeHeldContextDeclarationAsync(heldVariables);

        return new AgentRecap(agent.ID ?? string.Empty, systemPrompt, tools, heldContext, heldVariables);
    }
}
