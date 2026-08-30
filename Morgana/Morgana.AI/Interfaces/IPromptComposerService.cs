namespace Morgana.AI.Interfaces;

/// <summary>
/// Assembles every piece of text destined for a domain agent's model: the composed system prompt
/// (framework layer + domain layer, fenced), the tool descriptions, and the per-turn declaration of
/// held context variables. Sibling of <see cref="IPromptResolverService"/> — that one abstracts
/// <em>where prompts come from</em>, this one abstracts <em>how they are assembled into what the
/// model reads</em>.
/// </summary>
/// <remarks>
/// <para>
/// Default implementation: <c>ConfigurationPromptComposerService</c>, which reads the framework
/// layer and the injection templates from <c>morgana.json</c> through <see cref="IPromptResolverService"/>.
/// </para>
/// </remarks>
public interface IPromptComposerService
{
    /// <summary>
    /// Composes an agent's full system prompt: the framework layer (target, personality, global
    /// policies, instructions, formatting) followed by the domain layer, each inside its own fence.
    /// The fences are load-bearing — both layers carry the same four section labels, so without
    /// them the model sees each label twice with nothing marking which is which, and the
    /// framework's claim to precedence names a boundary the model cannot locate.
    /// </summary>
    /// <param name="domainPrompt">The agent's own prompt, resolved from <c>agents.json</c>.</param>
    /// <param name="peerCapable">
    /// True when this agent consults a colleague or is itself consulted, which is what admits the
    /// peer-consultation policy into the rendered rules. False leaves an agent outside the topology
    /// reading exactly the prompt it read before peer consultation existed.
    /// </param>
    /// <returns>The composed instructions, ready for <c>ChatOptions.Instructions</c>.</returns>
    Task<string> ComposeAgentInstructionsAsync(Records.Prompt domainPrompt, bool peerCapable = false);

    /// <summary>
    /// Produces the description a tool presents to the model. When the tool declares
    /// context-scoped parameters, the <c>ToolDescriptionContextGuidance</c> injection template is
    /// appended with its <c>((context_parameters))</c> placeholder resolved to those parameter
    /// names; otherwise the authored description is returned unchanged.
    /// </summary>
    /// <param name="toolDefinition">The tool definition from <c>morgana.json</c> or <c>agents.json</c>.</param>
    /// <returns>The description to expose on the generated <c>AIFunction</c>.</returns>
    Task<string> ComposeToolDescriptionAsync(Records.ToolDefinition toolDefinition);

    /// <summary>
    /// Produces the description under which a colleague is offered as a callable function: the
    /// framework's rules for consulting one, then the colleague's own statement of what falls to it
    /// (its <c>ConsultMeFor</c>, carried on the card as its description).
    /// </summary>
    /// <returns>The description to expose on the generated <c>AIFunction</c>.</returns>
    Task<string> ComposePeerDescriptionAsync(A2A.AgentCard peerCard);

    /// <summary>
    /// Produces the block naming the colleagues an agent holds, spliced into its own instructions.
    /// </summary>
    /// <remarks>
    /// One rung above <see cref="ComposePeerDescriptionAsync"/> on the placement ladder, and the rung
    /// that decides whether the lower one is ever read: a colleague's own description reaches the
    /// model only once it is already weighing that function, which is exactly what an agent about to
    /// answer "this is not on my books" never does. Static for the agent's life, so it rides in the
    /// cached prefix rather than being re-sent per turn like the held-context declaration.
    /// </remarks>
    /// <param name="colleagues">Function name to the colleague's own statement of what falls to it.</param>
    /// <returns>The block to append, or <c>null</c> when there are no colleagues or no template.</returns>
    Task<string?> ComposeColleaguesDeclarationAsync(IReadOnlyDictionary<string, string> colleagues);

    /// <summary>
    /// Produces the note placed in front of a colleague's question — the one signal telling the
    /// answering agent that this turn's reader is not the user.
    /// </summary>
    /// <returns>The note, or an empty string when the prompt layer declares no such template.</returns>
    Task<string> ComposeConsultationRequestAsync(string callerIntent);

    /// <summary>
    /// Produces the per-turn declaration handing the session's currently-held context variables
    /// directly to the model — name AND value, not just the name. This is the one composed fragment
    /// carrying a <em>fact</em> rather than a rule: tool descriptions are built once at agent creation
    /// and can only state the contract ("this tool takes a customerCode"), never the state
    /// ("customerCode is T780C right now"). Handing over the value outright turns recall from a
    /// discretionary tool call into a fact already in front of the model.
    /// </summary>
    /// <param name="heldVariables">
    /// The variables held by the session, framework-ephemeral keys already excluded.
    /// </param>
    /// <returns>
    /// The declaration to inject, or <c>null</c> when nothing should be injected — either because
    /// the session holds no variables or because the prompt layer declares no such template.
    /// </returns>
    Task<string?> ComposeHeldContextDeclarationAsync(IReadOnlyDictionary<string, object> heldVariables);
}