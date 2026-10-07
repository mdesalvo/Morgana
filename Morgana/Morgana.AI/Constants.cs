namespace Morgana.AI;

/// <summary>
/// The framework's glossary: every literal that is a CONTRACT between two parties rather than a
/// value belonging to one of them. Peer of <see cref="Records"/> — that one centralizes the model,
/// this one the vocabulary the model is spoken in.
/// </summary>
public static class Constants
{
    /// <summary>
    /// The reserved name of the system, on which everything else rests...
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
    /// predictable, so an actor is reached by name from a later turn (or from a controller that
    /// holds nothing but the conversation id) instead of a reference having to be kept alive.
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
    /// The labels that open each section of a composed prompt. Put there by code at composition, never
    /// written in configuration, so a section can never reach a model without the label that tells it
    /// which section it is reading.
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
    /// Names of the global policies that code resolves by name. The rest of the list lives in
    /// <c>morgana.json</c> and nowhere else: a policy the framework only renders is read by the model
    /// and by whoever edits the prompt and naming it here would be an index that no rename breaks.
    /// </summary>
    public static class Policies
    {
        /// <summary>
        /// P8 — the two-role contract of a consultation and the one policy rendered conditionally:
        /// only an agent inside the A2A topology reads it (see <c>ComposeAgentInstructionsAsync</c>),
        /// so this name is resolved on every composition rather than only edited in the prompt.
        /// </summary>
        public const string PeerConsultation = "PeerConsultation";
    }

    /// <summary>
    /// Names of the entries in the framework prompt's <c>Injections</c> array, the sibling of its
    /// policies. They are templates, not rules: each is spliced at exactly one site instead of being
    /// rendered among the policies and each is resolved by name through <c>Injection.ResolveTemplate</c>.
    /// </summary>
    public static class Injections
    {
        /// <summary>Placed in front of a colleague's question, telling the answering agent who its reader is.</summary>
        public const string PeerConsultationDeclaration = "PeerConsultationDeclaration";

        /// <summary>Fences a colleague's question and states what its text may not claim or be given.</summary>
        public const string PeerConsultationGuardrail = "PeerConsultationGuardrail";

        /// <summary>Spliced into a peer-capable agent's own instructions, naming the colleagues it holds.</summary>
        public const string ColleaguesDeclaration = "ColleaguesDeclaration";

        /// <summary>Follows a turn that the model wrote without Reply, asking for that turn's closure alone.</summary>
        public const string TurnClosureRequest = "TurnClosureRequest";

        /// <summary>Answers a Reply the framework refused, with the reason it was refused.</summary>
        public const string ReplyNotAccepted = "ReplyNotAccepted";

        /// <summary>Closes the description of a tool requiring execution approval, telling the model that the user approves it.</summary>
        public const string ExecutionApprovalGuidance = "ExecutionApprovalGuidance";

        /// <summary>Wraps a tool result of an earlier turn, as the model reads it back in its history.</summary>
        public const string EarlierToolResult = "EarlierToolResult";

        /// <summary>Wraps the result of a workflow tool that left the workflow standing at a step.</summary>
        public const string WorkflowStepReached = "WorkflowStepReached";

        /// <summary>Wraps the result of the workflow tool that ended the workflow.</summary>
        public const string WorkflowEnded = "WorkflowEnded";
    }

    /// <summary>
    /// The keys of a prompt's <c>AdditionalProperties</c>: everything a prompt declares beside its
    /// four authored sections. Each is written in <c>morgana.json</c> or a plugin's <c>agents.json</c>
    /// and read by code that never sees that file, which is exactly the contract this glossary holds.
    /// </summary>
    public static class PromptProperties
    {
        /// <summary>The framework rules rendered into every agent's system prompt.</summary>
        public const string GlobalPolicies = "GlobalPolicies";

        /// <summary>The framework templates spliced where each has a referent (see <see cref="Constants.Injections"/>).</summary>
        public const string Injections = "Injections";

        /// <summary>The callable tools a prompt declares, framework base tools and domain tools alike.</summary>
        public const string Tools = "Tools";

        /// <summary>The texts that framework tools return to the model, authored rather than hard-coded.</summary>
        public const string ToolResults = "ToolResults";

        /// <summary>The buttons the framework adds to let the user stay with an agent or leave it, authored as data.</summary>
        public const string ServiceButtons = "ServiceButtons";

        /// <summary>The texts that the framework says to the user in its own voice, each fetched by name (see <see cref="Constants.Messages"/>).</summary>
        public const string Messages = "Messages";

        /// <summary>
        /// What a request matching no modelled agent is. It belongs to the classifier and to no domain,
        /// so it is authored beside the vocabulary it closes rather than in any plugin.
        /// </summary>
        public const string ComplementIntentDescription = "ComplementIntentDescription";

        /// <summary>The retired agents.json key of an agent's procedures, which startup refuses: workflows are declared on their class.</summary>
        public const string Workflows = "Workflows";
    }

    /// <summary>
    /// What kind of thing an outbound message is, declared on <c>ChannelMessage.MessageType</c> and
    /// read by every channel to decide how to paint it. Two of them are conversation (somebody
    /// said something to somebody); the rest are notices about the conversation rather than
    /// part of it: a channel shows those as banners that fade. Morgana keeps none of them on
    /// record, because a transcript is what was said.
    /// </summary>
    public static class MessageTypes
    {
        /// <summary>An answer, from an agent or from Morgana herself.</summary>
        public const string Assistant = "assistant";

        /// <summary>Morgana opening a conversation or handing one back, styled apart from an answer.</summary>
        public const string Presentation = "presentation";

        /// <summary>A notice about the conversation carrying no reply, such as a budget running low.</summary>
        public const string SystemWarning = "system_warning";

        /// <summary>
        /// A command's own frame or outcome, never anything else: a channel keeps it out of the transcript
        /// on this type alone, since a command is not a turn of the conversation.
        /// </summary>
        public const string System = "system";

        /// <summary>A notice that something stopped the turn, such as a budget that ran out.</summary>
        public const string Error = "error";
    }

    /// <summary>
    /// Why Morgana refused to take a call, declared on <c>ChannelMessage.ErrorReason</c>. A channel reads it to
    /// act on the refusal rather than merely paint it: a spent budget ends the conversation on its side too.
    /// </summary>
    public static class ErrorReasons
    {
        /// <summary>The conversation called too often in one of its windows; it may call again later.</summary>
        public const string RateLimitExceeded = "rate_limit_exceeded";

        /// <summary>The conversation's dust budget is spent: it will take no further turn or command.</summary>
        public const string DustBudgetExhausted = "dust_budget_exhausted";
    }

    /// <summary>
    /// Names of the entries in the framework prompt's <c>ServiceButtons</c> array, each read into the
    /// matching member of <c>Records.ServiceButtons</c>.
    /// </summary>
    public static class ServiceButtonSets
    {
        /// <summary>Offered when the turn answered the request and awaits nothing.</summary>
        public const string Closure = "Closure";

        /// <summary>Appended to the actions that a turn offers, so the user is never trapped in them.</summary>
        public const string Escape = "Escape";

        /// <summary>Offered when the turn asks to run a tool that needs the user's approval.</summary>
        public const string Approval = "Approval";
    }

    /// <summary>
    /// Names of the texts that the framework says to the user in its own voice, authored under a
    /// prompt's <c>Messages</c> section in morgana.json.
    /// </summary>
    public static class Messages
    {
        /// <summary>The opening message served when the presenter's own model call fails (Presentation).</summary>
        public const string Fallback = "Fallback";

        /// <summary>The opening message served by a deployment carrying no agent at all (Presentation).</summary>
        public const string NoAgents = "NoAgents";

        /// <summary>What the user is asked when two intents collide too closely to route between (Classifier).</summary>
        public const string Disambiguation = "Disambiguation";

        /// <summary>What the user is told when classification lands on an intent that no agent handles (Classifier).</summary>
        public const string UnrecognizedIntent = "UnrecognizedIntent";

        /// <summary>
        /// What Morgana says when an agent finishes and the conversation comes back to her (Morgana).
        /// <c>{0}</c> is that agent's display name.
        /// </summary>
        public const string AgentExit = "AgentExit";

        /// <summary>What the user is asked when a turn asks to run a tool that needs their approval and says nothing itself (Morgana).</summary>
        public const string Approval = "Approval";

        /// <summary>What the user is told when an agent's turn fails (Morgana).</summary>
        public const string GenericError = "GenericError";

        /// <summary>What the user is told when the model service itself fails a call (Morgana).</summary>
        public const string LLMServiceError = "LLMServiceError";
    }

    /// <summary>
    /// Names of the texts that framework tools return, authored under <c>ToolResults</c> in morgana.json.
    /// </summary>
    public static class ToolResults
    {
        /// <summary>Reply accepted the closure.</summary>
        public const string TurnClosed = "TurnClosed";

        /// <summary>A tool did not run because a context-scoped value is held by nobody yet.</summary>
        public const string ContextValueMissing = "ContextValueMissing";

        /// <summary>Reply refused: nothing of the turn has reached the user.</summary>
        public const string ReplyWithoutText = "ReplyWithoutText";

        /// <summary>Reply refused: the card nests too deep.</summary>
        public const string CardTooDeep = "CardTooDeep";

        /// <summary>Reply refused: the card holds too many components.</summary>
        public const string CardTooLarge = "CardTooLarge";

        /// <summary>A colleague answering a consultation tried to consult another.</summary>
        public const string ConsultationChained = "ConsultationChained";

        /// <summary>A turn spent its consultation rounds.</summary>
        public const string ConsultationRoundsExhausted = "ConsultationRoundsExhausted";

        /// <summary>A colleague of this installation failed while answering.</summary>
        public const string ColleagueCouldNotAnswer = "ColleagueCouldNotAnswer";

        /// <summary>A published agent refused a partner's new conversation.</summary>
        public const string PeerAtCapacity = "PeerAtCapacity";

        /// <summary>A published agent's conversation has no dust left.</summary>
        public const string PeerOutOfBudget = "PeerOutOfBudget";

        /// <summary>A published agent did not answer within the wait.</summary>
        public const string PeerTimedOut = "PeerTimedOut";

        /// <summary>A published agent failed while answering.</summary>
        public const string PeerFailed = "PeerFailed";

        /// <summary>LaunchWorkflow started a workflow.</summary>
        public const string WorkflowStarted = "WorkflowStarted";

        /// <summary>A tool was called that the running workflow does not offer at its current step.</summary>
        public const string ToolNotAtThisStep = "ToolNotAtThisStep";

        /// <summary>A Reply at a choice step did not offer exactly the step's tools, one action each.</summary>
        public const string StepActionsRequired = "StepActionsRequired";
    }

    /// <summary>
    /// The base tool the framework resolves by name: it is the one call that closes a turn, so the
    /// agent forces it when the model forgot it and returns its argument errors for repair.
    /// </summary>
    public static class Tools
    {
        /// <summary>Closes the agent's turn: whether it awaits the user, the actions that it offers and its card.</summary>
        public const string Reply = "Reply";

        /// <summary>Starts one of the agent's workflows; offered only to an agent that declares one.</summary>
        public const string LaunchWorkflow = "LaunchWorkflow";
    }

    /// <summary>
    /// What a workflow class and its launch tool agree on across the plugin and morgana.json.
    /// Also what a plugin's returned record and the workflow engine reading its result agree on.
    /// </summary>
    public static class Workflows
    {
        /// <summary>The wire name of the nullable property of a tool's returned record that holds a value only when the call failed.</summary>
        public const string FailureField = "error";

        /// <summary>The suffix of a workflow class name that the workflow's name drops: <c>PlaceOrderWorkflow</c> is launched as <c>PlaceOrder</c>.</summary>
        public const string ClassNameSuffix = "Workflow";

        /// <summary>The parameter of <see cref="Tools.LaunchWorkflow"/> that names the workflow to start.</summary>
        public const string WorkflowParameter = "workflow";
    }

    /// <summary>
    /// The framework's own context keys: machinery rather than knowledge about the user, so no tool
    /// parameter is ever resolved from them. Written by a base tool or by the consultation guards and
    /// dropped by the agent at end of turn; <see cref="WorkflowPosition"/> alone outlives the turn.
    /// </summary>
    public static class ContextKeys
    {
        /// <summary>Set by <see cref="Tools.Reply"/>; read once, then dropped.</summary>
        public const string TurnReply = "turn_reply";

        /// <summary>Marks the turn as serving a colleague, which is what refuses a second hop.</summary>
        public const string ServingConsultation = "peer_consultation";

        /// <summary>Counts the consultations spent on one user turn, against the configured cap.</summary>
        public const string ConsultationRounds = "peer_consultation_rounds";

        /// <summary>
        /// Where the running workflow stands. Unlike its siblings it is not one turn long: it lasts until the
        /// workflow ends or the user leaves, so no end-of-turn drop may touch it.
        /// </summary>
        public const string WorkflowPosition = "workflow_position";
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
        /// delivered to the channel and read back by
        /// <c>SQLiteConversationPersistenceService.ExtractTextFromMessage</c>.
        /// </summary>
        public const string TurnText = "morgana:turn_text";

        /// <summary>
        /// Written alongside <see cref="TurnText"/>, carrying the buttons delivered with the turn, so a
        /// transcript shows exactly what the user was offered whoever composed them.
        /// </summary>
        public const string TurnQuickReplies = "morgana:turn_quick_replies";

        /// <summary>Written alongside <see cref="TurnText"/>, carrying the card delivered with the turn.</summary>
        public const string TurnRichCard = "morgana:turn_rich_card";

        /// <summary>
        /// Written on the user-facing message of a turn that the user left on. What the agent's model reads
        /// starts after the last one: a returning user opens a new episode, never the end of the old one.
        /// </summary>
        public const string EpisodeEnd = "morgana:episode_end";

        /// <summary>
        /// Written by <c>MorganaAgent</c> on a user message the orchestrator had already filed as
        /// her own, which is every phrase that arrived while no agent was active. The agent keeps
        /// the phrase because its model must read it. This says the phrase belongs to somebody
        /// else's side of the conversation: a transcript takes it from there and skips this copy,
        /// so the user reads what they said once rather than twice.
        /// </summary>
        public const string ContextOnly = "morgana:context_only";

        /// <summary>A2A message metadata naming the agent that asked. Dotted, not colon-separated, because it travels the protocol.</summary>
        public const string CallerIntent = "morgana:caller";

        /// <summary>The running summary a reducer stores on its anchor message. MEAI's own name, kept so sessions summarized before <c>MorganaChatReducer</c> shipped still resume.</summary>
        public const string Summary = "__summary__";
    }

    /// <summary>
    /// Placeholders authored inside prompt prose and resolved in code. Double parentheses because no
    /// natural sentence contains them and a prompt is edited by people who are not reading this file.
    /// </summary>
    public static class Placeholders
    {
        /// <summary>In <see cref="Injections.PeerConsultationDeclaration"/> — the intent of the agent asking.</summary>
        public const string ConsultationCaller = "((caller))";

        /// <summary>In <see cref="Injections.PeerConsultationGuardrail"/> — the colleague's question, inside the fence that marks it as data.</summary>
        public const string ConsultationQuestion = "((question))";

        /// <summary>In a tool result — the framework tool or colleague the text is about.</summary>
        public const string ToolName = "((tool))";

        /// <summary>In <see cref="ToolResults.ContextValueMissing"/> — the context-scoped values no one holds yet.</summary>
        public const string MissingValues = "((missing))";

        /// <summary>In <see cref="ToolResults.CardTooDeep"/> — how many levels the refused card nests.</summary>
        public const string CardDepth = "((depth))";

        /// <summary>In <see cref="ToolResults.CardTooLarge"/> — how many components the refused card holds.</summary>
        public const string CardComponents = "((count))";

        /// <summary>In the card refusals — the limit the card broke.</summary>
        public const string Limit = "((max))";

        /// <summary>In <see cref="ToolResults.ConsultationRoundsExhausted"/> — the rounds already spent.</summary>
        public const string ConsultationRounds = "((rounds))";

        /// <summary>In the colleague fallbacks — the intent of the colleague that did not answer.</summary>
        public const string AgentIntent = "((agent))";

        /// <summary>In the workflow texts — the workflow that the text is about.</summary>
        public const string Workflow = "((workflow))";

        /// <summary>In the workflow texts — the step at which the workflow stands.</summary>
        public const string Step = "((step))";

        /// <summary>In <see cref="ToolResults.StepActionsRequired"/> — the tools of the step, comma-joined.</summary>
        public const string Tools = "((tools))";

        /// <summary>In <see cref="Injections.EarlierToolResult"/> and the workflow injections — the result as the tool returned it.</summary>
        public const string EarlierToolResultContent = "((result))";

        /// <summary>In <see cref="Injections.ReplyNotAccepted"/> — why the closure was refused.</summary>
        public const string ReplyNotAcceptedReason = "((reason))";

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
        /// The complement of whatever domain is deployed: what a request matching no modelled agent
        /// is. No agent handles it, it is never offered as a quick reply and never counts as a
        /// collision candidate. Routed all the same, so the router answers with its
        /// unrecognized-intent message rather than the pipeline stalling.
        /// </summary>
        /// <remarks>
        /// The classifier's own and no domain's: the name is here because three parties compare it,
        /// while the description a model reads is authored in the Classifier prompt beside the rest
        /// of that actor's prose. A plugin declaring it is ignored at the door.
        /// </remarks>
        public const string Other = "other";
    }

    /// <summary>
    /// Transport dispatch keys: a channel announces one at the handshake and the host must have a
    /// service registered under exactly that spelling or the conversation is refused at ingress.
    /// </summary>
    /// <remarks>
    /// The set is deliberately open: <c>ChannelCoordinates.DeliveryMode</c> stays a free-form string
    /// so a new transport needs no contract change. These are the two this framework ships.
    /// </remarks>
    public static class DeliveryModes
    {
        /// <summary>Duplex push over a hub; needs no callback address.</summary>
        public const string SignalR = "signalr";

        /// <summary>Outbound POST to the address the channel declared, which the ingress gate therefore requires.</summary>
        public const string Webhook = "webhook";
    }

    /// <summary>
    /// The agent-to-agent surface: how a colleague is named to a model, where its card lives and
    /// under whose name this installation signs its own peer traffic.
    /// </summary>
    public static class AgentToAgent
    {
        /// <summary>Prefix of the function a colleague is offered under and the marker by which a consultation is recognised in an agent's own history.</summary>
        public const string PeerFunctionNamePrefix = "consult_";

        /// <summary>Root of the published A2A routes: <c>/a2a/{intent}</c>, with the card one level below it.</summary>
        public const string AgentPathPrefix = "/a2a";

        /// <summary>
        /// Where a published agent's card is served, relative to its own route. Fixed by the A2A
        /// specification: it is the one address a caller that knows nothing else can try.
        /// </summary>
        public const string WellKnownAgentCardPath = ".well-known/agent-card.json";

        /// <summary>
        /// Name this installation signs consultations between its own agents under and admits them
        /// back in under. Configured nowhere: the secret behind it is coined at every start, so the
        /// name is reserved rather than declared and no partner may be given it.
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
    /// Log lines somebody OUTSIDE the process reads. Ordinary logging is prose for an operator and
    /// belongs nowhere near this file; these three lines are different — they are the only place a
    /// context variable's NAME becomes observable and the PromptHarness parses them to assert how a
    /// context-scoped parameter was resolved, which no span attribute carries (a name is data and spans carry
    /// none). That makes their shape a contract with a reader that cannot be recompiled with them.
    /// </summary>
    public static class ObservableLogs
    {
        /// <summary>Emitter of the context-access lines, as it names itself in them.</summary>
        public const string ToolName = nameof(Adapters.MorganaToolAdapter);

        /// <summary>The model omitted the variable and the session held it: the user was not asked.</summary>
        public const string Hit = "HIT";

        /// <summary>The model omitted the variable and the session lacked it: the tool did not run.</summary>
        public const string Miss = "MISS";

        /// <summary>The model passed the variable, which was stored before the tool ran.</summary>
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
    }

    /// <summary>
    /// Sentinel values <c>appsettings.json</c> ships in place of a setting that MUST be filled in
    /// before the application is usable — a secret through User Secrets or the environment, a
    /// non-secret required value through either. A setting still holding one has not been
    /// configured and every reader treats it as absent rather than as a value.
    /// </summary>
    public static class SecretOverrides
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
    /// structure and says nothing of its own to whoever reads the text it punctuates.
    /// </summary>
    public static class Markers
    {
        /// <summary>
        /// Inserted between the text of two consecutive assistant messages of the same turn, both
        /// while streaming and when batching, so two messages meant to be read apart do not weld
        /// into one paragraph.
        /// </summary>
        public const string MessageSeparator = "\n\n";
    }
}