namespace Morgana.AI.Interfaces;

/// <summary>
/// Assembles every piece of framework text destined for a domain agent's model: the composed system
/// prompt (framework layer + domain layer, fenced), the colleagues it may consult and the question a
/// colleague puts to it. Sibling of <see cref="IPromptResolverService"/> — that one abstracts
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
    /// them the model sees each label twice with nothing marking which is which and the
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
    /// Produces the description under which a colleague is offered as a callable function: the
    /// colleague's own statement of what falls to it (its <c>ConsultMeFor</c>, carried on the card as
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
    /// Produces the whole of what an answering agent reads on a turn serving a colleague: the note
    /// telling it its reader is not the user, then the question inside the fence marking it as data.
    /// </summary>
    /// <param name="callerIntent">Intent of the asking agent; <c>null</c> when the caller named none,
    /// which the composer renders as an unnamed caller rather than as a name.</param>
    /// <param name="question">What the colleague asked, in its own words.</param>
    /// <returns>The composed turn, falling back to the bare question where no template is declared.</returns>
    Task<string> ComposeConsultationRequestAsync(string? callerIntent, string question);
}
