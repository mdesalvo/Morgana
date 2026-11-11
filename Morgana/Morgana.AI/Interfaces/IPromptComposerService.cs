namespace Morgana.AI.Interfaces;

/// <summary>Assembles every text that a domain agent's model reads, from its fenced two-layer prompt to the per-turn declarations.</summary>
// Sibling of IPromptResolverService: that one hides where prompts come from, this one how they become what the model reads.
public interface IPromptComposerService
{
    /// <summary>
    /// The agent's full system prompt: the framework layer then the domain layer, each inside its own fence.
    /// The fences are load-bearing: both layers carry the same section labels, so without them the model reads each label
    /// twice with nothing saying which is which and the framework's precedence names a boundary the model cannot find.
    /// </summary>
    /// <param name="domainPrompt">The agent's own prompt from agents.json.</param>
    /// <param name="peerCapable">True when the agent consults a colleague or is consulted, which admits the PeerConsultation policy among the rules.</param>
    /// <returns>The composed instructions, ready for <c>ChatOptions.Instructions</c>.</returns>
    Task<string> ComposeAgentInstructionsAsync(Records.Prompt domainPrompt, bool peerCapable = false);

    /// <summary>The description that a tool presents to the model, closed by <c>ToolDescriptionContextGuidance</c> when the tool takes context parameters.</summary>
    /// <param name="toolDefinition">The tool's definition as its class declares it.</param>
    Task<string> ComposeToolDescriptionAsync(Records.ToolDefinition toolDefinition);

    /// <summary>The description under which a colleague is offered as a function: its own <c>Territory</c> as its card carries it, with nothing added.</summary>
    Task<string> ComposePeerDescriptionAsync(A2A.AgentCard peerCard);

    /// <summary>
    /// The block naming the colleagues that an agent holds, which closes its instructions; null when it has none or no template exists.
    /// One rung above the colleague's own description and the rung that decides whether that one is ever read: a function's
    /// description reaches the model only once it is weighing the function, which an agent about to answer "this is not on
    /// my books" never does. Fixed for the agent's life, so it rides in the cached prefix instead of being sent every turn.
    /// </summary>
    /// <param name="colleagues">Each colleague's function name and its own statement of what falls to it.</param>
    Task<string?> ComposeColleaguesDeclarationAsync(IReadOnlyDictionary<string, string> colleagues);

    /// <summary>What an agent serving a colleague reads on that turn: the note that its reader is not the user, then the question fenced as data.</summary>
    /// <param name="callerIntent">The asking agent's intent; null when the caller named none, rendered as an unnamed caller.</param>
    /// <param name="question">What the colleague asked, in its own words.</param>
    /// <returns>The composed turn; the bare question where no template is declared.</returns>
    Task<string> ComposeConsultationRequestAsync(string? callerIntent, string question);

    /// <summary>
    /// The per-turn declaration of the context variables that the session holds, names and values; null when it holds none or no template exists.
    /// The one composed fragment carrying a fact rather than a rule: a tool description is built once and can state only the
    /// contract, never that customerCode is T780C right now. Handing over the value turns recall into a fact already in view.
    /// </summary>
    /// <param name="heldVariables">The variables the session holds, without the framework's ephemeral keys.</param>
    Task<string?> ComposeHeldContextDeclarationAsync(IReadOnlyDictionary<string, object> heldVariables);

    /// <summary>The declaration added on the turn on which the user chose the agent by pressing Morgana's button; null when no template exists.</summary>
    Task<string?> ComposeLandingDeclarationAsync();

    /// <summary>
    /// What the model reads in place of a tool result that the guard withheld; empty when no template exists.
    /// The result is withheld either way: an empty text only means nothing is said in its place.
    /// </summary>
    /// <param name="toolName">The function the model called.</param>
    Task<string> ComposeQuarantinedResultAsync(string toolName);
}