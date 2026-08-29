using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Morgana.AI.Attributes;
using Morgana.Contracts;

namespace Morgana.AI;

/// <summary>
/// Immutable record types (DTOs) for actor messages, configuration, and serialization.
/// Organized by functional area: conversation lifecycle, classification, prompts, tools, presentation, LLM providers.
/// Immutability ensures thread-safety; explicit types prevent routing errors in actor system.
/// </summary>
public static class Records
{
    /// <summary>
    /// Standard STJ deserialization options used by Morgana
    /// </summary>
    internal static readonly JsonSerializerOptions DefaultJsonSerializerOptions =
        new JsonSerializerOptions
        {
            AllowOutOfOrderMetadataProperties = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            PropertyNameCaseInsensitive = true
        };

    // ==========================================================================
    // CONVERSATION LIFECYCLE MESSAGES
    // ==========================================================================

    /// <summary>
    /// Supervisor → Manager final response: text, classification, metadata, agent info, optional quick replies/rich card.
    /// AgentName: agent identifier (e.g., "Morgana (Billing)"). AgentCompleted: flags multi-turn completion.
    /// </summary>
    public record ConversationResponse(
        string Response,
        string? Classification,
        Dictionary<string, string>? Metadata,
        string? AgentName = null,
        bool AgentCompleted = false,
        List<QuickReply>? QuickReplies = null,
        DateTime? OriginalTimestamp = null,
        RichCard? RichCard = null);

    /// <summary>
    /// Request to create a new conversation and initialize the actor hierarchy.
    /// </summary>
    /// <param name="ConversationId">Unique identifier for the new conversation</param>
    /// <param name="IsRestore">Flag indicating that the conversation is being created or restored</param>
    /// <param name="ChannelMetadata">Channel metadata declared by the originating client at start
    /// (channel name + capability budget). Required on fresh starts — Morgana refuses to
    /// create a conversation for a channel that does not announce itself. Null on restore,
    /// where the manager loads the persisted entry instead.</param>
    public record CreateConversation(
        string ConversationId,
        bool IsRestore,
        ChannelMetadata? ChannelMetadata = null);

    /// <summary>
    /// Request to terminate a conversation and stop all associated actors.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation to terminate</param>
    public record TerminateConversation(
        string ConversationId);

    // ==========================================================================
    // CONVERSATION PERSISTENCE
    // ==========================================================================

    /// <summary>
    /// Configuration for SQLite conversation persistence with AES-256 encryption.
    /// StoragePath: directory for conversation databases (auto-created if missing).
    /// EncryptionKey: base64-encoded 256-bit key (CRITICAL: keep secure, never commit).
    /// </summary>
    public record ConversationPersistenceOptions
    {
        /// <summary>
        /// Directory path where conversation files will be stored.
        /// Directory will be created if it doesn't exist.
        /// </summary>
        /// <example>C:/MorganaData</example>
        public string StoragePath { get; set; } = string.Empty;

        /// <summary>
        /// Base64-encoded 256-bit AES encryption key for conversation data.<br/>
        /// CRITICAL: Keep this key secure and never commit it to source control.
        /// </summary>
        /// <example>3q2+7w8e9r0t1y2u3i4o5p6a7s8d9f0g1h2j3k4l5z6x7c8v9b0n1m2==</example>
        public string EncryptionKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request to restore active agent state when resuming a conversation from persistence.
    /// Sent to ConversationSupervisor after conversation resume to set activeAgent and activeAgentIntent.
    /// </summary>
    /// <param name="AgentIntent">Intent of the agent that was last active (e.g., "billing", "contract")</param>
    public record RestoreActiveAgent(string AgentIntent);

    /// <summary>
    /// Request sent to RouterActor to restore/resolve an agent by intent.
    /// Returns the agent reference if successful, or null if intent is invalid.
    /// Router will cache the agent for future routing operations.
    /// </summary>
    /// <param name="AgentIntent">Intent name to resolve agent for</param>
    public record RestoreAgentRequest(string AgentIntent);

    /// <summary>
    /// Response from RouterActor containing the resolved agent reference.
    /// Null AgentRef indicates the intent could not be resolved to a valid agent.
    /// </summary>
    /// <param name="AgentIntent">Original intent requested</param>
    /// <param name="AgentRef">Resolved agent reference, or null if not found</param>
    public record RestoreAgentResponse(string AgentIntent, IActorRef? AgentRef);

    // ==========================================================================
    // RATE LIMITING
    // ==========================================================================

    /// <summary>
    /// Conversation rate limiting config: Enabled toggle, per-minute/hour/day limits, per-window error messages.
    /// Set Enabled=false for development. Sliding window algorithm enforced via SQLiteRateLimitService.
    /// </summary>
    public record RateLimitOptions
    {
        /// <summary>
        /// Master toggle for rate limiting feature.
        /// Set to false to disable all rate limiting (useful for development/testing).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum messages allowed per minute per conversation.
        /// Prevents burst spam. Set to 0 to disable this check.
        /// </summary>
        /// <example>5</example>
        public int MaxMessagesPerMinute { get; set; } = 5;

        /// <summary>
        /// Maximum messages allowed per hour per conversation.
        /// Prevents sustained abuse. Set to 0 to disable this check.
        /// </summary>
        /// <example>30</example>
        public int MaxMessagesPerHour { get; set; } = 30;

        /// <summary>
        /// Maximum messages allowed per day per conversation.
        /// Enforces daily quotas. Set to 0 to disable this check.
        /// </summary>
        /// <example>100</example>
        public int MaxMessagesPerDay { get; set; } = 80;

        /// <summary>
        /// Error message displayed when per-minute limit is exceeded.
        /// Supports placeholders: {limit} for the actual limit value.
        /// </summary>
        /// <example>✋ Whoa there! You're casting spells too quickly. Please wait a moment before trying again.</example>
        public string ErrorMessagePerMinute { get; set; } =
            "✋ Whoa there! You're casting spells too quickly. Please wait a moment before trying again.";

        /// <summary>
        /// Error message displayed when per-hour limit is exceeded.
        /// Supports placeholders: {limit} for the actual limit value.
        /// </summary>
        /// <example>⏰ You've reached your hourly spell quota. The magic cauldron needs time to recharge!</example>
        public string ErrorMessagePerHour { get; set; } =
            "⏰ You've reached your hourly spell quota. The magic cauldron needs time to recharge!";

        /// <summary>
        /// Error message displayed when per-day limit is exceeded.
        /// Supports placeholders: {limit} for the actual limit value.
        /// </summary>
        /// <example>🌙 You've exhausted today's magical energy. Return tomorrow for more spells!</example>
        public string ErrorMessagePerDay { get; set; } =
            "🌙 You've exhausted today's magical energy. Return tomorrow for more spells!";

        /// <summary>
        /// Default error message for unknown/generic rate limit violations.
        /// </summary>
        /// <example>⚠️ You're sending messages too quickly. Please slow down.</example>
        public string ErrorMessageDefault { get; set; } =
            "⚠️ You're sending messages too quickly. Please slow down.";
    }

    /// <summary>
    /// Result of a rate limit check operation.
    /// </summary>
    /// <param name="IsAllowed">Whether the request is allowed to proceed</param>
    /// <param name="ViolatedLimit">Description of which limit was exceeded (null if allowed)</param>
    /// <param name="RetryAfterSeconds">Suggested wait time in seconds before retrying (null if allowed)</param>
    public record RateLimitResult(
        bool IsAllowed,
        string? ViolatedLimit = null,
        int? RetryAfterSeconds = null);

    // ==========================================================================
    // MAGIC DUST (TOKEN BUDGET)
    // ==========================================================================

    /// <summary>
    /// Per-tier pricing: tokens-per-dust and cache cost-weights for accurate token-budget tracking.
    /// Live cost = (InputTokens × CachedInputWeight) + (CacheWriteTokens × CacheCreationWeight).
    /// Zero on either axis means that direction is free. Defaults calibrated for Haiku 4.5/Sonnet 5
    /// or gpt-4o-mini/gpt-4o; recalibrate when pointing a tier at a different model.
    /// Sanity-check BudgetPerConversation against your heaviest agent's actual calls-per-turn
    /// by inspecting dust_usage_log, not against nominal turn counts.
    /// </summary>
    public record MagicDustPricing
    {
        /// <summary>Input tokens that equal one dust unit. 0 disables input charging.</summary>
        public int InputTokensPerDustUnit { get; set; }

        /// <summary>Output tokens that equal one dust unit. 0 disables output charging.</summary>
        public int OutputTokensPerDustUnit { get; set; }

        /// <summary>
        /// Cost weight applied to cache-read input tokens (<c>CachedInputTokenCount</c>)
        /// relative to a fresh input token. Anthropic cache-read ≈ 0.10; OpenAI ≈ 0.50.
        /// </summary>
        public double CachedInputWeight { get; set; }

        /// <summary>
        /// Cost weight applied to cache-creation input tokens
        /// (<c>AdditionalCounts["CacheCreationInputTokens"]</c>) relative to a fresh input
        /// token. Anthropic 1h cache write ≈ 2.0; providers with no separate write cost = 1.0.
        /// </summary>
        public double CacheCreationWeight { get; set; }
    }

    // ==========================================================================
    // LLM TIERS (MULTI-MODEL)
    // ==========================================================================

    /// <summary>
    /// Closed set of LLM power/cost tiers an agent can declare itself against via
    /// <see cref="Attributes.RequiresLLMTierAttribute"/>. Modeled on the Intel E-core/P-core
    /// die split rather than a graduated price ladder: there are exactly two physical
    /// "core types" an agent can be built on, not a spectrum. Ordinal order (Efficiency &lt;
    /// Performance) is load-bearing: it is used to pick the framework-actor default (the
    /// cheapest configured tier). Cross-tier pricing is deliberately NOT assumed to be
    /// monotonic — see <c>Services.RequiresLLMTierValidationService</c> remarks.
    /// </summary>
    public enum LLMTier
    {
        /// <summary>
        /// The E-core die: Morgana's own framework actors (Guard, Classifier, Presenter,
        /// ChannelAdapter) and any domain agent handling routine work run here. Default for
        /// framework actors — always the cheapest configured tier. An agent belongs on
        /// Efficiency unless its author can point to a genuine, existential need for the
        /// expressive or computational headroom only Performance provides; "it might do a
        /// bit better" is not that bar.
        /// </summary>
        Efficiency,

        /// <summary>
        /// The P-core die: reserved exclusively for agents whose domain author declares an
        /// existential need for deep reasoning or high expressive power — not a "nicer to
        /// have" upgrade from Efficiency, a hard requirement the agent cannot function
        /// without. Most capable/expensive tier.
        /// </summary>
        Performance
    }

    /// <summary>
    /// Single provider die (E-core/P-core) with own dust pricing. Tier IS the unit deployer configures + agent binds to.
    /// Lives under Morgana:LLM:{Provider}:Tiers as JSON object (keyed by name) not array: allows per-layer overrides to merge
    /// by key. TierConfiguration is deliberate ChatOptions subset (ModelId, MaxOutputTokens only); MagicDust is tier-specific.
    /// </summary>
    public record TierDefinition(
        TierConfiguration Options,
        MagicDustPricing MagicDust);

    /// <summary>
    /// Deliberately minimal JSON-bindable DTO of tier-configurable ChatOptions subset.
    /// Excludes sampling knobs, Reasoning, StopSequences, and per-call parameters.
    /// Contains only ModelId (provider-specific identifier, e.g. "claude-haiku-4-5") and
    /// MaxOutputTokens (per-tier ceiling). Left null, MaxOutputTokens defers to provider SDK
    /// defaults, which are NOT uniform: Anthropic caps at 1024, while OpenAI/AzureOpenAI/Ollama
    /// leave uncapped up to the model's context window.
    /// </summary>
    public record TierConfiguration(
        string ModelId,
        int? MaxOutputTokens = null)
    {
        /// <summary>
        /// Materializes this census into a real <see cref="ChatOptions"/>, ready to be merged
        /// (field-by-field, fill-if-absent — see <see cref="ChatClients.TierDefaultsChatClient"/>)
        /// into every per-turn call on this tier.
        /// </summary>
        public ChatOptions ToChatOptions() => new()
        {
            ModelId = ModelId,
            MaxOutputTokens = MaxOutputTokens
        };
    }

    /// <summary>
    /// Sentinel placeholder values used throughout appsettings.json for settings that MUST be
    /// overridden via User Secrets or environment variables before the app is usable (see
    /// CLAUDE.md "Conventions": <c>_SECURE_OVERRIDE_</c> for secrets, <c>_FUNCTIONAL_OVERRIDE_</c>
    /// for non-secret required values). Lives here, next to <see cref="TierDefinition"/>, rather
    /// than inside <c>MorganaLLM</c>, because recognizing an unfilled placeholder is a config
    /// convention — not LLM-specific behavior — that any consumer of appsettings.json could
    /// reasonably need to check, not just the LLM provider constructors that happen to be the
    /// only ones checking it today.
    /// </summary>
    internal static readonly string[] OverridePlaceholders = ["_SECURE_OVERRIDE_", "_FUNCTIONAL_OVERRIDE_"];

    /// <summary>
    /// Per-conversation lifetime dust budget (no sliding window, no reset). Orthogonal to RateLimitOptions.
    /// Message templates are English defaults; deployments override in Morgana:DustLimiting with own personality.
    /// Percent placeholder: fuel-gauge semantics (remaining as 0–100 integer, not dust units).
    /// </summary>
    public record DustLimitingOptions
    {
        /// <summary>Master toggle. When false the limiter is fully bypassed (fail open).</summary>
        public bool Enabled { get; set; }

        /// <summary>Total dust a conversation may consume over its lifetime.</summary>
        public double BudgetPerConversation { get; set; }

        /// <summary>One-shot advisory shown when consumption crosses 70%.</summary>
        public string Warning70Message { get; set; }

        /// <summary>One-shot advisory shown when consumption crosses 90%.</summary>
        public string Warning90Message { get; set; }

        /// <summary>Blocking message shown when the budget is exhausted (100%).</summary>
        public string ErrorMessage { get; set; }
    }

    // ==========================================================================
    // AUTHENTICATION
    // ==========================================================================

    /// <summary>
    /// Per-issuer trust model: each channel declares own IssuerOptions entry with own signing key (compromise isolation).
    /// Tokens with undeclared iss claim are rejected. Onboarding new channel: add IssuerOptions where Name=iss claim,
    /// SymmetricKey=channel's signing secret. Rejected at first request if not declared or key mismatch.
    /// </summary>
    public record AuthenticationOptions
    {
        /// <summary>
        /// Declared issuers Morgana will accept tokens from. Each entry carries its own
        /// signing key, so the blast radius of a leaked key is limited to a single channel.
        /// A token whose <c>iss</c> claim is not in this list is rejected.
        /// </summary>
        public List<IssuerOptions> Issuers { get; set; } = [];

        /// <summary>
        /// Expected audience claim (<c>aud</c>) in the token.
        /// Tokens with a different audience will be rejected.
        /// </summary>
        /// <example>morgana-api</example>
        public string Audience { get; set; } = "morgana.ai";
    }

    /// <summary>
    /// Per-issuer authentication entry. Binds an issuer name (the value Morgana
    /// expects in the JWT <c>iss</c> claim) to the signing key used to validate
    /// tokens emitted under that name.
    /// </summary>
    public record IssuerOptions
    {
        /// <summary>
        /// Issuer name as it appears in the JWT <c>iss</c> claim
        /// (lowercase channel identifier, e.g. <c>"cauldron"</c>).
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Shared symmetric key used to validate this issuer's tokens (HMAC-SHA256).
        /// Must be at least 256 bits (32 bytes). Override via User Secrets or
        /// environment variables in production.
        /// </summary>
        public string SymmetricKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of a token authentication operation.
    /// </summary>
    /// <param name="IsAuthenticated">Whether the token was successfully validated</param>
    /// <param name="CallerId">The caller's unique identifier (from the <c>sub</c> claim), null if not authenticated</param>
    /// <param name="DisplayName">The caller's display name (from the <c>name</c> claim), null if not authenticated</param>
    /// <param name="Error">Description of why authentication failed, null if authenticated</param>
    public record AuthenticationResult(
        bool IsAuthenticated,
        string? CallerId = null,
        string? DisplayName = null,
        string? Error = null);

    // ==========================================================================
    // USER MESSAGE HANDLING
    // ==========================================================================

    /// <summary>
    /// User message submitted for processing through the conversation pipeline.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Text">User's message text</param>
    /// <param name="Timestamp">Timestamp when the message was created</param>
    /// <param name="TurnContext">OpenTelemetry activity context for the current turn span.</param>
    /// <param name="CallerId">Authenticated caller identity, propagated from the HTTP layer for conversation ownership and audit.</param>
    public record UserMessage(
        string ConversationId,
        string Text,
        DateTime Timestamp,
        ActivityContext TurnContext = default,
        string? CallerId = null);

    // ==========================================================================
    // GUARD (CONTENT MODERATION) MESSAGES
    // ==========================================================================

    /// <summary>
    /// Request for content moderation check on a user message.
    /// Sent to GuardActor for LLM-based policy checking.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Message">User message to check for policy violations</param>
    public record GuardCheckRequest(
        string ConversationId,
        string Message);

    /// <summary>
    /// Result of content moderation check from GuardActor.
    /// </summary>
    /// <param name="Compliant">True if message passes policy checks, false if violation detected</param>
    /// <param name="Violation">Description of policy violation if Compliant is false</param>
    public record GuardCheckResponse(
        [property: JsonPropertyName("compliant")] bool Compliant,
        [property: JsonPropertyName("violation")] string? Violation);

    /// <summary>
    /// IGuardRailService result: Compliant (true=passes all checks, false=violated). Violation: human-readable
    /// description of violated rule. Public contract, intentionally decoupled from GuardCheckResponse (internal LLM wire-format DTO).
    /// </summary>
    public record GuardRailResult(
        bool Compliant,
        string? Violation);
    
    /// <summary>
    /// Sent by an agent back to the supervisor when the LLM provider rejects the request
    /// due to a content filter (e.g. Azure OpenAI content_filter).
    /// The supervisor treats this identically to a guard rejection and routes the reply
    /// to the user-facing sender captured in its current processing state.
    /// </summary>
    public record ContentFilterRejection;

    // ==========================================================================
    // CLASSIFICATION MESSAGES
    // ==========================================================================

    /// <summary>
    /// LLM response from ClassifierActor: every intent the classifier judged plausible, ranked by
    /// confidence. One entry for a clean match; two or more only for a genuine collision — see
    /// <see cref="Services.LLMClassifierService"/> for how that distinction gets made.
    /// </summary>
    /// <param name="Intents">Candidate intents, ranked by confidence (highest first).</param>
    public record ClassificationResponse(
        [property: JsonPropertyName("intents")] List<IntentScore> Intents);

    /// <summary>One candidate intent with its confidence score, as scored by the classifier LLM.</summary>
    /// <param name="Intent">Candidate intent name (e.g., "billing", "contract", "other").</param>
    /// <param name="Confidence">Confidence 0.0-1.0, this intent's own match quality — not a rank.</param>
    public record IntentScore(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("confidence")] double Confidence);

    /// <summary>
    /// Internal classification result used by the conversation pipeline.
    /// Contains the classified intent and additional metadata for routing and diagnostics.
    /// </summary>
    /// <param name="Intent">Classified intent name — the top-ranked candidate.</param>
    /// <param name="Metadata">
    /// Confidence, error codes, etc. Also carries disambiguation as the well-known key
    /// <c>"ambiguousIntents"</c> — see <see cref="Services.LLMClassifierService"/> for how it's
    /// computed and <see cref="Actors.ConversationSupervisorActor"/> for how it's consumed.
    /// </param>
    public record ClassificationResult(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("metadata")] Dictionary<string, string> Metadata);

    // ==========================================================================
    // AGENT REQUEST/RESPONSE MODELS
    // ==========================================================================

    /// <summary>
    /// Request to agent: ConversationId, user message Content (null for tool-only), optional Classification
    /// (null for follow-up to active agents), TurnContext for OTel span, channel Capabilities (null→full capability).
    /// </summary>
    public record AgentRequest(
        string ConversationId,
        string? Content,
        ClassificationResult? Classification,
        ActivityContext TurnContext = default,
        ChannelCapabilities? Capabilities = null);

    /// <summary>
    /// Agent response: text, IsCompleted flag (true→idle, false→agent stays active),
    /// optional QuickReplies, optional RichCard for structured UX (e.g., contract terms, invoice details).
    /// </summary>
    public record AgentResponse(
        string Response,
        bool IsCompleted = true,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null);

    /// <summary>
    /// Response from RouterActor containing both the agent's response and a reference to the agent actor.
    /// Used to track which agent is handling the request for multi-turn conversation management.
    /// </summary>
    /// <param name="Response">Agent's response text</param>
    /// <param name="IsCompleted">Whether the agent has completed its task</param>
    /// <param name="AgentRef">Actor reference to the agent that generated this response</param>
    /// <param name="QuickReplies">Optional list of quick reply buttons from the agent</param>
    /// <param name="RichCard">Optional rich card from the agent</param>
    public record ActiveAgentResponse(
        string Response,
        bool IsCompleted,
        IActorRef AgentRef,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null);

    /// <summary>
    /// Represents a streaming chunk from an agent during real-time response generation.
    /// Sent incrementally to enable progressive UI rendering.
    /// </summary>
    public record AgentStreamChunk(
        string Text);

    // ==========================================================================
    // PEER CONSULTATION MODELS
    // ==========================================================================

    /// <summary>
    /// Question one agent puts to a colleague of the same conversation, sent to the colleague's
    /// actor and answered with a <see cref="PeerConsultationResponse"/>.
    /// </summary>
    /// <param name="ConversationId">Conversation both agents belong to; scopes session and shared context.</param>
    /// <param name="TurnContext">OTel context of the user turn that triggered the consultation.</param>
    public record PeerConsultation(
        string ConversationId,
        string CallerIntent,
        string Question,
        ActivityContext TurnContext = default);

    /// <summary>
    /// A colleague's answer, both as the actor reply and — serialized — as the tool result the
    /// asking agent's model reads, which is why every member carries an explicit JSON name.
    /// </summary>
    /// <param name="ColleagueAwaitsYourReply">True when the colleague is waiting, i.e. the exchange is unfinished.</param>
    /// <param name="Options">Options offered, as data to choose from — never buttons to render.</param>
    /// <param name="Card">Structured data presented, as data to read — never a card to render.</param>
    public record PeerConsultationResponse(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("colleagueAwaitsYourReply")] bool ColleagueAwaitsYourReply,
        [property: JsonPropertyName("options")] List<QuickReply>? Options = null,
        [property: JsonPropertyName("card")] RichCard? Card = null);

    /// <summary>
    /// LLM-generated presentation response from ConversationSupervisorActor.
    /// Contains the welcome message and quick reply buttons for user interaction.
    /// Deserialized from JSON returned by the LLM when generating presentation messages.
    /// </summary>
    /// <param name="Message">Welcome/presentation message text (2-4 sentences)</param>
    /// <param name="QuickReplies">List of quick reply button definitions</param>
    public record PresentationResponse(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("quickReplies")] List<QuickReply> QuickReplies);

    /// <summary>
    /// LLM-generated rewrite produced by the ChannelAdapter prompt. Contains the degraded
    /// plain-text rendering of an outbound message and the (optionally surviving) quick replies
    /// that still fit inside the target channel's capabilities.
    /// </summary>
    /// <param name="Text">Rewritten message text, channel-compliant and free of unsupported features.</param>
    /// <param name="QuickReplies">Quick replies preserved by the rewrite, or null when the channel cannot carry them.</param>
    public record ChannelAdapterResponse(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("quickReplies")] List<QuickReply>? QuickReplies);

    /// <summary>
    /// Result returned by <see cref="Interfaces.IPresenterService.GenerateAsync"/> containing
    /// the welcome message and quick reply buttons for the start of a conversation.
    /// </summary>
    /// <param name="Message">
    /// Welcome/presentation message text to display to the user (2-4 sentences).
    /// </param>
    /// <param name="QuickReplies">
    /// List of quick reply buttons derived from displayable intents.
    /// May be empty if no intents are configured; never null.
    /// </param>
    /// <remarks>
    /// This record is the public contract of <see cref="Interfaces.IPresenterService"/> and is
    /// intentionally decoupled from <see cref="PresentationResponse"/>, which is an LLM
    /// wire-format DTO used only by <see cref="Services.LLMPresenterService"/> internally.
    /// </remarks>
    public record PresentationResult(
        string Message,
        List<QuickReply> QuickReplies);

    // ==========================================================================
    // PRESENTATION FLOW MESSAGES
    // ==========================================================================

    /// <summary>
    /// Trigger message to generate and send the initial presentation/welcome message.
    /// Sent automatically when a conversation is created.
    /// </summary>
    public record GeneratePresentationMessage;

    /// <summary>
    /// Context containing the generated presentation message and available intents.
    /// Used internally by ConversationSupervisorActor to send presentation via SignalR.
    /// </summary>
    /// <param name="Message">Welcome message text (either LLM-generated or fallback)</param>
    /// <param name="Intents">List of available intent definitions</param>
    public record PresentationContext(
        string Message,
        List<IntentDefinition> Intents)
    {
        /// <summary>
        /// LLM-generated quick replies (takes precedence over Intents if available).
        /// If null, quick replies are derived from Intents directly.
        /// </summary>
        public List<QuickReply>? LLMQuickReplies { get; init; }
    }

    // ==========================================================================
    // CONTEXT WRAPPERS FOR BECOME/PIPETO PATTERN
    // ==========================================================================
    // These records wrap async operation results with the original sender reference
    // to ensure correct message routing after async operations complete.

    // --- ConversationSupervisorActor Contexts ---

    /// <summary>
    /// Processing context maintained throughout the conversation pipeline.
    /// Captures the original message, sender, and classification result as it flows through states.
    /// </summary>
    /// <param name="OriginalMessage">The user message being processed</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationManagerActor)</param>
    /// <param name="Classification">Intent classification result (populated after ClassifierActor processes message)</param>
    /// <param name="TurnContext">OpenTelemetry activity context for the current turn span.</param>
    public record ProcessingContext(
        UserMessage OriginalMessage,
        IActorRef OriginalSender,
        ClassificationResult? Classification = null,
        ActivityContext TurnContext = default);

    // --- MorganaAgent Contexts ---

    /// <summary>
    /// Wraps an unhandled agent exception so the agent can route its error-handling
    /// through its own mailbox (Self.Tell) and still remember who to reply to.
    /// </summary>
    /// <param name="Failure">Akka.NET failure status carrying the original exception</param>
    /// <param name="OriginalSender">The actor that sent the AgentRequest (typically RouterActor)</param>
    public record FailureContext(
        Status.Failure Failure,
        IActorRef OriginalSender);

    // ==========================================================================
    // INTENT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Intent definition for classification and presentation.
    /// Defines what intents the system can recognize and how to present them to users.
    /// </summary>
    /// <param name="Name">Intent identifier (lowercase, e.g., "billing", "contract")</param>
    /// <param name="Description">Intent description for classifier LLM</param>
    /// <param name="Label">User-facing label with emoji (e.g., "📄 Billing") for quick replies</param>
    /// <param name="DefaultValue">Sample user message for this intent (used in quick reply value)</param>
    public record IntentDefinition(
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Description")] string Description,
        [property: JsonPropertyName("Label")] string? Label,
        [property: JsonPropertyName("DefaultValue")] string? DefaultValue = null);

    // ==========================================================================
    // INTENT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Collection of intent definitions with utility methods for classification and presentation.
    /// Provides filtering and formatting capabilities for different use cases.
    /// </summary>
    public record IntentCollection
    {
        /// <summary>
        /// List of all intent definitions.
        /// </summary>
        public List<IntentDefinition> Intents { get; set; }

        /// <summary>
        /// Initializes a new instance of IntentCollection.
        /// </summary>
        /// <param name="intents">List of intent definitions to wrap</param>
        public IntentCollection(List<IntentDefinition> intents)
        {
            Intents = intents;
        }

        /// <summary>
        /// Converts intents to name→description dictionary for ClassifierActor LLM prompt formatting.
        /// Format: "billing (description)|contract (description)". Returns new dictionary each call.
        /// </summary>
        public Dictionary<string, string> AsDictionary()
        {
            return Intents.ToDictionary(i => i.Name, i => i.Description);
        }

        /// <summary>
        /// Returns intents for presentation quick replies, excluding "other" fallback and intents
        /// without labels. Filters per UI displayability rules (non-user-selectable excluded).
        /// </summary>
        public List<IntentDefinition> GetDisplayableIntents()
        {
            return
            [
                .. Intents
                    .Where(i => !string.Equals(i.Name, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(i.Label))
            ];
        }
    }

    // ==========================================================================
    // PROMPT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Root collection of prompts loaded from configuration files (morgana.json, agents.json).
    /// Used during JSON deserialization.
    /// </summary>
    /// <param name="Prompts">Array of prompt definitions</param>
    public record PromptCollection(
        Prompt[] Prompts);

    /// <summary>
    /// Complete prompt definition (Target, Instructions, Personality, Formatting) with metadata and structured properties.
    /// Loaded from morgana.json (framework) or agents.json (domain, intent-keyed).
    /// </summary>
    /// <param name="ID">Prompt identifier: framework="Morgana"/"Classifier"/"Guard"/"Presentation", domain=intent name</param>
    /// <param name="Type">Prompt type category (e.g., "SYSTEM", "INTENT")</param>
    /// <param name="SubType">Prompt subtype (e.g., "AGENT", "ACTOR", "PRESENTATION")</param>
    /// <param name="Target">Core prompt text: role definition, capabilities statement, operational boundaries</param>
    /// <param name="Instructions">Behavioral rules, operational order, response constraints, tool-usage doctrine</param>
    /// <param name="Formatting">Output formatting rules: markdown usage, quick reply format, rich card rendering</param>
    /// <param name="Personality">Optional tone/character: formality, voice, domain-specific persona traits</param>
    /// <param name="Language">BCP 47 language code (e.g., "en-US", "it-IT")</param>
    /// <param name="Version">Prompt version string for tracking iteration history and regression detection</param>
    /// <param name="AdditionalProperties">List of structured properties: Tools, GlobalPolicies, ErrorAnswers, FallbackMessage, etc</param>
    public record Prompt(
        string ID,
        string Type,
        string SubType,
        string Target,
        string Instructions,
        string Formatting,
        string? Personality,
        string Language,
        string Version,
        List<Dictionary<string, object>> AdditionalProperties)
    {
        /// <summary>
        /// Gets additional property value (Tools, GlobalPolicies, ErrorAnswers, FallbackMessage, etc).
        /// Throws KeyNotFoundException if property not found. Deserializes JsonElement to type T.
        /// </summary>
        public T GetAdditionalProperty<T>(string additionalPropertyName)
        {
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties)
            {
                if (additionalProperties.TryGetValue(additionalPropertyName, out object value))
                {
                    JsonElement element = (JsonElement)value;
                    return element.Deserialize<T>();
                }
            }
            throw new KeyNotFoundException($"AdditionalProperty with key '{additionalPropertyName}' was not found in the prompt with id='{ID}'");
        }

        /// <summary>
        /// Gets an additional property, or <paramref name="defaultValue"/> when the prompt does not
        /// declare it. For optional configuration whose absence is a legitimate authoring choice
        /// rather than a defect — where <see cref="GetAdditionalProperty{T}"/> would rightly throw.
        /// </summary>
        /// <typeparam name="T">Type to deserialize the property value into</typeparam>
        /// <param name="additionalPropertyName">Name of the property to retrieve</param>
        /// <param name="defaultValue">Value returned when the property is absent</param>
        public T GetAdditionalPropertyOrDefault<T>(string additionalPropertyName, T defaultValue)
        {
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties)
            {
                if (additionalProperties.TryGetValue(additionalPropertyName, out object value))
                {
                    JsonElement element = (JsonElement)value;
                    return element.Deserialize<T>() ?? defaultValue;
                }
            }
            return defaultValue;
        }
    }

    /// <summary>
    /// Framework-level behavioral rule: Name, Description, Type (Critical or Injection), Priority (lower=higher).
    /// Critical policies render into system prompt. Injection templates splice into tool/parameter descriptions
    /// or per-turn context declaration (see Templates.ToolDescriptionContextGuidance, HeldContextDeclaration).
    /// MorganaAgentAdapter orders by Type then Priority.
    /// </summary>
    public record GlobalPolicy(
        string Name,
        string Description,
        string Type,
        int Priority)
    {
        /// <summary>
        /// <see cref="Type"/> value marking an injection template rather than a rendered policy.
        /// </summary>
        public const string InjectionType = "Injection";

        /// <summary>
        /// True when this entry is an injection template, spliced into tool/parameter descriptions
        /// instead of being rendered among the global policies.
        /// </summary>
        public bool IsInjectionTemplate
            => string.Equals(Type, InjectionType, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves an injection template's text by name (see <see cref="Constants.Injections"/>);
        /// returns an empty string when the prompt layer declares none, which every splice site
        /// reads as "inject nothing".
        /// </summary>
        public static string ResolveTemplate(IEnumerable<GlobalPolicy> policies, string name)
            => policies.FirstOrDefault(policy =>
                   string.Equals(policy.Name, name, StringComparison.OrdinalIgnoreCase))?.Description ?? "";
    }

    /// <summary>
    /// Error message template with named identifier.
    /// Used to provide consistent, user-friendly error messages across the system.
    /// </summary>
    /// <param name="Name">Error identifier (e.g., "GenericError", "LLMServiceError")</param>
    /// <param name="Content">Error message template (may contain placeholders like ((llm_error)))</param>
    public record ErrorAnswer(
        string Name,
        string Content);

    // ==========================================================================
    // TOOL CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Tool definition specifying a callable tool method with parameters.
    /// Loaded from agents.json and used by MorganaToolAdapter to create AIFunction instances.
    /// </summary>
    /// <param name="Name">Tool method name (must match actual method name in MorganaTool class)</param>
    /// <param name="Description">Tool description for LLM understanding</param>
    /// <param name="Parameters">List of tool parameter definitions</param>
    /// <param name="Reserved">
    /// True for the five morgana.json base tools (GetContextVariable, SetContextVariable,
    /// SetTurnContinuation, SetQuickReplies, SetRichCard). Never set from configuration: a domain
    /// tool declaring this in agents.json has it forced back to false by MorganaAgentAdapter —
    /// it is stamped true only where MorganaAgentAdapter reads morgana.json's own Tools array,
    /// so no JSON a plugin author writes can ever make it stick. Consumers (e.g. the reverse
    /// guard-rail wrapper) use it to skip tools whose output the framework itself controls.
    /// </param>
    public record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters,
        bool Reserved = false);

    /// <summary>
    /// Tool parameter: name (must match method param), description, Required flag. Scope: "context" (GetContextVariable)
    /// or "request" (user input). Shared: whether to persist in conversation-scoped shared_context registry for
    /// cross-agent hydration. Only applies when Scope="context". Default: false.
    /// </summary>
    public record ToolParameter(
        string Name,
        string Description,
        bool Required,
        string Scope,
        bool Shared = false);

    // ==========================================================================
    // MODEL CONTEXT PROTOCOL
    // ==========================================================================

    /// <summary>
    /// Declares the transport mechanism used to communicate with an MCP server.
    /// Used by <see cref="UsesMCPServerAttribute"/> to disambiguate
    /// between remote HTTP servers and local stdio process-based servers.
    /// </summary>
    public enum MCPTransport
    {
        /// <summary>
        /// HTTP or HTTPS transport. The MCP server is a remote process reachable via a URL.
        /// </summary>
        Http,

        /// <summary>
        /// Standard I/O transport. The MCP server is a local executable spawned as a child process.
        /// Communication happens via stdin/stdout streams.
        /// </summary>
        Stdio
    }
}