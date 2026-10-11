using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// Shows the prompt an agent's model will read, composed by the framework's own composer over the draft.
/// </summary>
/// <remarks>Nothing is added on top: what the client reads is the prompt itself.</remarks>
public class RecapService : IRecapService
{
    /// <summary>The framework's composer, whose output the client is shown untouched.</summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>
    /// Initializes the recap over the framework's composer.
    /// </summary>
    public RecapService(IPromptComposerService promptComposerService)
    {
        this.promptComposerService = promptComposerService;
    }

    /// <inheritdoc />
    public async Task<AgentRecap> ComposeAsync(AgentDraft agent, bool consultedByOthers = false)
    {
        // The system prompt carries the colleague policy whenever the agent asks a colleague or is asked by one.
        string systemPrompt = await promptComposerService.ComposeAgentInstructionsAsync(
            DraftProjection.ToPrompt(agent),
            agent.Code.Consults.Count > 0 || consultedByOthers);

        // Each tool is shown with the description its model weighs mid-turn and the parameters it fills.
        List<ToolRecap> tools = [];

        foreach (ToolDraft tool in agent.Tools)
        {
            // The tool is projected into the framework's own definition, the shape the composer reads.
            Records.ToolDefinition definition = DraftProjection.ToToolDefinition(tool);

            // The tool's recap pairs the description the framework composes for the model with each parameter as the model fills it:
            // its name, its description, whether it is required and where its value comes from, absent while the scope is unsettled.
            tools.Add(new ToolRecap(
                definition.Name,
                await promptComposerService.ComposeToolDescriptionAsync(definition),
                [.. definition.Parameters.Select(p => new ParameterRecap(
                    p.Name,
                    p.Description,
                    p.Required,
                    string.IsNullOrWhiteSpace(p.Scope) ? null : p.Scope))]));
        }

        // The recap hands back the prompt and the tools in the two moments the model meets them.
        return new AgentRecap(agent.ID ?? string.Empty, systemPrompt, tools);
    }
}