using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IRecapService"/>: drives the framework's own composer over a Draft.
/// Every byte the client is shown comes out of <see cref="IPromptComposerService"/>, since anything Alembic added would be a claim about the prompt rather than the prompt.
/// </summary>
public class RecapService : IRecapService
{
    /// <summary>The framework's composer, the one a running agent's prompt comes out of.</summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>Binds the recap to the framework's composer.</summary>
    /// <param name="promptComposerService">Assembles what the model reads.</param>
    public RecapService(IPromptComposerService promptComposerService)
    {
        this.promptComposerService = promptComposerService;
    }

    /// <summary>
    /// Composes the agent's system prompt and each tool's description as two blocks, the way the model reads them at runtime.
    /// The system prompt is read once before the conversation and each tool description as it is weighed mid-turn, which <see cref="AgentRecap"/> keeps apart.
    /// </summary>
    /// <param name="agent">The agent to recap, projected into the framework's own <see cref="Records.Prompt"/> shape.</param>
    /// <param name="consultedByOthers">True when another agent of the domain declares it may consult this one.</param>
    /// <returns>The system prompt this agent's model will read, plus one <see cref="ToolRecap"/> per declared tool.</returns>
    public async Task<AgentRecap> ComposeAsync(AgentDraft agent, bool consultedByOthers = false)
    {
        // The consultation policy is rendered for either end of a consultation, the asking agent's and the asked one's.
        string systemPrompt = await promptComposerService.ComposeAgentInstructionsAsync(
            DraftProjection.ToPrompt(agent),
            agent.Code.Consults.Count > 0 || consultedByOthers);

        // One recap per declared tool, in the order the agent's toolkit lists them.
        List<ToolRecap> tools = [];

        foreach (ToolDraft tool in agent.Tools)
        {
            // The tool as the framework would project it from the generated C#.
            Records.ToolDefinition definition = DraftProjection.ToToolDefinition(tool);

            // The description with the framework's context guidance spliced in, beside each parameter as its schema shows it;
            // a blank scope is a value the model authors and is shown as none.
            tools.Add(new ToolRecap(
                definition.Name,
                await promptComposerService.ComposeToolDescriptionAsync(definition),
                [.. definition.Parameters.Select(parameter => new ParameterRecap(
                    parameter.Name,
                    parameter.Description,
                    parameter.Required,
                    string.IsNullOrWhiteSpace(parameter.Scope) ? null : parameter.Scope))]));
        }

        // The agent's intent names the recap on screen; an agent not yet named shows none.
        return new AgentRecap(agent.ID ?? string.Empty, systemPrompt, tools);
    }
}