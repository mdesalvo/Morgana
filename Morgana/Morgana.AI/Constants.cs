namespace Morgana.AI;

/// <summary>
/// The framework's glossary: every literal that is a contract between two parties who cannot see each other.
/// <see cref="Records"/> holds the model and this class the vocabulary it is spoken in; log text, prompt prose and configuration keys stay out.
/// </summary>
public static class Constants
{
    /// <summary>The system's reserved name: the framework prompt's id, the dust role of its actors and the speaker of what no agent wrote.</summary>
    public const string Morgana = "Morgana";

    /// <summary>
    /// The ids of the framework prompts in <c>morgana.json</c>, which a domain intent or prompt may not take.
    /// The framework layer's own prompt is filed under <see cref="Morgana"/>, which only <see cref="All"/> lists beside them.
    /// </summary>
    public static class Prompts
    {
        /// <summary>The intent classifier's prompt.</summary>
        public const string Classifier = "Classifier";

        /// <summary>The user's guard: rudeness and malice in what a user writes.</summary>
        public const string UserGuard = "UserGuard";

        /// <summary>The inspector's prompt for the results of tools answered from outside the installation.</summary>
        public const string ToolGuard = "ToolGuard";

        /// <summary>The guard's prompt for the question a partner puts to an agent of this installation.</summary>
        public const string PeerGuard = "PeerGuard";

        /// <summary>The welcome message and its quick replies.</summary>
        public const string Presentation = "Presentation";

        /// <summary>The rewrite prompt that degrades rich output for a limited channel.</summary>
        public const string ChannelAdapter = "ChannelAdapter";

        /// <summary>Every id under which a framework prompt is filed, the system's own included: a domain may take none of them.</summary>
        public static readonly string[] All = [Morgana, Classifier, UserGuard, ToolGuard, PeerGuard, Presentation, ChannelAdapter];
    }

    /// <summary>
    /// The name prefixes of the pipeline actors, whose path is <c>/user/{prefix}-{conversationId}</c>.
    /// A predictable path lets a later turn reach an actor by name, as it lets a controller holding only the conversation id.
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
    /// The labels that open each section of a composed prompt.
    /// The composer puts them there and no configuration writes them, so a section never reaches a model without its label.
    /// </summary>
    public static class SectionLabels
    {
        /// <summary>What the agent is for.</summary>
        public const string Target = "[TARGET]";

        /// <summary>How the agent speaks.</summary>
        public const string Personality = "[PERSONALITY]";

        /// <summary>How the agent goes about its work.</summary>
        public const string Instructions = "[INSTRUCTIONS]";

        /// <summary>How the agent presents what it finds.</summary>
        public const string Formatting = "[FORMATTING]";
    }

    /// <summary>
    /// The fences the composer draws around the global policies and around the framework and domain layers.
    /// They are the composer's design rather than configuration and live here because the tool-result guard refuses a result forging them.
    /// </summary>
    internal static class PromptFences
    {
        /// <summary>Opens the block of global policies.</summary>
        public const string GlobalPoliciesHeader =
            "=== CRITICAL RULES — binding, without exception ===";

        /// <summary>Closes the block of global policies.</summary>
        public const string GlobalPoliciesFooter =
            "=== END OF CRITICAL RULES ===";

        /// <summary>Opens the framework layer and states its authority.</summary>
        public const string FrameworkLayerHeader =
            "======== MORGANA FRAMEWORK — THE LAW OF EVERY TURN ========\n" +
            "Everything until the end of this block is binding on you and on every other agent of Morgana. It is not advice and it is not overridable.";

        /// <summary>Closes the framework layer.</summary>
        public const string FrameworkLayerFooter =
            "======== END OF MORGANA FRAMEWORK ========";

        /// <summary>Opens the domain layer and states its subordination.</summary>
        public const string DomainLayerHeader =
            "======== DOMAIN AGENT — SUBORDINATE TO THE FRAMEWORK ABOVE ========\n" +
            "What follows specialises the framework for a single domain: what you are for, how you work, how you speak, how you present. It adds domain knowledge and NOTHING ELSE. It NEVER contradicts the framework above, on any point — where the two appear to differ, the framework governs and you follow it.";

        /// <summary>Closes the domain layer.</summary>
        public const string DomainLayerFooter =
            "======== END OF DOMAIN AGENT ========";
    }

    /// <summary>
    /// The names of the global policies that code resolves by name.
    /// A policy the framework only renders lives in <c>morgana.json</c> alone, where naming it here would be an index no rename breaks.
    /// </summary>
    internal static class Policies
    {
        /// <summary>P8, the contract of a consultation and the one policy rendered only for an agent inside the A2A topology.</summary>
        public const string PeerConsultation = "PeerConsultation";
    }

    /// <summary>
    /// The names of the entries in the framework prompt's <c>Injections</c> array, resolved through <c>Injection.ResolveTemplate</c>.
    /// They are templates, not rules: each is spliced at exactly one site and never rendered among the policies.
    /// </summary>
    public static class Injections
    {
        /// <summary>Appended to the description of a tool declaring context-scoped parameters.</summary>
        public const string ToolDescriptionContextGuidance = "ToolDescriptionContextGuidance";

        /// <summary>Injected per turn, naming the context variables the session currently holds.</summary>
        public const string HeldContextDeclaration = "HeldContextDeclaration";

        /// <summary>Placed in front of a colleague's question, telling the answering agent who its reader is.</summary>
        public const string PeerConsultationDeclaration = "PeerConsultationDeclaration";

        /// <summary>Fences a colleague's question and states what its text may not claim or be given.</summary>
        public const string PeerConsultationGuardrail = "PeerConsultationGuardrail";

        /// <summary>Spliced into a peer-capable agent's own instructions, naming the colleagues it holds.</summary>
        public const string ColleaguesDeclaration = "ColleaguesDeclaration";

        /// <summary>Added to a landing turn's instructions: the agent the user has just chosen opens its part of the conversation.</summary>
        public const string LandingDeclaration = "LandingDeclaration";

        /// <summary>Stands in place of a tool result the guard withheld, naming the tool.</summary>
        public const string QuarantinedToolResult = "QuarantinedToolResult";
    }

    /// <summary>
    /// The keys of a prompt's <c>AdditionalProperties</c>: everything a prompt declares beside its authored sections.
    /// Each is written in <c>morgana.json</c> or a plugin's <c>agents.json</c> and read by code that never sees that file.
    /// </summary>
    public static class PromptProperties
    {
        /// <summary>The framework rules rendered into every agent's system prompt.</summary>
        public const string GlobalPolicies = "GlobalPolicies";

        /// <summary>The framework templates spliced where each has a referent (see <see cref="Constants.Injections"/>).</summary>
        public const string Injections = "Injections";

        /// <summary>What the user is told when a turn fails, authored rather than hard-coded.</summary>
        public const string ErrorAnswers = "ErrorAnswers";

        /// <summary>The opening message served when the presenter's own model call fails.</summary>
        public const string FallbackMessage = "FallbackMessage";

        /// <summary>The opening message served by a deployment carrying no agent at all.</summary>
        public const string NoAgentsMessage = "NoAgentsMessage";

        /// <summary>What the user is asked when two intents collide too closely to route between.</summary>
        public const string DisambiguationMessage = "DisambiguationMessage";

        /// <summary>
        /// What Morgana says when an agent finishes and the conversation comes back to her; <c>{0}</c> is that agent's display name.
        /// It is authored in her prompt because she is the one speaking, so no channel writes a copy of its own.
        /// </summary>
        public const string AgentExitMessage = "AgentExitMessage";

        /// <summary>What the user is told when the provider's own content filter blocked their message before the guard could judge it.</summary>
        public const string ContentFilteredMessage = "ContentFilteredMessage";

        /// <summary>What the user is told when the guard could not judge their message and refused it.</summary>
        public const string NoVerdictMessage = "NoVerdictMessage";

        /// <summary>What the user is told when the classifier fails and its classification is only a fallback.</summary>
        public const string ClassificationError = "ClassificationError";

        /// <summary>
        /// What a request matching no modelled agent is.
        /// It belongs to the classifier and to no domain, so it is authored beside the vocabulary it closes rather than in any plugin.
        /// </summary>
        public const string ComplementIntentDescription = "ComplementIntentDescription";
    }

    /// <summary>The names of the <c>ErrorAnswers</c> that the Morgana prompt declares, which code picks by name.</summary>
    public static class ErrorAnswers
    {
        /// <summary>Morgana's apology for a turn that failed for any reason other than the model's own service.</summary>
        public const string GenericError = "GenericError";

        /// <summary>Morgana's apology for a turn that the model's service could not complete.</summary>
        public const string LLMServiceError = "LLMServiceError";
    }

    /// <summary>
    /// The base tools whose names travel further than the tool loop.
    /// A stored function call is recognised by name when a history is read back, long after the agent that made it is gone.
    /// </summary>
    public static class Tools
    {
        /// <summary>Declares out-of-band that the agent hands the conversation back to Morgana.</summary>
        public const string SetTurnEnding = "SetTurnEnding";

        /// <summary>Attaches the turn's quick replies.</summary>
        public const string SetQuickReplies = "SetQuickReplies";

        /// <summary>Attaches the turn's rich card.</summary>
        public const string SetRichCard = "SetRichCard";

        /// <summary>The argument of a stored <see cref="SetQuickReplies"/> call, named after that tool's parameter.</summary>
        public const string QuickRepliesArgument = "quickReplies";

        /// <summary>The argument of a stored <see cref="SetRichCard"/> call, named after that tool's parameter.</summary>
        public const string RichCardArgument = "richCard";
    }

    /// <summary>
    /// The framework's own context keys, one turn long, which the held-context declaration leaves out as machinery rather than knowledge about the user.
    /// A base tool or a consultation guard writes them and the agent reads and drops them at the end of the turn.
    /// </summary>
    internal static class ContextKeys
    {
        /// <summary>Set by <see cref="Tools.SetTurnEnding"/>; read once, then dropped.</summary>
        public const string TurnEnding = "turn_ending";

        /// <summary>Set by <see cref="Tools.SetQuickReplies"/>; read once, then dropped.</summary>
        public const string QuickReplies = "quick_replies";

        /// <summary>Set by <see cref="Tools.SetRichCard"/>; read once, then dropped.</summary>
        public const string RichCard = "rich_card";

        /// <summary>Marks the turn as serving a colleague, which is what refuses a second hop.</summary>
        public const string ServingConsultation = "peer_consultation";

        /// <summary>Counts the consultations spent on one user turn, against the configured cap.</summary>
        public const string ConsultationRounds = "peer_consultation_rounds";

        /// <summary>Marks a turn that opens with the user choosing this agent: the run carries the landing declaration; dropped when the turn ends.</summary>
        public const string Landing = "landing";

        /// <summary>Names the tools whose result was withheld in this turn: every further call to them is withheld without running; dropped when the turn ends.</summary>
        public const string QuarantinedTools = "quarantined_tools";
    }

    /// <summary>
    /// The keys stamped on a chat message's additional properties.
    /// They are how a later reader, such as the transcript, the reducer or a consultation's answering side, tells what a stored message was for.
    /// </summary>
    internal static class MessageProperties
    {
        /// <summary>
        /// Marks the last assistant message of a turn as the one the user read, written by <c>MorganaAgent</c> at the end of the turn.
        /// The transcript reads it to leave out the turn's intermediate tool-calling messages.
        /// </summary>
        public const string UserFacing = "morgana:user_facing";

        /// <summary>The turn's reply exactly as the channel was given it, written beside <see cref="UserFacing"/> and read back by the transcript.</summary>
        public const string TurnText = "morgana:turn_text";

        /// <summary>
        /// Marks the agent's copy of a phrase Morgana already filed as her own, every phrase that arrived while no agent was active.
        /// The agent's model must read the phrase, while the transcript skips this copy so the user reads what they said once.
        /// </summary>
        public const string ContextOnly = "morgana:context_only";

        /// <summary>The A2A message metadata naming the intent of the agent that asked.</summary>
        public const string CallerIntent = "morgana:caller";

        /// <summary>The running summary <c>MorganaChatReducer</c> stores on its anchor message, under the key MEAI's own summarizer uses.</summary>
        public const string Summary = "__summary__";
    }

    /// <summary>
    /// The placeholders authored inside prompt prose and resolved in code.
    /// Double parentheses appear in no natural sentence, which matters because a prompt is edited by people who are not reading this file.
    /// </summary>
    internal static class Placeholders
    {
        /// <summary>In <see cref="Injections.ToolDescriptionContextGuidance"/> — the tool's own context-scoped parameter names.</summary>
        public const string ContextParameters = "((context_parameters))";

        /// <summary>In <see cref="Injections.HeldContextDeclaration"/> — the held variables as name: value pairs.</summary>
        public const string HeldVariables = "((held_variables))";

        /// <summary>In <see cref="Injections.PeerConsultationDeclaration"/> — the intent of the agent asking.</summary>
        public const string ConsultationCaller = "((caller))";

        /// <summary>In <see cref="Injections.PeerConsultationGuardrail"/> — the colleague's question, inside the fence that marks it as data.</summary>
        public const string ConsultationQuestion = "((question))";

        /// <summary>In <see cref="Injections.ColleaguesDeclaration"/> — one line per colleague: function name and territory.</summary>
        public const string Colleagues = "((colleagues))";

        /// <summary>In <see cref="Injections.QuarantinedToolResult"/> — the name of the tool whose result was withheld.</summary>
        public const string Tool = "((tool))";

        /// <summary>In the <see cref="Prompts.Classifier"/> prompt — the configured intents, formatted for ranking.</summary>
        public const string FormattedIntents = "((formattedIntents))";

        /// <summary>In the <see cref="Prompts.Presentation"/> prompt — the intents offered as opening quick replies.</summary>
        public const string Intents = "((intents))";

        /// <summary>In the <see cref="Prompts.ChannelAdapter"/> prompt — the target channel's capability budget, as JSON.</summary>
        public const string ChannelCapabilities = "((channel_capabilities))";
    }

    /// <summary>
    /// The values of <c>Records.ToolParameter.Scope</c>: where a tool's input comes from.
    /// A parameter carrying a value the model itself authors declares neither.
    /// </summary>
    public static class Scopes
    {
        /// <summary>Resolved from the session's context variables before the user is ever asked.</summary>
        public const string Context = "context";

        /// <summary>Obtained from the user, on the turn it is needed.</summary>
        public const string Request = "request";
    }

    /// <summary>The intent names the framework itself knows; every other intent is a domain's own.</summary>
    public static class Intents
    {
        /// <summary>
        /// The complement of whatever domain is deployed, which no agent handles and no button or collision ever offers.
        /// The router answers it with Morgana's presentation, with the classification-error message when it is a fallback.
        /// Three parties compare the name, while its description is authored in the Classifier prompt; a plugin declaring it is refused.
        /// </summary>
        public const string Other = "other";
    }

    /// <summary>
    /// Keys of a classification's metadata that the classifier, the supervisor and the router agree on.
    /// </summary>
    public static class ClassificationMetadata
    {
        /// <summary>Marks a classification as a technical fallback rather than the classifier's own answer: written by the classifier and the supervisor, read by the router.</summary>
        public const string Error = "error";

        /// <summary>The top intent's confidence on a 0 to 1 scale, written with two decimals whatever the host's locale.</summary>
        public const string Confidence = "confidence";

        /// <summary>The colliding intents, comma-separated and most confident first, whose presence alone diverts the turn to the disambiguation.</summary>
        public const string AmbiguousIntents = "ambiguousIntents";
    }

    /// <summary>
    /// The two delivery modes the framework ships, which a channel announces at the handshake and a transport is registered under.
    /// <c>ChannelCoordinates.DeliveryMode</c> stays a free-form string, so a new transport needs no contract change.
    /// </summary>
    public static class DeliveryModes
    {
        /// <summary>Duplex push over a hub; needs no callback address.</summary>
        public const string SignalR = "signalr";

        /// <summary>Outbound POST to the address the channel declared, which the ingress gate therefore requires.</summary>
        public const string Webhook = "webhook";
    }

    /// <summary>The agent-to-agent surface: how a colleague is named to a model, where its card lives and the name this installation signs its own peer traffic under.</summary>
    public static class AgentToAgent
    {
        /// <summary>Prefix of the function a colleague is offered under and the marker by which a consultation is recognised in an agent's own history.</summary>
        public const string PeerFunctionNamePrefix = "consult_";

        /// <summary>Root of the published A2A routes: <c>/a2a/{intent}</c>, with the card one level below it.</summary>
        public const string AgentPathPrefix = "/a2a";

        /// <summary>Where a published agent's card is served relative to its route, as the A2A specification fixes it for a caller that knows nothing else.</summary>
        public const string WellKnownAgentCardPath = ".well-known/agent-card.json";

        /// <summary>
        /// The name this installation signs consultations between its own agents under and admits them back under.
        /// Its secret is coined at every start, so the name is reserved rather than configured and no partner may take it.
        /// </summary>
        public const string IssuerName = "morgana";

        /// <summary>Name the bearer scheme is declared under on a card and referenced by in its requirements.</summary>
        public const string BearerSchemeName = "morgana-bearer";

        /// <summary>HTTP authentication scheme a card requires, in the spelling the A2A schema uses.</summary>
        public const string BearerScheme = "bearer";

        /// <summary>Format the bearer token is advertised in, as a hint to whoever has to produce one.</summary>
        public const string BearerFormat = "JWT";
    }

    /// <summary>
    /// The log lines a reader outside the process parses: the only place a context variable's name becomes observable.
    /// The PromptHarness reads them to assert the lookup-before-asking cycle, which no span carries since a name is data.
    /// </summary>
    public static class ObservableLogs
    {
        /// <summary>Emitter of the context-access lines, as it names itself in them.</summary>
        public const string ToolName = nameof(global::Morgana.AI.Tools.MorganaTool);

        /// <summary>Emitter of the declaration line, as it names itself in it.</summary>
        public const string ContextProviderName = nameof(Providers.MorganaAIContextProvider);

        /// <summary>The variable was already held: the lookup answered and the user was not asked.</summary>
        public const string Hit = "HIT";

        /// <summary>The variable was not held: asking the user is legitimate from here on.</summary>
        public const string Miss = "MISS";

        /// <summary>The variable was written, whoever the value came from.</summary>
        public const string Set = "SET";

        /// <summary>
        /// The stable head of all three context-access lines: the emitter, the operation and the variable name.
        /// Everything the harness needs is here, so the tails below may change without moving the reader.
        /// </summary>
        public const string ContextAccessHead = "{MorganaToolName} ({Name}) {Operation} variable '{VariableName}'";

        /// <summary>Read of a held variable, with the value it answered.</summary>
        public const string ContextHit = ContextAccessHead + " from agent context. Value is: {Value}";

        /// <summary>Read of a variable the session does not hold.</summary>
        public const string ContextMiss = ContextAccessHead + " from agent context.";

        /// <summary>Write of a variable, with the value stored.</summary>
        public const string ContextSet = ContextAccessHead + " into agent context. Value is: {Value}";

        /// <summary>
        /// The per-turn declaration of the variables handed to the model outright, several at once.
        /// It is why an agent may legitimately use a variable without ever calling GetContextVariable.
        /// </summary>
        public const string DeclaredContext = "{MorganaAiContextProviderName} DECLARED '{VariableNames}'";
    }

    /// <summary>
    /// The sentinels <c>appsettings.json</c> ships in place of a setting that must be filled in through User Secrets or the environment.
    /// A setting still holding one has not been configured and every reader treats it as absent.
    /// </summary>
    internal static class SecretOverrides
    {
        /// <summary>Stands in for a secret: an API key, a signing key.</summary>
        public const string Secure = "_SECURE_OVERRIDE_";

        /// <summary>Stands in for a required non-secret: a model id, an endpoint.</summary>
        public const string Functional = "_FUNCTIONAL_OVERRIDE_";

        /// <summary>Both, for a reader that only asks whether a setting was ever filled in.</summary>
        public static readonly string[] All = [Secure, Functional];
    }

    /// <summary>The characters that join or separate two pieces of composed text, structure that says nothing of its own.</summary>
    public static class Markers
    {
        /// <summary>
        /// Separates the stable two-layer prompt from the per-turn tail, so a provider's client can cache the former.
        /// The ASCII Record Separator is never typed by a person nor written by a model, so it cannot collide with real content.
        /// </summary>
        public const string DynamicInstructions = "\u001E";

        /// <summary>Joins the text of two consecutive assistant messages of one turn, streamed or not, so they never weld into one paragraph.</summary>
        public const string MessageSeparator = "\n\n";
    }

    /// <summary>
    /// The keys of the extra counts a provider's chat client reports beside the standard usage.
    /// The dust ledger and the telemetry read the same key, so a charge and a span count the same tokens.
    /// </summary>
    public static class UsageCounts
    {
        /// <summary>Prompt tokens written to the provider's cache by this call, billed apart from fresh and cached ones.</summary>
        public const string CacheCreationInputTokens = "CacheCreationInputTokens";
    }
}