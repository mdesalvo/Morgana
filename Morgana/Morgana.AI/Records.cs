using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Attributes;
using Morgana.Contracts;

namespace Morgana.AI;

/// <summary>
/// Every immutable record, enum and small value helper that crosses a boundary: actor messages,
/// configuration bound from appsettings and the JSON that the models and the channels exchange.
/// Records are immutable so that actors can share them without copying; a distinct type per message is what routes it.
/// </summary>
public static class Records
{
    /// <summary>
    /// JSON options for every deserialization of model output and prompt configuration: tolerant of casing and property order, enums as camel-case names.
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
    /// Supervisor → Manager final response: text, classification, metadata, agent info, optional quick replies and rich card.
    /// AgentName names the answering agent (e.g., "Morgana (Billing)"); AgentCompleted flags that the multi-turn exchange is over.
    /// RecordedTimestamp is the instant an agent recorded this reply in its own session, so the reply pushed live
    /// and the same reply read back later are recognisably one. It is null when Morgana herself said the reply
    /// (a refusal, a disambiguation, a fallback): she is then the one who files and dates it.
    /// </summary>
    public record ConversationResponse(
        string Response,
        string? Classification,
        Dictionary<string, string>? Metadata,
        string? AgentName = null,
        bool AgentCompleted = false,
        List<QuickReply>? QuickReplies = null,
        DateTime? RecordedTimestamp = null,
        RichCard? RichCard = null);

    /// <summary>
    /// Request to start a new conversation: creates its supervisor and has Morgana present herself.
    /// The handshake is already settled on record by the time it is sent, so it carries none.
    /// </summary>
    /// <param name="ConversationId">Unique identifier for the new conversation</param>
    public record CreateConversation(
        string ConversationId);

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
    /// Where conversations are stored and the key that encrypts them: set under <c>Morgana:ConversationPersistence</c>.
    /// </summary>
    public record ConversationPersistenceOptions
    {
        /// <summary>
        /// Directory that holds one database file per conversation; created when missing.
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
    /// Asks the router for the agent that handles an intent, creating it when the conversation has none yet.
    /// </summary>
    /// <param name="AgentIntent">Intent name to resolve agent for</param>
    public record RestoreAgentRequest(string AgentIntent);

    /// <summary>
    /// The router's answer to a <see cref="RestoreAgentRequest"/>; a null reference means that no agent handles the intent.
    /// </summary>
    /// <param name="AgentIntent">Original intent requested</param>
    /// <param name="AgentRef">Resolved agent reference or null if not found</param>
    public record RestoreAgentResponse(string AgentIntent, IActorRef? AgentRef);

    // ==========================================================================
    // RATE LIMITING
    // ==========================================================================

    /// <summary>
    /// Limits on how many messages a conversation may send per minute, hour and day, with the message shown for each window.
    /// Set Enabled=false for development.
    /// </summary>
    public record RateLimitOptions
    {
        /// <summary>
        /// Master toggle: false disables every rate limit (useful for development and testing).
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
    /// The time windows a conversation's requests are counted over, each with its own cap and its own authored refusal.
    /// </summary>
    public enum RateLimitWindow
    {
        /// <summary>The last minute, capped by <c>MaxMessagesPerMinute</c>.</summary>
        PerMinute,

        /// <summary>The last hour, capped by <c>MaxMessagesPerHour</c>.</summary>
        PerHour,

        /// <summary>The last day, capped by <c>MaxMessagesPerDay</c>.</summary>
        PerDay
    }

    /// <summary>
    /// Result of a rate limit check operation.
    /// </summary>
    /// <param name="IsAllowed">Whether the request is allowed to proceed</param>
    /// <param name="ViolatedWindow">The window whose cap was reached (null if allowed): it picks the refusal the user reads.</param>
    /// <param name="ViolatedCap">The cap of that window (null if allowed), which the refusal may quote.</param>
    /// <param name="RetryAfterSeconds">Suggested wait time in seconds before retrying (null if allowed)</param>
    public record RateLimitResult(
        bool IsAllowed,
        RateLimitWindow? ViolatedWindow = null,
        int? ViolatedCap = null,
        int? RetryAfterSeconds = null)
    {
        /// <summary>The violated limit as the log and the client read it, named after its setting, such as <c>MaxMessagesPerMinute (10)</c>.</summary>
        public string? ViolatedLimit
            => ViolatedWindow is { } window ? $"MaxMessages{window} ({ViolatedCap})" : null;
    }

    // ==========================================================================
    // MAGIC DUST (TOKEN BUDGET)
    // ==========================================================================

    /// <summary>
    /// What one dust unit is worth on a tier: tokens per unit for each direction and the cost weights of cached input.
    /// A zero on either axis makes that direction free. One dust unit is worth $0.015, so a tier's tokens per unit
    /// are 0.015 divided by the model's price per token: recalibrate when the tier changes model.
    /// Check BudgetPerConversation against the heaviest agent's calls per turn in dust_usage_log, not against nominal turn counts.
    /// </summary>
    public record MagicDustPricing
    {
        /// <summary>Input tokens that equal one dust unit. 0 disables input charging.</summary>
        public int InputTokensPerDustUnit { get; set; }

        /// <summary>Output tokens that equal one dust unit. 0 disables output charging.</summary>
        public int OutputTokensPerDustUnit { get; set; }

        /// <summary>
        /// Cost weight applied to cache-read input tokens (<c>CachedInputTokenCount</c>)
        /// relative to a fresh input token: about 0.10 on most current models, 0.05 on some (Claude Opus 5.5, GPT-6.1 Sol).
        /// </summary>
        public double CachedInputWeight { get; set; }

        /// <summary>
        /// Cost weight applied to cache-creation input tokens
        /// (<c>AdditionalCounts[Constants.UsageCounts.CacheCreationInputTokens]</c>) relative to a fresh input
        /// token. Anthropic 1h cache write ≈ 2.0; providers with no separate write cost = 1.0.
        /// </summary>
        public double CacheCreationWeight { get; set; }
    }

    // ==========================================================================
    // LLM TIERS (MULTI-MODEL)
    // ==========================================================================

    /// <summary>
    /// Closed set of LLM tiers an agent can declare itself against via
    /// <see cref="Attributes.RequiresLLMTierAttribute"/>: an agent declares the tier its work needs
    /// and each tier is served by the provider and model that the deployment configures for it.
    /// </summary>
    public enum LLMTier
    {
        /// <summary>The cheapest model: routine work that needs no deliberate reasoning.</summary>
        Economy,

        /// <summary>The default for Morgana's own framework actors (Guard, Classifier, Presenter, ChannelAdapter) when <c>Morgana:ActorSystem:Tier</c> names none.</summary>
        Efficiency,

        /// <summary>The most capable model: reserved for agents whose domain author declares an existential need for deep reasoning.</summary>
        Performance
    }

    /// <summary>The LLM providers that a tier can be served by.</summary>
    public enum LLMProvider
    {
        /// <summary>Anthropic's Claude models.</summary>
        Anthropic,

        /// <summary>GPT models served by Azure OpenAI or Azure AI Foundry.</summary>
        AzureOpenAI,

        /// <summary>GPT models served by OpenAI.</summary>
        OpenAI,

        /// <summary>Local models served by Ollama.</summary>
        Ollama
    }

    /// <summary>The <c>Morgana:LLM</c> section: the three tiers, each served by its own provider.</summary>
    public record LLMConfiguration(LLMTiers Tiers);

    /// <summary>The definitions of the three tiers, all of them mandatory.</summary>
    public record LLMTiers(
        TierDefinition Economy,
        TierDefinition Efficiency,
        TierDefinition Performance)
    {
        /// <summary>Returns the definition declared for <paramref name="tier"/>.</summary>
        public TierDefinition For(LLMTier tier) => tier switch
        {
            // The cheapest tier takes the definition that the deployer declared for Economy.
            LLMTier.Economy => Economy,
            // The middle tier takes the definition that the deployer declared for Efficiency.
            LLMTier.Efficiency => Efficiency,
            // The most capable tier takes the definition that the deployer declared for Performance.
            LLMTier.Performance => Performance,
            // An unknown tier is refused, since every tier must be declared.
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
        };
    }

    /// <summary>
    /// One tier with its own provider, connection, model and dust pricing.
    /// Declared under Morgana:LLM:Tiers:{tier} as a JSON object keyed by name rather than an array, so that overrides of
    /// different configuration layers merge by key.
    /// </summary>
    public record TierDefinition(
        LLMProvider Provider,
        TierConnection Connection,
        TierConfiguration Options,
        MagicDustPricing MagicDust);

    /// <summary>
    /// How a tier reaches its provider. Each provider reads the fields it uses and ignores the others,
    /// which may therefore be left out of the configuration altogether.
    /// </summary>
    /// <param name="ApiKey">Credential of the provider account.</param>
    /// <param name="Endpoint">Address of the provider service where it is not fixed.</param>
    /// <param name="MaxRetries">Retries of a refused call.</param>
    /// <param name="TimeoutSeconds">Ceiling of one attempt.</param>
    public record TierConnection(
        string? ApiKey = null,
        string? Endpoint = null,
        int MaxRetries = 2,
        int TimeoutSeconds = 60);

    /// <summary>
    /// Where a tool parameter's value comes from, declared on the parameter with
    /// <see cref="Attributes.ToolParameterAttribute"/>.
    /// </summary>
    public enum ToolScope
    {
        /// <summary>Resolved by the framework from the session's context variables, never required of the model.</summary>
        Context,

        /// <summary>Obtained from the user, on the turn it is needed.</summary>
        Request
    }

    /// <summary>
    /// The part of <see cref="ChatOptions"/> that a deployer sets per tier: the model and the output ceiling, nothing else.
    /// The ceiling is mandatory on every tier: the Anthropic adapter falls back to 1024 when none is given
    /// and reasoning tokens count against it. Sampling, reasoning and stop sequences are per-call parameters.
    /// </summary>
    public record TierConfiguration(
        string ModelId,
        int MaxOutputTokens)
    {
        /// <summary>
        /// Builds the <see cref="ChatOptions"/> that <see cref="ChatClients.TierDefaultsChatClient"/> fills into every call on this tier.
        /// </summary>
        public ChatOptions ToChatOptions() => new()
        {
            ModelId = ModelId,
            MaxOutputTokens = MaxOutputTokens
        };
    }

    /// <summary>
    /// Lifetime dust budget of a conversation, with no window and no reset; independent of <see cref="RateLimitOptions"/>.
    /// The messages are set in Morgana:DustLimiting in the deployment's own voice.
    /// A percent placeholder in them stands for the dust remaining as a 0–100 integer, not as dust units.
    /// </summary>
    public record DustLimitingOptions
    {
        /// <summary>Master toggle. When false the limiter is fully bypassed (fail open).</summary>
        public bool Enabled { get; set; }

        /// <summary>Total dust a conversation may consume over its lifetime. Zero is a valid budget: spent from the start, so no turn or command is ever admitted.</summary>
        public double BudgetPerConversation { get; set; }

        /// <summary>One-shot advisory shown when consumption crosses 70%.</summary>
        public string Warning70Message { get; set; } = string.Empty;

        /// <summary>One-shot advisory shown when consumption crosses 90%.</summary>
        public string Warning90Message { get; set; } = string.Empty;

        /// <summary>Blocking message shown when the budget is exhausted (100%).</summary>
        public string ErrorMessage { get; set; } = string.Empty;
    }

    // ==========================================================================
    // AUTHENTICATION
    // ==========================================================================

    /// <summary>
    /// Per-channel trust model: each channel declares its own entry with its own signing key, so the
    /// blast radius of a leaked key is one channel. Tokens with an undeclared <c>iss</c> claim are refused.
    /// </summary>
    /// <remarks>
    /// This list holds channels and nothing else. A colleague reaching this installation over A2A is
    /// declared once, as a <see cref="PartnerOptions"/> entry carrying its key beside its reach, so
    /// what a caller is follows from the list it was written in rather than from a field it declares.
    /// </remarks>
    public record AuthenticationOptions
    {
        /// <summary>
        /// Channels this installation accepts tokens from, each with its own signing key. A token
        /// whose <c>iss</c> claim names none of them and no declared partner either is refused.
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
    /// One channel this installation serves. Binds the name Morgana expects in the JWT <c>iss</c>
    /// claim to the key that channel signs with.
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
    /// One installation this one federates with, in whichever direction it federates.
    /// </summary>
    /// <remarks>
    /// A partner is declared once and the entry answers every question about it: who it is, where it
    /// answers, the secret the two sides share and what each direction is allowed. Both directions are
    /// off until a policy says otherwise, so an entry alone grants nothing.
    /// <para>An entry describes an installation, not an agent: declaring one opens as many colleagues
    /// as it serves. A partner need not be another Morgana, nor somebody else's.</para>
    /// </remarks>
    public record PartnerOptions
    {
        /// <summary>
        /// Name this partner is known by here: what <c>[ConsultsAgent]</c> writes to reach its agents
        /// and, unless a policy overrides it, the <c>iss</c> claim its own calls must arrive under.
        /// Never a hostname — an attribute names whose agent is being called, while where that agent
        /// runs is deployment. <c>morgana</c> is reserved for this installation's own agents.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Absolute address the partner answers on — everything before the published agent path,
        /// which is appended from the intent being consulted. Required only to call it.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// The secret the two installations share (HMAC-SHA256, at least 256 bits): calls made to this
        /// partner are signed with it and calls arriving from it are proven against it. One key per
        /// partner, so what a leak costs is this relationship and no other.
        /// </summary>
        public string SymmetricKey { get; set; } = string.Empty;

        /// <summary>
        /// Whether this partner exists at all. Set to false to park a relationship — its agents become
        /// unreachable and its calls are refused — without deleting the declaration that describes it.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Whether and how this installation may call the partner. Absent means it may not.</summary>
        public PartnerOutboundPolicy? OutboundPolicy { get; set; }

        /// <summary>Whether and how far the partner may call this installation. Absent means it may not.</summary>
        public PartnerInboundPolicy? InboundPolicy { get; set; }
    }

    /// <summary>
    /// What this installation may do toward a partner: consult its published agents or nothing.
    /// </summary>
    public record PartnerOutboundPolicy
    {
        /// <summary>
        /// Whether agents here may consult that partner's. Off, a <c>[ConsultsAgent]</c> naming it is
        /// refused at startup rather than failing on the first conversation.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The name that partner knows this installation by, which its own declaration of this
        /// relationship carries and which its gate expects in the <c>iss</c> claim.
        /// </summary>
        /// <remarks>
        /// Required whenever the partner demands a bearer and it cannot come from its card: an
        /// issuer is the name a publisher filed one caller under, so a document served to everyone
        /// cannot state it. It travels out of band with the key it goes with. Left unset only for a
        /// partner whose card requires no credentials at all, which is called bare.
        /// </remarks>
        public string? Issuer { get; set; }

        /// <summary>
        /// Audience that partner validates a token against, when it is not the one this installation
        /// uses itself. Left unset in the ordinary case: two installations of Morgana share the
        /// shipped default until one of them deliberately changes it.
        /// </summary>
        public string? Audience { get; set; }
    }

    /// <summary>
    /// What a partner may do toward this installation: which agents it reaches and how often it may
    /// open a new exchange at them.
    /// </summary>
    public record PartnerInboundPolicy
    {
        /// <summary>
        /// Whether the partner is admitted at all. Off, its key proves who it is and opens nothing,
        /// which is what a purely outbound relationship looks like.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Name its calls actually arrive under, when the partner signs under something other than the
        /// name this installation knows it by. Left unset in the ordinary case.
        /// </summary>
        public string? Issuer { get; set; }

        /// <summary>
        /// Published agents this partner may consult or <c>null</c> to admit it to every one of them.
        /// This is how one company's several installations reach only the agents that concern them:
        /// publication stays whole and what narrows is admission.
        /// </summary>
        public List<string>? OnAgents { get; set; }

        /// <summary>
        /// How often the partner may open an exchange here. Required whenever it is admitted, since
        /// nothing reads an absent declaration as licence to spend without limit.
        /// </summary>
        public PartnerRateLimitingOptions? RateLimiting { get; set; }
    }

    /// <summary>
    /// The ceiling on how many conversations one admitted partner may open within a sliding hour.
    /// </summary>
    /// <remarks>
    /// Behind the A2A door the caller writes the name of the conversation it is served on, so a
    /// partner rotating names would draw a fresh per-conversation budget with every one. What is
    /// bounded here is therefore not a second measure of spend but how many exchanges may start; the
    /// ceiling on spend follows as admissions times the budget each one carries.
    /// </remarks>
    public record PartnerRateLimitingOptions
    {
        /// <summary>
        /// Whether the ceiling applies. Off is a deployment saying in as many words that this partner
        /// is trusted to open what it likes — which is a sentence somebody wrote, unlike an omission.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>Conversations the partner may open within a sliding hour. Required when the ceiling applies.</summary>
        public int? MaxConversationsPerHour { get; set; }

        /// <summary>
        /// What the partner is told when it has opened all it may. Reaches the asking agent as the
        /// colleague's own answer, so it reads as an agent that cannot take the question rather than
        /// as an error code.
        /// </summary>
        public string ErrorMessagePerHour { get; set; } =
            "This agent cannot take on further conversations right now.";
    }

    /// <summary>
    /// Result of a token authentication operation.
    /// </summary>
    /// <param name="IsAuthenticated">Whether the token was successfully validated</param>
    /// <param name="CallerId">The caller's unique identifier (from the <c>sub</c> claim), null if not authenticated</param>
    /// <param name="DisplayName">The caller's display name (from the <c>name</c> claim), null if not authenticated</param>
    /// <param name="Error">Description of why authentication failed, null if authenticated</param>
    /// <param name="Issuer">The validated <c>iss</c>, set only on success. Names which door the
    /// credential was cut for, so a gate can admit some issuers and not others.</param>
    /// <param name="IsPartner">Whether the key that proved this caller was a partner's rather than a
    /// channel's. Not a role the caller declared but which list it was found in: the conversation API
    /// serves channels while the A2A door serves partners and neither will take the other's key.</param>
    public record AuthenticationResult(
        bool IsAuthenticated,
        string? CallerId = null,
        string? DisplayName = null,
        string? Error = null,
        string? Issuer = null,
        bool IsPartner = false);

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
    /// <param name="Intent">Candidate intent name (e.g., "billing", "contract", or
    /// <see cref="Constants.Intents.Other"/>).</param>
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
    /// Request to an agent: the user's Content (null for a tool-only request), the Classification (null on a follow-up to the active agent),
    /// the TurnContext of the OTel span and the channel Capabilities (null means a channel without limits).
    /// </summary>
    public record AgentRequest(
        string ConversationId,
        string? Content,
        ClassificationResult? Classification,
        ActivityContext TurnContext = default,
        ChannelCapabilities? Capabilities = null,
        bool ContentAlreadyStored = false);

    /// <summary>
    /// An agent's answer: text, IsCompleted (true returns the conversation to Morgana; false keeps the agent active),
    /// optional QuickReplies and an optional RichCard for structured data.
    /// RecordedTimestamp is the timestamp that the agent's session keeps this reply under, so the history and the live push
    /// date it identically; null when nothing was recorded.
    /// </summary>
    public record AgentResponse(
        string Response,
        bool IsCompleted = true,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null,
        DateTime? RecordedTimestamp = null);

    /// <summary>
    /// The router's reply to a turn: the agent's response with a reference to the agent that wrote it,
    /// from which the supervisor knows which agent stays active.
    /// </summary>
    /// <param name="Response">Agent's response text</param>
    /// <param name="IsCompleted">Whether the agent has completed its task</param>
    /// <param name="AgentRef">Actor reference to the agent that generated this response</param>
    /// <param name="QuickReplies">Optional list of quick reply buttons from the agent</param>
    /// <param name="RichCard">Optional rich card from the agent</param>
    /// <param name="RecordedTimestamp">The timestamp the agent's session keeps this reply under</param>
    public record ActiveAgentResponse(
        string Response,
        bool IsCompleted,
        IActorRef AgentRef,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null,
        DateTime? RecordedTimestamp = null);

    /// <summary>
    /// One piece of the text that an agent is streaming, sent as it is written.
    /// </summary>
    public record AgentStreamChunk(
        string Text);

    /// <summary>
    /// Reports that an agent's turn is advancing on something the user cannot see — a tool running,
    /// a colleague being consulted. It carries nothing and never reaches the channel.
    /// </summary>
    /// <remarks>
    /// The supervisor's wait on an agent is a budget on silence and is renewed by every streamed
    /// chunk, so a turn that keeps writing is never cut short. Work producing no text would
    /// otherwise be indistinguishable from an agent that has died and the longest such work — a
    /// consultation, which is a whole turn at another agent — is precisely the one most likely to
    /// outlast the budget.
    /// </remarks>
    public record AgentStillWorking;

    // ==========================================================================
    // PEER CONSULTATION MODELS
    // ==========================================================================

    /// <summary>
    /// How long each party to a consultation waits, as one ladder derived from the pipeline's own
    /// turn budget.
    /// </summary>
    /// <remarks>
    /// The parties cannot see each other, so the order is what makes a failure legible instead of
    /// merely loud: the colleague gives up first and what comes back to the asking model is its
    /// envelope; the asking agent gives up next and still has a turn left to answer the user in;
    /// only then would the supervisor conclude nobody is coming. Stated once here because a party
    /// deriving its own wait from the same setting would be free to derive it differently.
    /// </remarks>
    /// <param name="Turn">The pipeline's budget on one turn and the outermost of the three.</param>
    /// <param name="Caller">Wait of the agent asking, on the wire.</param>
    /// <param name="Callee">Wait of the installation answering, on the actor that serves it.</param>
    public record PeerConsultationWaits(TimeSpan Turn, TimeSpan Caller, TimeSpan Callee)
    {
        /// <summary>Share of the turn left to the asking agent, the rest being what the turn keeps to answer with.</summary>
        private const double CallerShareOfTurn = 0.9;

        /// <summary>Share of the turn left to the answering installation, one step further in.</summary>
        private const double CalleeShareOfTurn = 0.8;

        /// <summary>
        /// Reads the ladder off the pipeline's turn budget, which is the only thing configured: the
        /// steps are proportions rather than subtracted seconds, so shortening the turn cannot
        /// invert the order or leave a party waiting for no time at all.
        /// </summary>
        /// <param name="configuration">Application configuration, read for the turn budget.</param>
        public static PeerConsultationWaits From(IConfiguration configuration)
        {
            // The pipeline's turn budget is the single configured figure; 180 seconds applies where a deployment sets none.
            TimeSpan turn = TimeSpan.FromSeconds(configuration.GetValue("Morgana:ActorSystem:TimeoutSeconds", 180));

            // Caller and callee shares are strictly below the turn and ordered, so the colleague gives up first and the supervisor last.
            return new PeerConsultationWaits(turn, turn * CallerShareOfTurn, turn * CalleeShareOfTurn);
        }
    }

    /// <summary>
    /// Names one consultable colleague.
    /// </summary>
    /// <param name="Intent">Intent the colleague handles, as its own installation publishes it.</param>
    /// <param name="Instance">Partner publishing it, declared in <c>Morgana:AgentToAgent:Partners</c>;
    /// <c>null</c> for an agent of this one. Two colleagues handling the same intent at two
    /// installations are two colleagues, which is why the pair and not the intent is the name.</param>
    public record PeerReference(string Intent, string? Instance = null);

    /// <summary>
    /// Whether a partner may open one more exchange here and what it is told when it may not.
    /// </summary>
    /// <param name="IsAdmitted">Whether the conversation may be opened.</param>
    /// <param name="RefusalMessage">What the partner reads instead, set only on a refusal. Written by
    /// the deployment on that partner's own entry, so a turned-away colleague answers in this
    /// installation's voice rather than with a status code the asking model would narrate.</param>
    public record PeerAdmissionResult(bool IsAdmitted, string? RefusalMessage = null);

    /// <summary>
    /// Question one agent puts to a colleague of the same conversation, sent to the colleague's
    /// actor and answered with a <see cref="PeerConsultationResponse"/>.
    /// </summary>
    /// <param name="ConversationId">Conversation both agents belong to; scopes session and shared context.</param>
    /// <param name="CallerIntent">Intent of the asking agent or <c>null</c> when the A2A caller is
    /// not an agent of this installation and named none.</param>
    /// <param name="Question">The colleague's question, already carrying the declaration spliced in
    /// front of it, since the answering agent's prompt says nothing about serving a colleague.</param>
    /// <param name="TurnContext">OTel context of the user turn that triggered the consultation.</param>
    public record PeerConsultation(
        string ConversationId,
        string? CallerIntent,
        string Question,
        ActivityContext TurnContext = default);

    /// <summary>
    /// A colleague's answer, both as the actor reply and — serialized — as the tool result the
    /// asking agent's model reads, which is why every member carries an explicit JSON name.
    /// </summary>
    /// <param name="Answer">What the colleague answers, as text.</param>
    /// <param name="ColleagueAwaitsYourReply">True when the colleague is waiting, i.e. the exchange is unfinished.</param>
    /// <param name="Options">Options offered, as data to choose from — never buttons to render.</param>
    /// <param name="Card">Structured data presented, as data to read — never a card to render.</param>
    /// <param name="DustConsumed">What answering cost the colleague, in its own dust. Set only when the
    /// caller declared itself an agent of a Morgana, since nothing else knows what to do with it and
    /// removed by the asking side before the envelope reaches a model.</param>
    public record PeerConsultationResponse(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("colleagueAwaitsYourReply")] bool ColleagueAwaitsYourReply,
        [property: JsonPropertyName("options")] List<QuickReply>? Options = null,
        [property: JsonPropertyName("card")] RichCard? Card = null,
        [property: JsonPropertyName("dustConsumed")] double? DustConsumed = null);

    /// <summary>
    /// The welcome message and quick replies as the presentation prompt returns them.
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
    /// <param name="QuickReplies">Quick replies preserved by the rewrite or null when the channel cannot carry them.</param>
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
    /// Tells the supervisor to generate and send the welcome message; sent when a conversation is created.
    /// </summary>
    public record GeneratePresentationMessage;

    /// <summary>
    /// The welcome message together with the intents it can offer, carried to the point where the supervisor sends it.
    /// </summary>
    /// <param name="Message">Welcome message text (either LLM-generated or fallback)</param>
    /// <param name="Intents">List of available intent definitions</param>
    public record PresentationContext(
        string Message,
        List<IntentDefinition> Intents)
    {
        /// <summary>
        /// Quick replies written by the model; they take precedence over the Intents, from which the replies are derived when this is null.
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
    /// Captures the original message, sender and classification result as it flows through states.
    /// </summary>
    /// <param name="OriginalMessage">The user message being processed</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationManagerActor)</param>
    /// <param name="ChannelCapabilities">The channel's budget for this turn, stamped on every agent request it sends</param>
    /// <param name="Classification">Intent classification result (populated after ClassifierActor processes message)</param>
    /// <param name="TurnContext">OpenTelemetry activity context for the current turn span.</param>
    /// <param name="UserMessageAlreadyStored">
    /// True when no agent was active as this turn arrived, which is when Morgana saves the phrase on
    /// her own side of the conversation. Settled once at ingress, then carried through the turn: an
    /// agent engaged a moment later would otherwise make the answer unreadable, since by then one is
    /// active although none was when the user spoke.
    /// </param>
    public record ProcessingContext(
        UserMessage OriginalMessage,
        IActorRef OriginalSender,
        ChannelCapabilities ChannelCapabilities,
        ClassificationResult? Classification = null,
        ActivityContext TurnContext = default,
        bool UserMessageAlreadyStored = false);

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
    /// <param name="Label">User-facing label with emoji (e.g., "📄 Billing") for quick replies; required at startup.</param>
    /// <param name="DefaultValue">Sample user message for this intent (used in quick reply value); required at startup.</param>
    public record IntentDefinition(
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Description")] string Description,
        [property: JsonPropertyName("Label")] string Label,
        [property: JsonPropertyName("DefaultValue")] string DefaultValue);

    /// <summary>
    /// The configured intents, with the views that classification and presentation each need.
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
        /// Maps each intent name to its description, as the classifier's prompt lists them; a new dictionary on every call.
        /// </summary>
        public Dictionary<string, string> AsDictionary()
        {
            // Names are unique across the merged configuration (startup validation), so the dictionary cannot hit a duplicate key.
            return Intents.ToDictionary(i => i.Name, i => i.Description);
        }

        /// <summary>
        /// Returns the intents that a user can pick from the welcome quick replies: every one but <see cref="Constants.Intents.Other"/>.
        /// </summary>
        public List<IntentDefinition> GetDisplayableIntents()
        {
            // Other is the classifier's complement: no agent stands behind it, so it cannot be offered.
            return
            [
                .. Intents
                    .Where(i => !string.Equals(i.Name, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase))
            ];
        }
    }

    // ==========================================================================
    // PROMPT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Root of a prompt configuration file (morgana.json or agents.json).
    /// </summary>
    /// <param name="Prompts">Array of prompt definitions</param>
    public record PromptCollection(
        Prompt[] Prompts);

    /// <summary>
    /// One prompt as authored: its four sections with the territory and the structured properties.
    /// Framework prompts come from morgana.json; domain prompts come from agents.json keyed by intent.
    /// </summary>
    /// <param name="ID">Prompt identifier: framework="Morgana"/"Classifier"/"Guard"/"Presentation", domain=intent name</param>
    /// <param name="Target">Core prompt text: role definition, capabilities statement, operational boundaries</param>
    /// <param name="Instructions">Behavioral rules, operational order, response constraints, tool-usage doctrine</param>
    /// <param name="Formatting">Output formatting rules: markdown usage, quick reply format, rich card rendering</param>
    /// <param name="Personality">Optional tone/character: formality, voice, domain-specific persona traits</param>
    /// <param name="Territory">What falls to this agent, addressed to a colleague who might consult it</param>
    /// <param name="Language">BCP 47 language code (e.g., "en-US", "it-IT")</param>
    /// <param name="Version">Prompt version string for tracking iteration history and regression detection</param>
    public record Prompt(
        string ID,
        string Target,
        string Instructions,
        string Formatting,
        string? Personality,
        string? Territory,
        string Language,
        string Version)
    {
        /// <summary>
        /// Structured properties of the prompt (GlobalPolicies, Messages and the like); empty when the prompt declares none.
        /// </summary>
        public List<Dictionary<string, object>> AdditionalProperties { get; init; } = [];

        /// <summary>
        /// Gets the array that the prompt declares under <paramref name="additionalPropertyName"/>; throws when it declares none or declares it null.
        /// </summary>
        public T GetAdditionalProperty<T>(string additionalPropertyName)
        {
            // Each block of additional properties the prompt declares is searched in turn.
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties)
            {
                // The first block that declares the property supplies its value.
                if (additionalProperties.TryGetValue(additionalPropertyName, out object? value))
                {
                    // The configuration binder leaves every value as a JsonElement, which is read into the type the caller expects.
                    // A property declared null is as much an authoring defect as one not declared at all.
                    JsonElement element = (JsonElement)value!;
                    return element.Deserialize<T>()
                        ?? throw new InvalidOperationException($"AdditionalProperty with key '{additionalPropertyName}' is null in the prompt with id='{ID}'");
                }
            }

            // A prompt without the property it was asked for is an authoring defect, so it is reported rather than defaulted.
            throw new KeyNotFoundException($"AdditionalProperty with key '{additionalPropertyName}' was not found in the prompt with id='{ID}'");
        }

        /// <summary>
        /// Puts a section's label in front of its text, as every composed prompt shows it (see
        /// <see cref="Constants.SectionLabels"/>); empty for a section that says nothing.
        /// </summary>
        /// <param name="label">The section's label.</param>
        /// <param name="text">The section as authored.</param>
        public static string Labeled(string label, string? text)
        {
            // A section that says nothing is left out altogether, so no bare label reaches the model.
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // A plugin's agents.json may still open a section with its own label: it is kept once, never doubled.
            string trimmed = text.Trim();
            return trimmed.StartsWith(label, StringComparison.Ordinal) ? trimmed : $"{label} {trimmed}";
        }

        /// <summary>
        /// Gets a text that the framework says to the user in its own voice, from the prompt's
        /// <c>Messages</c> section (see <see cref="Constants.Messages"/>).
        /// </summary>
        /// <param name="name">Which message.</param>
        /// <returns>The authored text; empty when the prompt declares no such message.</returns>
        public string GetMessage(string name)
            => GetAdditionalPropertyOrDefault<List<Message>>(Constants.PromptProperties.Messages, [])
                .FirstOrDefault(message => string.Equals(message.Name, name, StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;

        /// <summary>
        /// Gets an additional property or <paramref name="defaultValue"/> when the prompt does not declare it:
        /// for optional configuration, where <see cref="GetAdditionalProperty{T}"/> would rightly throw.
        /// </summary>
        /// <typeparam name="T">Type to deserialize the property value into</typeparam>
        /// <param name="additionalPropertyName">Name of the property to retrieve</param>
        /// <param name="defaultValue">Value returned when the property is absent</param>
        public T GetAdditionalPropertyOrDefault<T>(string additionalPropertyName, T defaultValue)
        {
            // Each block of additional properties the prompt declares is searched in turn.
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties)
            {
                // The first block that declares the property supplies its value and a block without it is passed over.
                if (additionalProperties.TryGetValue(additionalPropertyName, out object? value))
                {
                    // The configuration binder leaves every value as a JsonElement; a declared null reads as absent.
                    JsonElement element = (JsonElement)value!;
                    return element.Deserialize<T>() ?? defaultValue;
                }
            }

            // The prompt does not declare the property: that is an authoring choice and not a defect.
            return defaultValue;
        }
    }

    /// <summary>
    /// One framework-level rule, rendered among the global policies of every agent's system prompt.
    /// Every policy binds equally, so the only thing left to declare about one is where it is read:
    /// <c>Priority</c> orders the block, lowest first.
    /// </summary>
    public record GlobalPolicy(
        string Name,
        string Description,
        int Priority);

    /// <summary>A text that the framework says to the user in its own voice, fetched by name (see <see cref="Constants.Messages"/>).</summary>
    /// <param name="Name">Identifier the framework fetches the text by.</param>
    /// <param name="Content">The text as the user reads it.</param>
    public record Message(
        string Name,
        string Content);

    /// <summary>
    /// A text of the framework prompt that the model reads, authored in morgana.json and fetched by name
    /// (see <see cref="Constants.PromptInjections"/> and <see cref="Constants.ToolInjections"/>).
    /// Domain tools return their own text, which Morgana never authors.
    /// </summary>
    /// <remarks>
    /// What an entry is follows from the array it lives in: a <c>PromptInjections</c> entry is spliced into
    /// the instructions or the conversation's messages, a <c>ToolInjections</c> entry is read as part of a tool.
    /// </remarks>
    /// <param name="Name">Identifier the framework fetches the text by.</param>
    /// <param name="Content">The text, carrying its values as <c>((…))</c> placeholders.</param>
    public record Injection(
        string Name,
        string Content)
    {
        /// <summary>
        /// Resolves a prompt injection's text by name; returns an empty string when the prompt layer
        /// declares none, which every splice site reads as "inject nothing".
        /// </summary>
        public static string ResolveTemplate(IEnumerable<Injection> injections, string name)
            => injections.FirstOrDefault(injection =>
                   string.Equals(injection.Name, name, StringComparison.OrdinalIgnoreCase))?.Content ?? "";

        /// <summary>
        /// Resolves a tool injection by name with its values spliced in; the bare name when the prompt layer
        /// declares none, so a missing entry shows up in the transcript instead of failing the turn.
        /// </summary>
        /// <param name="injections">The prompt layer's tool injections.</param>
        /// <param name="name">Which injection.</param>
        /// <param name="values">Placeholder (see <see cref="Constants.Placeholders"/>) to the value that it stands for.</param>
        public static string Resolve(IEnumerable<Injection> injections, string name, IReadOnlyDictionary<string, string>? values = null)
        {
            // The bare name stands in for a missing entry, so the gap is visible in the transcript.
            string content = injections.FirstOrDefault(injection =>
                string.Equals(injection.Name, name, StringComparison.OrdinalIgnoreCase))?.Content ?? name;

            // Each placeholder is replaced literally: the values come from the turn and must never be read as patterns.
            foreach ((string placeholder, string value) in values ?? new Dictionary<string, string>())
                content = content.Replace(placeholder, value, StringComparison.Ordinal);

            // The text the model reads as the tool's result.
            return content;
        }
    }

    /// <summary>
    /// A framework tool's result named rather than written: the tool loop turns it into the authored text
    /// of a <see cref="Injection"/> of <c>ToolInjections</c>, which a tool holding no prompt layer cannot reach itself.
    /// </summary>
    /// <param name="Name">Which result (see <see cref="Constants.ToolInjections"/>).</param>
    /// <param name="Values">Placeholder to the value that it stands for, if the text carries any.</param>
    public record FrameworkToolResult(
        string Name,
        IReadOnlyDictionary<string, string>? Values = null);

    // ==========================================================================
    // TOOL CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// A callable tool as the model is offered it, projected from the tool's method: on the
    /// <see cref="Abstractions.MorganaTool"/> subclass for a native domain tool and on <c>ReplyTool</c> for the framework's own.
    /// </summary>
    /// <param name="Name">Tool method name (the actual method name in the MorganaTool class)</param>
    /// <param name="Description">Tool description for LLM understanding</param>
    /// <param name="Parameters">List of tool parameter definitions</param>
    /// <param name="Returns">
    /// The fields of the record that the tool method returns, projected from that record.
    /// Present for every native domain tool; for a tool acquired over MCP it is projected from the server's
    /// output schema when the server declares one and absent otherwise; absent for the base tool.
    /// </param>
    /// <param name="RequiresExecutionApproval">
    /// True when the tool changes something real and runs only once the user has approved that exact
    /// call. Declared with <see cref="Attributes.RequiresApprovalAttribute"/>; the approval itself is
    /// Microsoft.Extensions.AI's, through <c>ApprovalRequiredAIFunction</c>.
    /// </param>
    /// <param name="Reserved">
    /// True for the framework's base tool (Reply) alone: the projection of a domain tool's class leaves it false.
    /// Consumers skip a reserved tool because the framework controls its output.
    /// </param>
    public record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters,
        bool Reserved = false,
        bool RequiresExecutionApproval = false,
        IReadOnlyList<ToolReturn>? Returns = null);

    /// <summary>
    /// One parameter of a tool, projected from the method parameter, its <c>[Description]</c> and its <see cref="Attributes.ToolParameterAttribute"/>.
    /// Required is the signature's (no default value). Scope is "context" (resolved by the framework from the session and never required of the model)
    /// or "request" (asked of the user). Shared, valid for a context parameter only, routes the value into the conversation's shared_context registry for the other agents.
    /// </summary>
    public record ToolParameter(
        string Name,
        string Description,
        bool Required,
        string Scope,
        bool Shared = false);

    /// <summary>
    /// One field of what a tool returns: its name, what it holds and whether holding a value means
    /// the call failed. Projected from the returned record's properties and their <c>[Description]</c>:
    /// the failure marker is the nullable property serialized as <see cref="Constants.Workflows.FailureField"/>.
    /// </summary>
    public record ToolReturn(
        string Name,
        string Description,
        bool Failure = false);

    // ==========================================================================
    // WORKFLOWS
    // ==========================================================================

    /// <summary>
    /// A procedure of one agent whose steps are kept in order by the framework: declared by a
    /// <c>MorganaWorkflow</c> class beside the agent's tools and run by the workflow engine.
    /// </summary>
    /// <param name="Name">What names the function that starts it, <c>Start{Name}</c>; unique per agent.</param>
    /// <param name="Description">What the procedure does, offered to the model beside its name.</param>
    /// <param name="Steps">The steps of the procedure; the first one is where it starts.</param>
    /// <param name="Edges">Every transition of the procedure, in the order the class declared them.</param>
    /// <param name="Parameters">The public instance properties that the class declares itself: the values that edges may carry.</param>
    public record WorkflowDefinition(
        string Name,
        string Description,
        IReadOnlyList<WorkflowStep> Steps,
        IReadOnlyList<WorkflowEdge> Edges,
        IReadOnlyList<string> Parameters)
    {
        /// <summary>Every tool that a step of the workflow names.</summary>
        public HashSet<string> ToolSignature()
            => [.. Steps.SelectMany(step => step.Tools)];

        /// <summary>The tools of the first step: the procedure is entered through its launcher alone, so they are never offered or run outside it.</summary>
        public IReadOnlyList<string> EntryTools
            => Steps is { Count: > 0 } ? Steps[0].Tools : [];
    }

    /// <summary>
    /// One step of a workflow: the tools the agent may call while the workflow stands at it.
    /// </summary>
    /// <param name="Name">Unique within the workflow.</param>
    /// <param name="Tools">The tools offered at this step; with several, the tool called decides the branch.</param>
    public record WorkflowStep(
        string Name,
        IReadOnlyList<string> Tools);

    /// <summary>
    /// One transition of a workflow: followed when the tool called at the source step ends with the outcome
    /// that <paramref name="OnFailure"/> names.
    /// </summary>
    /// <param name="Source">The step the call is made at.</param>
    /// <param name="Target">The step the workflow moves to.</param>
    /// <param name="Tool">The tool whose outcome decides the transition.</param>
    /// <param name="OnFailure">True when the edge follows a failed call; false when it follows a successful one.</param>
    /// <param name="Carrying">The workflow's properties whose values the target step takes by name from that call's result.</param>
    public record WorkflowEdge(
        string Source,
        string Target,
        string Tool,
        bool OnFailure,
        IReadOnlyList<string> Carrying);

    /// <summary>
    /// What the engine asks of the agent at a step: the step itself and the parameters already bound for it.
    /// </summary>
    /// <param name="Step">The step the workflow has reached.</param>
    /// <param name="Arguments">Parameter name to the JSON text of the value the framework binds.</param>
    public record StepPrompt(
        string Step,
        IReadOnlyDictionary<string, string> Arguments);

    /// <summary>
    /// What the agent answers at a step: the tool it called and how that call ended.
    /// </summary>
    /// <param name="Tool">The tool that was called.</param>
    /// <param name="Failed">True when the call failed: its failure field held a value or, for an MCP tool, the server reported an error.</param>
    /// <param name="FieldsJson">
    /// The JSON text of the object that holds the result's fields: the returned record for a native tool, the
    /// structured content for an MCP tool. <c>null</c> when the result holds none.
    /// </param>
    public record StepOutcome(
        string Tool,
        bool Failed,
        string? FieldsJson);

    /// <summary>
    /// Where a running workflow stands, kept in the agent's session so that it survives the turn and a restart.
    /// </summary>
    /// <param name="Workflow">The workflow that runs.</param>
    /// <param name="Step">The step it stands at.</param>
    /// <param name="Arguments">Parameter name to the JSON text of the value bound for this step.</param>
    /// <param name="Checkpoint">The latest checkpoint of the engine, from which every call rebuilds it.</param>
    public record WorkflowPosition(
        string Workflow,
        string Step,
        IReadOnlyDictionary<string, string> Arguments,
        string Checkpoint)
    {
        /// <summary>
        /// Finds the workflow and the step this position names among the agent's declarations.
        /// </summary>
        /// <param name="workflows">The workflows the agent declares.</param>
        /// <returns>Both of them; <c>null</c> when the declarations no longer hold either, which is a position nothing can serve.</returns>
        public (WorkflowDefinition Definition, WorkflowStep Step)? Resolve(IEnumerable<WorkflowDefinition> workflows)
        {
            // The agent may have been redeployed with other workflows since the position was saved, so either name can have vanished.
            WorkflowDefinition? definition = workflows.FirstOrDefault(candidate => string.Equals(candidate.Name, Workflow, StringComparison.Ordinal));
            WorkflowStep? step = definition?.Steps.FirstOrDefault(candidate => string.Equals(candidate.Name, Step, StringComparison.Ordinal));

            // A position that names neither is handed back as unresolved for the caller to drop.
            return definition is null || step is null ? null : (definition, step);
        }
    }

    // ==========================================================================
    // TURN CLOSURE
    // ==========================================================================

    /// <summary>
    /// What a turn ends waiting for from the user, declared through the Reply tool. Anything but
    /// <see cref="Nothing"/> keeps the agent in service for the user's next message.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AwaitedFromUser>))]
    public enum AwaitedFromUser
    {
        /// <summary>The request is answered: the conversation may return to Morgana.</summary>
        [JsonStringEnumMemberName("nothing")] Nothing,

        /// <summary>A value the user types in their own words: a code, a quantity, a name.</summary>
        [JsonStringEnumMemberName("typed_answer")] TypedAnswer,

        /// <summary>One of the actions that the turn offers as buttons.</summary>
        [JsonStringEnumMemberName("action_choice")] ActionChoice
    }

    /// <summary>
    /// One action that a turn offers the user as a button, naming the agent's own tool that carries it out.
    /// </summary>
    /// <param name="Tool">The agent's tool the action leads to.</param>
    /// <param name="Label">What the button shows.</param>
    /// <param name="Value">The message sent on the user's behalf when the button is pressed.</param>
    public record ReplyAction(
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("value")] string Value);

    /// <summary>
    /// The two pairs of buttons the framework adds to an agent's turn so the user can stay or leave,
    /// authored in morgana.json. Never chosen by the model: which pair a turn gets follows from its closure.
    /// </summary>
    /// <param name="Closure">Offered when the turn answered the request and awaits nothing.</param>
    /// <param name="Escape">Appended to the actions that a turn offers, so the user is never trapped in them.</param>
    /// <param name="Approval">
    /// Offered when the turn asks to run a tool that needs the user's approval: the first approves it,
    /// the second declines it. Pressing anything else or typing declines it too.
    /// </param>
    public record FrameworkReplies(
        List<QuickReply> Closure,
        List<QuickReply> Escape,
        List<QuickReply>? Approval = null)
    {
        /// <summary>Gathers the authored sets by name; a set that the prompt does not declare is empty (null for the approval pair).</summary>
        /// <param name="sets">The prompt's <c>FrameworkReplies</c> array.</param>
        public static FrameworkReplies From(IEnumerable<FrameworkReplySet> sets)
        {
            // The array is walked once per set name, so it is materialized first.
            List<FrameworkReplySet> declared = [.. sets];

            // Looks up the replies of one named set and yields null when the prompt does not declare it.
            List<QuickReply>? Find(string name)
                => declared.FirstOrDefault(set => string.Equals(set.Name, name, StringComparison.OrdinalIgnoreCase))?.Replies;

            // Closure and escape default to empty so that a delivery never meets a null; the approval pair stays null, which means that no approval is offered.
            return new FrameworkReplies(
                Find(Constants.FrameworkReplySets.Closure) ?? [],
                Find(Constants.FrameworkReplySets.Escape) ?? [],
                Find(Constants.FrameworkReplySets.Approval));
        }
    }

    /// <summary>One named set of buttons as authored in morgana.json (see <see cref="Constants.FrameworkReplySets"/>).</summary>
    /// <param name="Name">Which set.</param>
    /// <param name="Replies">The buttons of the set, in the order they are offered.</param>
    public record FrameworkReplySet(
        string Name,
        List<QuickReply> Replies);

    /// <summary>
    /// How an agent closed its turn: the one structured decision beside its free text, recorded by the
    /// Reply tool and read once by <c>MorganaAgent</c> at the end of the turn.
    /// </summary>
    /// <param name="Awaits">What the turn ends waiting for from the user.</param>
    /// <param name="UserIsLeaving">True when the user's own message was a goodbye.</param>
    /// <param name="Actions">The actions offered as buttons; empty when none.</param>
    /// <param name="Card">The card presenting the turn's structured data; null when none.</param>
    public record TurnReply(
        [property: JsonPropertyName("awaits")] AwaitedFromUser Awaits,
        [property: JsonPropertyName("userIsLeaving")] bool UserIsLeaving,
        [property: JsonPropertyName("actions")] IReadOnlyList<ReplyAction> Actions,
        [property: JsonPropertyName("card")] RichCard? Card)
    {
        /// <summary>Joins an action's tool and its number in the id of the button that offers it.</summary>
        private const char ActionIdSeparator = '#';

        /// <summary>
        /// The tool that an action button leads to, read back from its id; null for a button that offers no
        /// action of an agent's, such as the framework's own.
        /// </summary>
        /// <param name="quickReplyId">The id of a button that a turn delivered.</param>
        public static string? ActionTool(string quickReplyId)
        {
            // The tool name may itself contain the separator, so the last one splits the button's number off.
            int separator = quickReplyId.LastIndexOf(ActionIdSeparator);

            // A button whose id carries no number was not made from an action, so it leads to no tool.
            return separator > 0 && int.TryParse(quickReplyId[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? quickReplyId[..separator]
                : null;
        }

        /// <summary>
        /// The turn as a choice step requires it: one action per tool of the step in the step's order, awaiting an action choice.
        /// </summary>
        /// <remarks>
        /// The model's own action is kept for a tool of the step (the first one) and a missing tool gets a button worded from its name.
        /// </remarks>
        /// <param name="stepTools">The tools of the choice step, in the order the step declares them.</param>
        public TurnReply WithStepActions(IReadOnlyList<string> stepTools)
        {
            // The step dictates the buttons: every tool of the step gets one so that the user can take any branch.
            List<ReplyAction> actions = [.. stepTools.Select(tool =>
                Actions.FirstOrDefault(action => string.Equals(action.Tool, tool, StringComparison.Ordinal))
                ?? new ReplyAction(tool, LabelFromToolName(tool), LabelFromToolName(tool)))];

            // The turn now awaits a choice among exactly those buttons; the model's other decisions are kept.
            return new TurnReply(AwaitedFromUser.ActionChoice, UserIsLeaving, actions, Card);
        }

        /// <summary>Words a tool name as a button label: <c>ConfirmOrder</c> becomes <c>Confirm order</c>.</summary>
        private static string LabelFromToolName(string toolName)
        {
            // The label is the tool name respelled as the words that a user reads on its button.
            StringBuilder label = new StringBuilder();

            for (int index = 0; index < toolName.Length; index++)
            {
                char current = toolName[index];

                // A word starts at a capital that follows a lower-case letter or a digit, so an acronym stays one word.
                if (index > 0 && char.IsUpper(current) && (char.IsLower(toolName[index - 1]) || char.IsDigit(toolName[index - 1])))
                    label.Append(' ');

                // The first letter is capitalized and every other one lowered, so the label reads as a sentence.
                label.Append(index == 0 ? char.ToUpperInvariant(current) : char.ToLowerInvariant(current));
            }

            return label.ToString();
        }

        /// <summary>
        /// True when the turn ends waiting for the user: a typed answer or one of the actions that it offers.
        /// </summary>
        [JsonIgnore]
        public bool AwaitsUser => !UserIsLeaving && (Awaits != AwaitedFromUser.Nothing || Actions.Count > 0);

        /// <summary>
        /// What the channel receives beside the text and whether the agent hands the conversation back.
        /// </summary>
        /// <remarks>
        /// A departing user gets no button and the conversation returns to Morgana. A typed answer is
        /// asked with no button, so nothing gates it. Offered actions carry the escape pair; an answered
        /// request carries the closure pair, through which the user stays or leaves. Without authored
        /// closure buttons an answered request has no way to be left, so it hands the conversation back.
        /// </remarks>
        /// <param name="frameworkReplies">The authored closure and escape pairs.</param>
        public (List<QuickReply>? QuickReplies, bool HandsBack) ToDelivery(FrameworkReplies frameworkReplies)
        {
            // A user who says goodbye gets no button and the agent hands the conversation back.
            if (UserIsLeaving)
                return (null, true);

            // The user is asked to type, so a button would only offer a way around the question; the agent stays active.
            if (Awaits == AwaitedFromUser.TypedAnswer)
                return (null, false);

            // Numbered per turn, so two actions leading to one tool stay two distinct buttons.
            if (Actions.Count > 0)
                return ([.. Actions.Select((action, index) => new QuickReply($"{action.Tool}{ActionIdSeparator}{index + 1}", action.Label, action.Value)),
                         .. frameworkReplies.Escape], false);

            // An answered request lets the user stay or leave through the closure pair; without authored buttons nothing could end the stay, so the agent hands back.
            return frameworkReplies.Closure.Count > 0 ? ([.. frameworkReplies.Closure], false) : (null, true);
        }
    }

    /// <summary>The turn as filed before the model runs.</summary>
    /// <param name="Session">The agent's own session that the turn is served on, loaded or created by the opening.</param>
    /// <param name="HistoryBaseline">History length before the turn, from which the turn's own messages are counted.</param>
    /// <param name="PressedActionTool">The tool that the pressed action button leads to; null when the message is not such a press.</param>
    public record TurnOpening(AgentSession Session, int HistoryBaseline, string? PressedActionTool);

    /// <summary>What the model's passes left of the turn.</summary>
    public record TurnOutcome(string Text, bool AwaitsApproval);

    /// <summary>The turn as delivered to the user.</summary>
    public record TurnClosure(string Text, List<QuickReply>? QuickReplies, RichCard? Card, bool IsCompleted, bool UserIsLeaving);

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
        /// HTTP or HTTPS transport. The MCP server is a remote process reachable at a URL.
        /// </summary>
        Http,

        /// <summary>
        /// Standard I/O transport. The MCP server is a local executable spawned as a child process.
        /// Communication runs over its stdin and stdout.
        /// </summary>
        Stdio
    }
}