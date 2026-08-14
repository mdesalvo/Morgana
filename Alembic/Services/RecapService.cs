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

        return new AgentRecap(agent.ID ?? string.Empty, systemPrompt, tools);
    }
}
