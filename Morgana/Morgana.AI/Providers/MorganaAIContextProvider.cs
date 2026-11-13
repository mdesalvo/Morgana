using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Providers;

/// <summary>
/// Holds one agent's context variables in its session and declares the held ones to the model on every turn.
/// A write to a shared variable is also handed to <see cref="OnSharedContextUpdate"/>, which files it in the conversation's <c>shared_context</c>.
/// </summary>
public class MorganaAIContextProvider : AIContextProvider
{
    /// <summary>
    /// The framework's own keys, written into the same variables to carry a turn's presentation and consultation bookkeeping.
    /// They are never declared to the model: naming a stale one would invite the next turn to re-read a card already rendered.
    /// </summary>
    private static readonly ImmutableHashSet<string> EphemeralVariableNames =
        [
            Constants.ContextKeys.TurnEnding,
            Constants.ContextKeys.QuickReplies,
            Constants.ContextKeys.RichCard,
            Constants.ContextKeys.ServingConsultation,
            Constants.ContextKeys.ConsultationRounds,
            Constants.ContextKeys.Landing,
            Constants.ContextKeys.QuarantinedTools
        ];

    /// <summary>Writes the variable access lines.</summary>
    private readonly ILogger logger;

    /// <summary>Composes the per-turn held-context declaration and the landing declaration from the framework's injections.</summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>The variables that a tool parameter declares as shared, whose writes reach the conversation's <c>shared_context</c>.</summary>
    private readonly ImmutableHashSet<string> sharedVariableNames;

    /// <summary>Keeps the <see cref="MorganaContextState"/> inside each session, so it is saved and restored with it.</summary>
    private readonly ProviderSessionState<MorganaContextState> sessionState;

    /// <summary>
    /// Files a shared variable's value in the conversation's <c>shared_context</c>, where every agent reads it at its next turn.
    /// The writing tool call awaits it, so the value is on record before the turn issues its next tool call.
    /// </summary>
    public Func<string, object, Task>? OnSharedContextUpdate { get; set; }

    /// <summary>The key this provider's state is kept under in the session.</summary>
    public override IReadOnlyList<string> StateKeys => [ nameof(MorganaAIContextProvider) ];

    /// <summary>Binds the provider to its composer and to the variables that the agent's tools declare as shared.</summary>
    /// <param name="logger">Writes the variable access lines.</param>
    /// <param name="promptComposerService">Composes the per-turn declarations.</param>
    /// <param name="sharedVariableNames">The context parameters that a tool declares as shared; none when omitted.</param>
    public MorganaAIContextProvider(
        ILogger logger,
        IPromptComposerService promptComposerService,
        IEnumerable<string>? sharedVariableNames = null)
    {
        this.logger = logger;
        this.promptComposerService = promptComposerService;
        this.sharedVariableNames = [.. sharedVariableNames ?? []];

        // The state lives in the session under this provider's key, serialized as Microsoft.Agents.AI serializes the rest of the session.
        sessionState = new ProviderSessionState<MorganaContextState>(
            stateInitializer: _ => new MorganaContextState(),
            stateKey: StateKeys[0],
            jsonSerializerOptions: AgentAbstractionsJsonUtilities.DefaultOptions);
    }

    // =========================================================================
    // Agent Context
    // =========================================================================

    /// <summary>Reads a variable from the session's context; <c>null</c> when it is not held.</summary>
    public object? GetVariable(AgentSession session, string variableName)
    {
        // A session read for the first time starts with no variables.
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);

        // The tool tells a hit from a miss by a value coming back.
        if (contextState.Variables.TryGetValue(variableName, out object? value))
        {
            logger.LogInformation("{MorganaAiContextProviderName} GET '{VariableName}' = '{Value}'", nameof(MorganaAIContextProvider), variableName, value);
            return value;
        }

        logger.LogInformation("{MorganaAiContextProviderName} MISS '{VariableName}'", nameof(MorganaAIContextProvider), variableName);

        // The miss the tool turns into the line telling the model the variable is not in context.
        return null;
    }

    /// <summary>
    /// Reads the value of one of the framework's own context keys; <c>null</c> when the session does not hold it.
    /// It writes no access line, since the agent and the consultation guards ask it on every turn.
    /// </summary>
    public object? GetFrameworkKeyValue(AgentSession session, string frameworkKey)
        => sessionState.GetOrInitializeState(session).Variables.GetValueOrDefault(frameworkKey);

    /// <summary>Writes a variable to the session's context, handing a shared one to <see cref="OnSharedContextUpdate"/> as well.</summary>
    public async Task SetVariableAsync(AgentSession session, string variableName, object variableValue)
    {
        // The value replaces any earlier one in this agent's own context and the session keeps it.
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);
        contextState.Variables[variableName] = variableValue;
        sessionState.SaveState(session, contextState);

        // Only a variable that a tool parameter declares as shared travels to the other agents.
        bool isShared = sharedVariableNames.Contains(variableName);

        logger.LogInformation(
            "{MorganaAiContextProviderName} SET {Private} '{VariableName}' = '{VariableValue}'", nameof(MorganaAIContextProvider), isShared ? "SHARED" : "PRIVATE", variableName, variableValue);

        // A shared write is filed in the registry before the tool call returns.
        if (isShared && OnSharedContextUpdate is not null)
            await OnSharedContextUpdate(variableName, variableValue);
    }

    /// <summary>Removes a variable from the session's context, as the agent drops the turn's ephemeral keys once it has read them.</summary>
    public void DropVariable(AgentSession session, string variableName)
    {
        // The ephemeral keys live among the variables, so dropping one removes it from the same store.
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);

        // The session is saved only when there was something to remove.
        if (contextState.Variables.Remove(variableName))
        {
            sessionState.SaveState(session, contextState);
            logger.LogInformation("{MorganaAiContextProviderName} DROPPED '{VariableName}'", nameof(MorganaAIContextProvider), variableName);
        }
    }

    /// <summary>Merges the conversation's shared variables into the session's context, first write winning: a variable already held keeps its value.</summary>
    public void MergeSharedContext(AgentSession session, Dictionary<string, object> sharedContext)
    {
        // The variables this agent already holds, which the registry never overwrites.
        MorganaContextState contextState = sessionState.GetOrInitializeState(session);

        // Whether any variable was taken, so the session is saved only when it changed.
        bool changed = false;

        // Each shared variable is taken only where this agent does not hold it yet.
        foreach (KeyValuePair<string, object> sharedVariable in sharedContext)
        {
            // Absent here, the registry's value is taken: this is how a customerCode given to Billing reaches Contract.
            if (!contextState.Variables.TryGetValue(sharedVariable.Key, out object? existing))
            {
                contextState.Variables[sharedVariable.Key] = sharedVariable.Value;
                changed = true;

                logger.LogInformation(
                    "{MorganaAiContextProviderName} MERGED shared context '{KvpKey}' = '{KvpValue}'", nameof(MorganaAIContextProvider), sharedVariable.Key, sharedVariable.Value);
            }
            else
            {
                // A variable the agent already holds keeps its own value.
                logger.LogInformation(
                    "{MorganaAiContextProviderName} IGNORED shared context '{KvpKey}' (already set to '{Existing}')", nameof(MorganaAIContextProvider), sharedVariable.Key, existing);
            }
        }

        // Saved only when something was taken, since every turn merges and most merge nothing.
        if (changed)
            sessionState.SaveState(session, contextState);
    }

    // =========================================================================
    // AIContextProvider overrides
    // =========================================================================

    /// <summary>
    /// Hands the model, for this run alone, the variables the session holds and, on a landing turn, the landing declaration.
    /// An agent activated mid-conversation has an empty history, so a variable hydrated from the registry would otherwise be invisible to it.
    /// </summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Read on every run, after the turn's merge of the shared registry, so the declaration names what was just hydrated.
        MorganaContextState contextState = sessionState.GetOrInitializeState(context.Session);

        // The landing declaration, only on the turn the user has just chosen this agent.
        string? landingDeclaration = contextState.Variables.ContainsKey(Constants.ContextKeys.Landing)
            ? await promptComposerService.ComposeLandingDeclarationAsync()
            : null;

        // The framework's own keys are machinery, never something the model holds. Ordinal order keeps the
        // declaration byte-identical across turns while the held set does not change.
        SortedDictionary<string, object> heldVariables = new SortedDictionary<string, object>(
            contextState.Variables
                .Where(variable => !EphemeralVariableNames.Contains(variable.Key))
                .ToDictionary(variable => variable.Key, variable => variable.Value),
            StringComparer.Ordinal);

        // Null when nothing is held or the framework declares no template.
        string? declaration = await promptComposerService.ComposeHeldContextDeclarationAsync(heldVariables);

        // Neither a held value nor a landing: the agent's own instructions are all the model reads.
        if (declaration is null && landingDeclaration is null)
            return new AIContext();

        // The declared names are logged where the PromptHarness reads them.
        if (declaration is not null)
            logger.LogInformation(
                Constants.ObservableLogs.DeclaredContext,
                Constants.ObservableLogs.ContextProviderName, string.Join(", ", heldVariables.Keys));

        // Both ride in the per-turn tail, which opens with the one marker the cache split looks for: the held-context
        // declaration carries it and a landing without held values takes it in front of its own text.
        string perTurnInstructions = (declaration, landingDeclaration) switch
        {
            // Values held and a landing: the declaration's marker already opens the tail.
            ({ } heldDeclaration, { } landing) => $"{heldDeclaration}\n\n{landing}",
            // Values held on an ordinary turn.
            ({ } heldDeclaration, null) => heldDeclaration,
            // A landing on a session that holds nothing yet.
            _ => Constants.Markers.DynamicInstructions + landingDeclaration
        };

        // Instructions added to this run only, never to the session's history.
        return new AIContext { Instructions = perTurnInstructions };
    }

    /// <summary>One session's context variables, saved and restored with the session.</summary>
    public sealed class MorganaContextState
    {
        /// <summary>The session's variables by name, the framework's own keys among them.</summary>
        [JsonPropertyName("variables")]
        public Dictionary<string, object> Variables { get; set; } = [];
    }
}