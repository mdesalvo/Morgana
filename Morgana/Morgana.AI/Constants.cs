namespace Morgana.AI;

/// <summary>
/// The framework's glossary: every literal that is a CONTRACT between two parties rather than a
/// value belonging to one of them. Peer of <see cref="Records"/> — that one centralizes the model,
/// this one the vocabulary the model is spoken in.
/// </summary>
public static class Constants
{
    /// <summary>
    /// The name of the system, on which everything else rests...
    /// </summary>
    public const string Morgana = "Morgana";

    /// <summary>
    /// IDs of the framework prompts in <c>morgana.json</c>, resolved through
    /// <c>IPromptResolverService</c>. They name framework actors rather than domain concepts, which
    /// is why a domain intent never collides with one. The framework layer's own prompt is not here:
    /// it is filed under the system's name itself, <see cref="Morgana"/>.
    /// </summary>
    public static class Prompts
    {
        /// <summary>The intent classifier's prompt.</summary>
        public const string Classifier = "Classifier";

        /// <summary>The guard rail's compliance-check prompt.</summary>
        public const string Guard = "Guard";

        /// <summary>The welcome message and its quick replies.</summary>
        public const string Presentation = "Presentation";

        /// <summary>The rewrite prompt that degrades rich output for a limited channel.</summary>
        public const string ChannelAdapter = "ChannelAdapter";
    }

    /// <summary>
    /// Name prefixes of the pipeline actors. An actor's path is <c>/user/{prefix}-{conversationId}</c>,
    /// built by <c>ActorSystemExtensions.GetOrCreateActorAsync</c>: the prefix is what makes a path
    /// predictable, so an actor is reached by name from a later turn — or from a controller that
    /// holds nothing but the conversation id — instead of a reference having to be kept alive.
    /// </summary>
    public static class Actors
    {
        /// <summary>Entry point of a conversation: lifecycle, channel metadata, the supervisor it owns.</summary>
        public const string Manager = "manager";

        /// <summary>The FSM orchestrating one turn through guard, classifier and router.</summary>
        public const string Supervisor = "supervisor";

        /// <summary>Content moderation, in front of every turn.</summary>
        public const string Guard = "guard";

        /// <summary>Intent classification, skipped whenever an agent is already active.</summary>
        public const string Classifier = "classifier";

        /// <summary>Intent-to-agent dispatch.</summary>
        public const string Router = "router";
    }

    /// <summary>
    /// Names of the global policies that code resolves by name. The rest of the list lives in
    /// <c>morgana.json</c> and nowhere else: a policy the framework only renders is read by the model
    /// and by whoever edits the prompt, and naming it here would be an index that no rename breaks.
    /// </summary>
    public static class Policies
    {
        /// <summary>
        /// P8 — the two-role contract of a consultation, and the one policy rendered conditionally:
        /// only an agent inside the A2A topology reads it (see <c>ComposeAgentInstructionsAsync</c>),
        /// so this name is resolved on every composition rather than only edited in the prompt.
        /// </summary>
        public const string PeerConsultation = "PeerConsultation";
    }

    /// <summary>
    /// Names of the entries carrying <c>Type: "Injection"</c> in the same array as the policies.
    /// They are templates, not rules: each is spliced at exactly one site instead of being rendered
    /// among the policies, and each is resolved by name through <c>GlobalPolicy.ResolveTemplate</c>.
    /// </summary>
    public static class Injections
    {
        /// <summary>Appended to the description of a tool declaring context-scoped parameters.</summary>
        public const string ToolDescriptionContextGuidance = "ToolDescriptionContextGuidance";

        /// <summary>Injected per turn, naming the context variables the session currently holds.</summary>
        public const string HeldContextDeclaration = "HeldContextDeclaration";

        /// <summary>Placed in front of a colleague's question, telling the answering agent who its reader is.</summary>
        public const string PeerConsultationDeclaration = "PeerConsultationDeclaration";

        /// <summary>Spliced into a peer-capable agent's own instructions, naming the colleagues it holds.</summary>
        public const string ColleaguesDeclaration = "ColleaguesDeclaration";
    }

    /// <summary>
    /// The base tools whose names travel further than the tool loop: a stored function call is
    /// recognised by name when a conversation's history is replayed, long after the agent that made
    /// it is gone.
    /// </summary>
    public static class Tools
    {
        /// <summary>Declares out-of-band that the agent awaits the user's next turn.</summary>
        public const string SetTurnContinuation = "SetTurnContinuation";

        /// <summary>Attaches the turn's quick replies.</summary>
        public const string SetQuickReplies = "SetQuickReplies";

        /// <summary>Attaches the turn's rich card.</summary>
        public const string SetRichCard = "SetRichCard";
    }

    /// <summary>
    /// The framework's own context keys: ephemeral, one turn long, and excluded from the held-context
    /// declaration precisely because they are machinery rather than knowledge about the user. Written
    /// by a base tool or by the consultation guards, read and dropped by the agent at end of turn.
    /// </summary>
    public static class ContextKeys
    {
        /// <summary>Set by <see cref="Tools.SetTurnContinuation"/>; read once, then dropped.</summary>
        public const string TurnContinuation = "turn_continuation";

        /// <summary>Set by <see cref="Tools.SetQuickReplies"/>; read once, then dropped.</summary>
        public const string QuickReplies = "quick_replies";

        /// <summary>Set by <see cref="Tools.SetRichCard"/>; read once, then dropped.</summary>
        public const string RichCard = "rich_card";

        /// <summary>Marks the turn as serving a colleague, which is what refuses a second hop.</summary>
        public const string ServingConsultation = "peer_consultation";

        /// <summary>Counts the consultations spent on one user turn, against the configured cap.</summary>
        public const string ConsultationRounds = "peer_consultation_rounds";
    }

    /// <summary>
    /// Keys stamped on a <c>ChatMessage</c>'s AdditionalProperties. They are the only way a later
    /// reader — the persistence layer rebuilding a transcript, the reducer resuming a summarized
    /// session, the answering side of a consultation — can tell what a stored message was FOR.
    /// </summary>
    public static class MessageProperties
    {
        /// <summary>
        /// Written by <c>MorganaAgent</c> at end-of-turn to mark the LAST assistant message of that
        /// turn as the user-facing one. Read by
        /// <c>SQLiteConversationPersistenceService.GetConversationHistoryAsync</c> to filter out the
        /// intermediate tool-calling assistant messages when history is rendered on resume.
        /// </summary>
        public const string UserFacing = "morgana:user_facing";

        /// <summary>
        /// Written alongside <see cref="UserFacing"/>, carrying the turn's reply exactly as it was
        /// delivered to the channel, and read back by
        /// <c>SQLiteConversationPersistenceService.ExtractTextFromMessage</c>.
        /// </summary>
        public const string TurnText = "morgana:turn_text";

        /// <summary>A2A message metadata naming the agent that asked. Dotted, not colon-separated, because it travels the protocol.</summary>
        public const string CallerIntent = "morgana:caller";

        /// <summary>The running summary a reducer stores on its anchor message. MEAI's own name, kept so sessions summarized before <c>MorganaChatReducer</c> shipped still resume.</summary>
        public const string Summary = "__summary__";
    }

    /// <summary>
    /// Placeholders authored inside prompt prose and resolved in code. Double parentheses because no
    /// natural sentence contains them, and a prompt is edited by people who are not reading this file.
    /// </summary>
    public static class Placeholders
    {
        /// <summary>In <see cref="Injections.ToolDescriptionContextGuidance"/> — the tool's own context-scoped parameter names.</summary>
        public const string ContextParameters = "((context_parameters))";

        /// <summary>In <see cref="Injections.HeldContextDeclaration"/> — the held variables as name: value pairs.</summary>
        public const string HeldVariables = "((held_variables))";

        /// <summary>In <see cref="Injections.PeerConsultationDeclaration"/> — the intent of the agent asking.</summary>
        public const string ConsultationCaller = "((caller))";

        /// <summary>In <see cref="Injections.ColleaguesDeclaration"/> — one line per colleague: function name and territory.</summary>
        public const string Colleagues = "((colleagues))";

        /// <summary>In the <see cref="Prompts.Classifier"/> prompt — the configured intents, formatted for ranking.</summary>
        public const string FormattedIntents = "((formattedIntents))";

        /// <summary>In the <see cref="Prompts.Presentation"/> prompt — the intents offered as opening quick replies.</summary>
        public const string Intents = "((intents))";

        /// <summary>In the <see cref="Prompts.ChannelAdapter"/> prompt — the target channel's capability budget, as JSON.</summary>
        public const string ChannelCapabilities = "((channel_capabilities))";
    }

    /// <summary>
    /// Values of <c>Records.ToolParameter.Scope</c>: where a tool's input comes FROM. A parameter
    /// carrying a value the model itself authors declares neither.
    /// </summary>
    public static class Scopes
    {
        /// <summary>Resolved from the session's context variables before the user is ever asked.</summary>
        public const string Context = "context";

        /// <summary>Obtained from the user, on the turn it is needed.</summary>
        public const string Request = "request";
    }

    /// <summary>
    /// Intent names the framework itself knows. Every other intent is a domain's own.
    /// </summary>
    public static class Intents
    {
        /// <summary>
        /// The classifier's fallback: no agent handles it, it is never offered as a quick reply and
        /// never counts as a collision candidate. Routed all the same, so the router answers with
        /// its unrecognized-intent message rather than the pipeline stalling.
        /// </summary>
        public const string Other = "other";
    }

    /// <summary>
    /// The agent-to-agent surface: how a colleague is named to a model, where its card lives, and
    /// under whose name this installation signs its own peer traffic.
    /// </summary>
    public static class AgentToAgent
    {
        /// <summary>Prefix of the function a colleague is offered under, and the marker by which a consultation is recognised in an agent's own history.</summary>
        public const string PeerFunctionNamePrefix = "consult_";

        /// <summary>Root of the published A2A routes: <c>/a2a/{intent}</c>, with the card one level below it.</summary>
        public const string AgentPathPrefix = "/a2a";

        /// <summary>Issuer name Morgana signs its own peer requests under. The only issuer the A2A endpoints admit; must be declared in <c>Morgana:Authentication:Issuers</c> like any channel.</summary>
        public const string IssuerName = "morgana";
    }

    /// <summary>
    /// Log lines somebody OUTSIDE the process reads. Ordinary logging is prose for an operator and
    /// belongs nowhere near this file; these three lines are different — they are the only place a
    /// context variable's NAME becomes observable, and the PromptHarness parses them to assert the
    /// lookup-before-asking cycle, which no span attribute carries (a name is data, and spans carry
    /// none). That makes their shape a contract with a reader that cannot be recompiled with them.
    /// </summary>
    public static class ObservableLogs
    {
        /// <summary>Emitter of the context-access lines, as it names itself in them.</summary>
        public const string ToolName = nameof(Abstractions.MorganaTool);

        /// <summary>Emitter of the declaration line, as it names itself in it.</summary>
        public const string ContextProviderName = nameof(Providers.MorganaAIContextProvider);

        /// <summary>The variable was already held: the lookup answered and the user was not asked.</summary>
        public const string Hit = "HIT";

        /// <summary>The variable was not held: asking the user is legitimate from here on.</summary>
        public const string Miss = "MISS";

        /// <summary>The variable was written, whoever the value came from.</summary>
        public const string Set = "SET";

        /// <summary>
        /// The stable head of all three context-access lines. Everything the harness needs is in it —
        /// the emitter, the operation and the variable name — so the per-operation tails below may
        /// change without moving the reader.
        /// </summary>
        public const string ContextAccessHead = "{MorganaToolName} ({Name}) {Operation} variable '{VariableName}'";

        /// <summary>Read of a held variable, with the value it answered.</summary>
        public const string ContextHit = ContextAccessHead + " from agent context. Value is: {Value}";

        /// <summary>Read of a variable the session does not hold.</summary>
        public const string ContextMiss = ContextAccessHead + " from agent context.";

        /// <summary>Write of a variable, with the value stored.</summary>
        public const string ContextSet = ContextAccessHead + " into agent context. Value is: {Value}";

        /// <summary>
        /// The per-turn declaration: the variables handed to the model outright, which is why an agent
        /// may legitimately use one without ever calling GetContextVariable. Names several at once.
        /// </summary>
        public const string DeclaredContext = "{MorganaAiContextProviderName} DECLARED '{VariableNames}'";
    }

    /// <summary>
    /// Sentinel values <c>appsettings.json</c> ships in place of a setting that MUST be filled in
    /// before the application is usable — a secret through User Secrets or the environment, a
    /// non-secret required value through either. A setting still holding one has not been
    /// configured, and every reader treats it as absent rather than as a value.
    /// </summary>
    public static class Overrides
    {
        /// <summary>Stands in for a secret: an API key, a signing key.</summary>
        public const string Secure = "_SECURE_OVERRIDE_";

        /// <summary>Stands in for a required non-secret: a model id, an endpoint.</summary>
        public const string Functional = "_FUNCTIONAL_OVERRIDE_";

        /// <summary>Both, for a reader that only asks whether a setting was ever filled in.</summary>
        public static readonly string[] All = [Secure, Functional];
    }

    /// <summary>
    /// Characters that join or separate two pieces of composed text. Never prose: each one is
    /// structure, and says nothing of its own to whoever reads the text it punctuates.
    /// </summary>
    public static class Markers
    {
        /// <summary>
        /// Separates the stable framework+domain prompt from the per-turn dynamic tail, so
        /// <c>MorganaAnthropicClient</c> can cache the former without a changing declaration busting
        /// it. The actual ASCII Record Separator (U+001E), not its printable glyph: never typed by a
        /// human and never produced by a model, so it cannot collide with real content.
        /// </summary>
        public const string DynamicInstructions = "\u001E";

        /// <summary>
        /// Inserted between the text of two consecutive assistant messages of the same turn, both
        /// while streaming and when batching, so two messages meant to be read apart do not weld
        /// into one paragraph.
        /// </summary>
        public const string MessageSeparator = "\n\n";
    }
}