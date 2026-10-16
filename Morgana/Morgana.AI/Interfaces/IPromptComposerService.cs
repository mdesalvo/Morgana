using System.Text.Json;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Assembles every piece of framework text destined for a domain agent's model: the composed system
/// prompt (framework layer + domain layer, fenced), the colleagues it may consult and the question a
/// colleague puts to it. Sibling of <see cref="IPromptResolverService"/> — that one abstracts
/// <em>where prompts come from</em>, this one abstracts <em>how they are assembled into what the
/// model reads</em>.
/// </summary>
public interface IPromptComposerService
{
    /// <summary>
    /// Composes an agent's full system prompt: the framework layer (target, personality, global
    /// policies, instructions, formatting) followed by the domain layer, each inside its own fence.
    /// The fences are load-bearing — both layers carry the same four section labels, so without
    /// them the model sees each label twice with nothing marking which is which and the
    /// framework's claim to precedence names a boundary the model cannot locate.
    /// </summary>
    /// <param name="domainPrompt">The agent's own prompt, resolved from <c>agents.json</c>.</param>
    /// <param name="peerCapable">
    /// True when this agent consults a colleague or is itself consulted, which is what admits the
    /// peer-consultation policy into the rendered rules. False leaves an agent outside the topology
    /// with a prompt that carries no peer-consultation text.
    /// </param>
    /// <returns>The composed instructions, ready for <c>ChatOptions.Instructions</c>.</returns>
    Task<string> ComposeAgentInstructionsAsync(Records.Prompt domainPrompt, bool peerCapable = false);

    /// <summary>
    /// Produces the description that a tool presents to the model: the authored one, followed by the
    /// schema of what it returns and closed by the framework's guidance when the tool requires execution approval.
    /// </summary>
    /// <param name="toolDefinition">The tool definition from <c>morgana.json</c> or <c>agents.json</c>.</param>
    Task<string> ComposeToolDescriptionAsync(Records.ToolDefinition toolDefinition);

    /// <summary>
    /// Appends to a description the schema of what the tool returns, since no provider transmits a function's return schema.
    /// </summary>
    /// <param name="description">The tool's description as authored.</param>
    /// <param name="returnSchema">The tool's return schema; null or not a JSON object leaves the description as it is.</param>
    /// <returns>The description followed by the return contract; the description unchanged when there is nothing to append.</returns>
    Task<string> ComposeReturnContractAsync(string description, JsonElement? returnSchema);

    /// <summary>
    /// Produces the description under which a colleague is offered as a callable function: the
    /// colleague's own statement of what falls to it (its <c>Territory</c>, carried on the card as
    /// its description), with nothing of the framework's added to it.
    /// </summary>
    /// <returns>The description to expose on the generated <c>AIFunction</c>.</returns>
    Task<string> ComposePeerDescriptionAsync(A2A.AgentCard peerCard);

    /// <summary>
    /// Produces the block naming the colleagues an agent holds, spliced into its own instructions.
    /// </summary>
    /// <remarks>
    /// One rung above <see cref="ComposePeerDescriptionAsync"/> on the placement ladder and the rung
    /// that decides whether the lower one is ever read: a colleague's own description reaches the
    /// model only once it is already weighing that function, which is exactly what an agent about to
    /// answer "this is not on my books" never does. Static for the agent's life, so it rides in the
    /// cached prefix rather than being re-sent per turn.
    /// </remarks>
    /// <param name="colleagues">Function name to the colleague's own statement of what falls to it.</param>
    /// <returns>The block to append, or <c>null</c> when there are no colleagues or no template.</returns>
    Task<string?> ComposeColleaguesDeclarationAsync(IReadOnlyDictionary<string, string> colleagues);

    /// <summary>
    /// Produces the declaration added to the instructions of a landing turn, in which the agent introduces itself and learns the buttons beneath its text.
    /// </summary>
    /// <remarks>
    /// Composed once per agent, since the menu is fixed at the agent's birth.
    /// </remarks>
    /// <param name="menu">The actions that the user receives as buttons beneath the agent's text, in order.</param>
    /// <returns>The declaration to add; <c>null</c> when the deployment declares none.</returns>
    Task<string?> ComposeLandingDeclarationAsync(IReadOnlyList<Records.OfferedAction> menu);

    /// <summary>
    /// Produces the whole of what an answering agent reads on a run serving a colleague. The instructions
    /// tell it that its reader is not the user and what the question may not claim. The question itself
    /// is the message of the turn, inside the fence marking it as data.
    /// </summary>
    /// <param name="callerIntent">Intent of the asking agent; <c>null</c> when the caller named none,
    /// which the composer renders as an unnamed caller rather than as a name.</param>
    /// <param name="question">What the colleague asked, in its own words.</param>
    /// <returns>The run's instructions (<c>null</c> where none is declared) and the question, falling back to the bare question where no fence is declared.</returns>
    Task<Records.ConsultationRequest> ComposeConsultationRequestAsync(string? callerIntent, string question);

    /// <summary>
    /// Produces what the model reads after a turn that it wrote without closing it, asking for the closure alone.
    /// </summary>
    /// <returns>The request; <c>null</c> when no template is declared and such turns stay unclosed.</returns>
    Task<string?> ComposeTurnClosureRequestAsync();

    /// <summary>
    /// Produces what the model reads in place of CloseTurn's result when the closure that it sent was refused.
    /// </summary>
    /// <param name="reason">Why the closure was refused, as a fact.</param>
    /// <returns>The composed answer, falling back to the bare reason where no template is declared.</returns>
    Task<string> ComposeTurnClosureNotAcceptedAsync(string reason);

    /// <summary>
    /// Produces what the model reads in place of a tool result an earlier turn received.
    /// </summary>
    /// <param name="result">The result as the tool returned it.</param>
    /// <returns>The wrapped result; <c>null</c> when no template is declared and results stay as they were.</returns>
    Task<string?> ComposeEarlierToolResultAsync(string result);

    /// <summary>
    /// Produces what the model reads in place of the result of a workflow tool: the result under the label that says where the workflow stands.
    /// </summary>
    /// <param name="workflow">The workflow that the tool belongs to.</param>
    /// <param name="step">The step the workflow now stands at; <c>null</c> when the call ended it.</param>
    /// <param name="result">The result as the tool returned it.</param>
    /// <returns>The wrapped result; <c>null</c> when no template is declared and the result stays as it was.</returns>
    Task<string?> ComposeWorkflowResultAsync(string workflow, string? step, string result);

    /// <summary>
    /// Produces one of the ToolInjections texts, by name and with its values spliced in.
    /// </summary>
    /// <param name="name">Which text (see <c>Constants.ToolInjections</c>).</param>
    /// <param name="values">Placeholder to the value that it stands for, if the text carries any.</param>
    Task<string> ComposeToolInjectionAsync(string name, IReadOnlyDictionary<string, string>? values = null);
}
