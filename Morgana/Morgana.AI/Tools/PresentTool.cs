using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;

namespace Morgana.AI.Tools;

/// <summary>
/// The framework's own tool: <c>Present</c>, with which an agent presents its domain and receives the menu that closes the turn.
/// </summary>
/// <remarks>
/// Declared the way a domain tool is, so the model reads its description from the method and the startup contract check weighs it with the others.
/// </remarks>
public class PresentTool : MorganaTool
{
    /// <summary>
    /// Initializes the tool for one agent.
    /// </summary>
    /// <param name="toolLogger">Logger for tool diagnostics.</param>
    /// <param name="getToolContext">Factory returning the <see cref="MorganaTool.ToolContext"/> for the current turn.</param>
    public PresentTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
    }

    /// <summary>
    /// Tells the agent which actions the user receives as buttons, so that its presentation can speak of them.
    /// </summary>
    /// <returns>The named result the tool loop turns into its authored text: the menu's labels or the fact that there is none.</returns>
    [Description("Returns the actions the user receives as buttons beneath your text. Your introduction is the text you write after it: a few sentences of your own on what you can do for them.")]
    [SystemTool]
    public Task<Records.FrameworkToolResult> Present()
    {
        // The menu is the agent's own and was fixed when the agent was built: the model gives it a voice and never chooses it.
        IReadOnlyList<Records.ReplyAction> menu = getToolContext().Provider.Menu;

        // The labels in menu order are what the presentation can refer to; an agent that offers nothing says so,
        // so the presentation does not promise buttons that will not come.
        return Task.FromResult(new Records.FrameworkToolResult(Constants.ToolInjections.MenuOffered, new Dictionary<string, string>
        {
            [Constants.Placeholders.Actions] = menu.Count == 0 ? "none" : string.Join(", ", menu.Select(action => action.Label))
        }));
    }
}