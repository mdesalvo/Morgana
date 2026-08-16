using Distiller.Interfaces;
using Distiller.Model;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Distiller.Services;

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
    /// <remarks>
    /// Composed as two separate blocks rather than concatenated, because that is how the model
    /// itself reads them at runtime — the whole system prompt once, before the conversation starts,
    /// then each tool description as it is weighed mid-turn. <see cref="AgentRecap"/> keeps that
    /// same split so what the client is shown here matches the two moments it actually happens.
    /// </remarks>
    /// <param name="agent">The agent to recap, projected into the framework's own <see cref="Records.Prompt"/> shape.</param>
    /// <returns>The system prompt this agent's model will read, plus one <see cref="ToolRecap"/> per declared tool.</returns>
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
