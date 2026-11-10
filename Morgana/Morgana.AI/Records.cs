using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Providers;
using Morgana.Contracts;

namespace Morgana.AI;

/// <summary>
/// Immutable record types (DTOs) for actor messages, configuration and serialization.
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
    /// RecordedTimestamp: the instant an agent recorded this reply in its own session, carried so the reply
    /// pushed live and the same reply read back later are recognisably one. Null when no agent wrote it —
    /// a refusal, a disambiguation, a fallback: Morgana said it, so she is the one who files and dates it.
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

    /// <summary>What a model costs in US dollars per million tokens, as its vendor bills it.</summary>
    /// <param name="InputUsdPerMillion">Price of a fresh prompt token.</param>
    /// <param name="OutputUsdPerMillion">Price of a generated token.</param>
    /// <param name="CacheReadUsdPerMillion">Price of a prompt token that the provider's cache served.</param>
    /// <param name="CacheWriteUsdPerMillion">Price of a prompt token written into the provider's cache, at the lifetime that the provider's client asks for.</param>
    public record ModelPricing(decimal InputUsdPerMillion, decimal OutputUsdPerMillion, decimal CacheReadUsdPerMillion, decimal CacheWriteUsdPerMillion);

    /// <summary>
    /// What one dust unit is worth on a tier: tokens per unit for each direction and the cost weights of cached input.
    /// A zero on either axis makes that direction free. It is derived from a <see cref="ModelPricing"/>, listed by the provider or declared by the tier, by
    /// <c>MorganaLanguageModel.ConvertToMorganaDust</c> and never configured.
    /// Check BudgetPerConversation against the heaviest agent's calls per turn in dust_usage_log, not against nominal turn counts.
    /// </summary>
    public record MagicDustPricing
    {
        /// <summary>Input tokens that equal one dust unit. 0 disables input charging.</summary>
        public int InputTokensPerDustUnit { get; set; }

        /// <summary>Output tokens that equal one dust unit. 0 disables output charging.</summary>
        public int OutputTokensPerDustUnit { get; set; }

        /// <summary>
        /// Cost weight applied to cache-read input tokens (<c>CachedInputTokenCount</c>):
        /// the price of such a token relative to a fresh input token.
        /// </summary>
        public double CachedInputWeight { get; set; }

        /// <summary>
        /// Cost weight applied to cache-creation input tokens
        /// (<c>AdditionalCounts[Constants.UsageCounts.CacheCreationInputTokens]</c>):
        /// the price of such a token relative to a fresh input token.
        /// </summary>
        public double CacheCreationWeight { get; set; }
    }

    // ==========================================================================
    // LLM TIERS (MULTI-MODEL)
    // ==========================================================================

    /// <summary>
    /// Closed set of LLM power/cost tiers an agent can declare itself against via
    /// <see cref="Attributes.RequiresLLMTierAttribute"/>: three models, not a spectrum, with no
    /// fallback from one to another. Morgana's own framework actors run on the tier that
    /// <c>Morgana:ActorSystem:Tier</c> names. Cross-tier pricing is deliberately NOT assumed to be
    /// monotonic — see <c>Services.RequiresLLMTierValidationService</c> remarks.
    /// </summary>
    public enum LLMTier
    {
        /// <summary>
        /// The cheapest model: routine work that needs no deliberate reasoning, such as the framework
        /// actors (Guard, Classifier, Presenter, ChannelAdapter) where the deployment puts them here.
        /// </summary>
        Economy,

        /// <summary>
        /// The tier of any domain agent handling ordinary work. An agent belongs on Efficiency unless
        /// its author can point to a genuine, existential need for the expressive or computational
        /// headroom only Performance provides; "it might do a bit better" is not that bar.
        /// </summary>
        Efficiency,

        /// <summary>
        /// Reserved exclusively for agents whose domain author declares an
        /// existential need for deep reasoning or high expressive power — not a "nicer to
        /// have" upgrade from Efficiency, a hard requirement the agent cannot function
        /// without. Most capable/expensive tier.
        /// </summary>
        Performance
    }

    /// <summary>A tier as it is served: its client, the provider that built the client and its dust.</summary>
    public record ServedTier(IChatClient Client, MorganaLanguageModel LanguageModel, MagicDustPricing MorganaDust);

    /// <summary>The options of <see cref="ChatOptions"/> that a model carries, in a provider's <c>models.json</c> and on a tier alike.</summary>
    /// <remarks>Each provider's adapter sends the fields that its vendor supports, while tools, tool mode, instructions and response format are left out so that a deployment cannot force a tool call or rewrite what an agent was told.</remarks>
    public record ModelOptions
    {
        /// <summary>The name under which the provider serves the model.</summary>
        public required string ModelId { get; init; }

        /// <summary>The output ceiling of one answer; the provider's models.json gives the model's own and a tier may lower it.</summary>
        public int? MaxOutputTokens { get; init; }

        /// <summary>The sampling temperature.</summary>
        public float? Temperature { get; init; }

        /// <summary>The nucleus sampling threshold.</summary>
        public float? TopP { get; init; }

        /// <summary>The number of most likely tokens that sampling is limited to.</summary>
        public int? TopK { get; init; }

        /// <summary>The penalty on tokens in proportion to how often they already appeared.</summary>
        public float? FrequencyPenalty { get; init; }

        /// <summary>The penalty on tokens that already appeared.</summary>
        public float? PresencePenalty { get; init; }

        /// <summary>The seed that makes sampling repeatable where the provider supports it.</summary>
        public long? Seed { get; init; }

        /// <summary>The reasoning effort and the form of its output.</summary>
        public ReasoningOptions? Reasoning { get; init; }

        /// <summary>The sequences that end generation.</summary>
        public List<string>? StopSequences { get; init; }

        /// <summary>The options that only one provider reads, under the names that its own API gives them.</summary>
        public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }

        /// <summary>Fills into <paramref name="chatOptions"/> what this model declares and the call left unset.</summary>
        public void FillUnsetOptions(ChatOptions chatOptions)
        {
            // The call's own choice wins over the tier for each scalar option.
            chatOptions.ModelId ??= ModelId;
            chatOptions.MaxOutputTokens ??= MaxOutputTokens;
            chatOptions.Temperature ??= Temperature;
            chatOptions.TopP ??= TopP;
            chatOptions.TopK ??= TopK;
            chatOptions.FrequencyPenalty ??= FrequencyPenalty;
            chatOptions.PresencePenalty ??= PresencePenalty;
            chatOptions.Seed ??= Seed;

            // A copy is filled in, because a shared instance would leak one call's changes into the next.
            if (chatOptions.Reasoning is null && Reasoning is not null)
                chatOptions.Reasoning = new ReasoningOptions { Effort = Reasoning.Effort, Output = Reasoning.Output };

            // A copy of the list for the same reason: the call may append to its own stop sequences.
            if (chatOptions.StopSequences is null && StopSequences is not null)
                chatOptions.StopSequences = [.. StopSequences];

            // Provider-specific keys are merged one by one, so a call that sets one of them keeps its own value.
            if (AdditionalProperties is not null)
            {
                // The call's dictionary is created when it has none, never shared with this record.
                chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                foreach (KeyValuePair<string, object?> additionalProperty in AdditionalProperties)
                    chatOptions.AdditionalProperties.TryAdd(additionalProperty.Key, additionalProperty.Value);
            }
        }
    }

    /// <summary>A model as a provider catalogues it and as a tier runs it.</summary>
    /// <param name="Options">What the model is called and how it is asked.</param>
    /// <param name="Pricing">What the model costs.</param>
    public record ModelDefinition(ModelOptions Options, ModelPricing Pricing);

    /// <summary>
    /// Per-conversation lifetime dust budget (no sliding window, no reset). Orthogonal to RateLimitOptions.
    /// Message templates are English defaults; deployments override in Morgana:DustLimiting with own personality.
    /// Percent placeholder: fuel-gauge semantics (remaining as 0–100 integer, not dust units).
    /// </summary>
    public record DustLimitingOptions
    {
        /// <summary>Master toggle. When false the limiter is fully bypassed (fail open).</summary>
        public bool Enabled { get; set; }

        /// <summary>Total dust a conversation may consume over its lifetime. Zero is a valid budget: spent from the start, so no turn or command is ever admitted.</summary>
        public double BudgetPerConversation { get; set; }

        /// <summary>One-shot advisory shown when consumption crosses 70%.</summary>
        public string Warning70Message { get; set; } =
            "🧙‍♀️ The magic dust in the cauldron is running low ({percent}% left)...";

        /// <summary>One-shot advisory shown when consumption crosses 90%.</summary>
        public string Warning90Message { get; set; } =
            "🧙‍♀️ The cauldron is almost empty — wrap up soon, the dust is nearly gone ({percent}% left).";

        /// <summary>Blocking message shown when the budget is exhausted (100%).</summary>
        public string ErrorMessage { get; set; } =
            "🧙‍♀️ The magic dust from this conversation has run out. This conversation is now complete — start a new one to keep going.";
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
    /// What this installation may do toward a partner: consult its published agents, or nothing.
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
        /// Published agents this partner may consult, or <c>null</c> to admit it to every one of them.
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
            "This agent cannot take on further conversations right now. Proceed without it.";
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
    /// <param name="TurnCancellation">Stops the work of this request once whoever asked for it stops waiting.</param>
    public record UserMessage(
        string ConversationId,
        string Text,
        DateTime Timestamp,
        ActivityContext TurnContext = default,
        string? CallerId = null,
        CancellationToken TurnCancellation = default);

    // ==========================================================================
    // GUARD (CONTENT MODERATION) MESSAGES
    // ==========================================================================

    /// <summary>
    /// Request for content moderation check on a user message.
    /// Sent to GuardActor for LLM-based policy checking.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Message">User message to check for policy violations</param>
    /// <param name="TurnCancellation">Stops the work of this request once whoever asked for it stops waiting.</param>
    public record GuardCheckRequest(
        string ConversationId,
        string Message,
        CancellationToken TurnCancellation = default);

    /// <summary>
    /// The verdict in the JSON shape the Guard prompt asks the model for; read by <c>LLMGuardRailService</c> alone.
    /// </summary>
    /// <param name="Compliant">True if message passes policy checks, false if violation detected</param>
    /// <param name="Violation">Description of policy violation if Compliant is false</param>
    public record GuardCheckResponse(
        [property: JsonPropertyName("compliant")] bool Compliant,
        [property: JsonPropertyName("violation")] string? Violation);

    /// <summary>
    /// What the ToolGuard inspector reads: the tool's name, the arguments the agent called it with and what it returned, as a JSON object.
    /// </summary>
    /// <remarks>The three JSON names are a contract with the ToolGuard prompt's Target sentence.</remarks>
    /// <param name="Tool">The name of the tool that answered; together with "arguments" it is the call the result answers.</param>
    /// <param name="Arguments">The arguments the agent called the tool with; for a partner consultation they are the question.</param>
    /// <param name="Result">What the tool returned.</param>
    public record ToolGuardSubject(
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("arguments")] JsonElement Arguments,
        [property: JsonPropertyName("result")] JsonElement Result);

    /// <summary>
    /// What the UserGuard reads: the answer the user received last and the message they wrote after it, as a JSON object.
    /// </summary>
    /// <remarks>The two JSON names are a contract with the UserGuard prompt's Target sentence.</remarks>
    /// <param name="Answer">The last answer the channel delivered to the conversation; left out when it is not at hand, since a null would read as a claim that nobody has spoken.</param>
    /// <param name="Message">What the user wrote.</param>
    public record UserGuardSubject(
        [property: JsonPropertyName("answer")] UserGuardAnswer? Answer,
        [property: JsonPropertyName("message")] string Message);

    /// <summary>
    /// The answer as the user saw it, which the UserGuard reads to tell what a message refers to.
    /// </summary>
    /// <param name="Text">The text of the answer.</param>
    /// <param name="Card">The rich card the answer carried; omitted when there was none.</param>
    /// <param name="Buttons">The labels of the buttons the user saw; null when the answer carried none.</param>
    public record UserGuardAnswer(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("card")] RichCard? Card,
        [property: JsonPropertyName("buttons")] IReadOnlyList<string>? Buttons);

    /// <summary>
    /// What the ToolGuard inspector reads when an external source offers tools: the source and the tools it offers, as a JSON object.
    /// </summary>
    /// <remarks>The JSON names are a contract with the ToolGuard prompt's Target sentence about the offer.</remarks>
    /// <param name="Source">The server or partner that makes the offer.</param>
    /// <param name="Tools">The tools the source offers.</param>
    public record ToolGuardOffer(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("tools")] IReadOnlyList<ToolGuardOfferedTool> Tools);

    /// <summary>
    /// One tool of an offer, as the source describes it.
    /// </summary>
    /// <remarks>The JSON names are a contract with the ToolGuard prompt's Target sentence about the offer.</remarks>
    /// <param name="Name">The name under which the model would call the tool.</param>
    /// <param name="Description">The description the source gives the tool.</param>
    /// <param name="Parameters">The tool's input schema as the source sent it; null for a partner's function, whose schema is Morgana's own.</param>
    public record ToolGuardOfferedTool(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement? Parameters);

    /// <summary>
    /// What the PeerGuard reads: the partner asking, the agent asked, what that agent answers for and the question, as a JSON object.
    /// </summary>
    /// <remarks>The four JSON names are a contract with the PeerGuard prompt's Target sentence.</remarks>
    /// <param name="Partner">The partner asking, as the authentication gate proved it.</param>
    /// <param name="Agent">The agent asked.</param>
    /// <param name="Territory">What the asked agent answers for, as its card publishes it.</param>
    /// <param name="Question">The partner's question in its own words.</param>
    public record PeerGuardSubject(
        [property: JsonPropertyName("partner")] string Partner,
        [property: JsonPropertyName("agent")] string Agent,
        [property: JsonPropertyName("territory")] string Territory,
        [property: JsonPropertyName("question")] string Question);

    /// <summary>
    /// The tools an agent may hold from one MCP server and the verdict they were admitted or refused under.
    /// </summary>
    /// <param name="Tools">The admitted tools; empty unless <paramref name="Verdict"/> is compliant.</param>
    /// <param name="Verdict">The verdict on the server's current offer.</param>
    public record MCPServerAdmission(IReadOnlyList<AIFunction> Tools, GuardRailResult Verdict);

    /// <summary>
    /// Which layer of a guard decided a verdict, carried so an operator can tell a judgment from an admission by default.
    /// </summary>
    public enum GuardVerdictSource
    {
        /// <summary>The deterministic check decided, with no model involved.</summary>
        Prefilter,

        /// <summary>A model judged the text against its guard prompt.</summary>
        Inspector,

        /// <summary>The provider's own content filter refused the text before any judgment.</summary>
        ProviderFilter,

        /// <summary>No judgment could be reached and the text was admitted.</summary>
        FailOpen,

        /// <summary>No judgment could be reached on a text nobody here vouches for and the text was withheld.</summary>
        FailClosed,

        /// <summary>The guard is switched off and the text was admitted unjudged.</summary>
        Disabled
    }

    /// <summary>
    /// The verdict of a guard: whether the text passes, the finding when it does not and which layer decided.
    /// </summary>
    /// <param name="Compliant">True if the text passes all checks</param>
    /// <param name="Violation">Human-readable finding when Compliant is false</param>
    /// <param name="Source">The layer that decided the verdict</param>
    public record GuardRailResult(
        bool Compliant,
        string? Violation,
        GuardVerdictSource Source);

    /// <summary>
    /// Sent by an agent back to the supervisor when the LLM provider rejects the request
    /// due to a content filter, as the provider of the agent's tier recognises it.
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
    /// Request to agent: ConversationId, user message Content (null for tool-only), optional Classification
    /// (null for follow-up to active agents), TurnContext for OTel span, channel Capabilities (null→full capability).
    /// IsLanding marks the press of a button that Morgana offered to choose this agent: the turn opens with the agent presenting itself.
    /// </summary>
    /// <param name="ConversationId">Conversation the request belongs to.</param>
    /// <param name="Content">The user's message text; null for tool-only requests.</param>
    /// <param name="Classification">The classified intent; null for a follow-up to the active agent.</param>
    /// <param name="TurnContext">OpenTelemetry activity context of the current turn span.</param>
    /// <param name="Capabilities">What the channel can show; null means full capability.</param>
    /// <param name="ContentAlreadyStored">True when the user's phrase was already saved as Morgana's own.</param>
    /// <param name="IsLanding">True when the turn opens with the agent presenting itself.</param>
    /// <param name="TurnCancellation">Stops the work of this request once whoever asked for it stops waiting.</param>
    public record AgentRequest(
        string ConversationId,
        string? Content,
        ClassificationResult? Classification,
        ActivityContext TurnContext = default,
        ChannelCapabilities? Capabilities = null,
        bool ContentAlreadyStored = false,
        bool IsLanding = false,
        CancellationToken TurnCancellation = default);

    /// <summary>
    /// Agent response: text, IsCompleted flag (true→idle, false→agent stays active),
    /// optional QuickReplies, optional RichCard for structured UX (e.g., contract terms, invoice details).
    /// RecordedTimestamp: the timestamp the agent's session keeps this reply under, carried to the
    /// channel so the history and the live push date the reply identically. Null when nothing was recorded.
    /// </summary>
    public record AgentResponse(
        string Response,
        bool IsCompleted = true,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null,
        DateTime? RecordedTimestamp = null);

    /// <summary>
    /// Response from RouterActor containing both the agent's response and a reference to the agent actor.
    /// Used to track which agent is handling the request for multi-turn conversation management.
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
    /// Represents a streaming chunk from an agent during real-time response generation.
    /// Sent incrementally to enable progressive UI rendering.
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
    /// How long each party to a turn waits, as one ladder derived from the pipeline's own turn budget.
    /// </summary>
    /// <remarks>
    /// The parties cannot see each other, so the order makes a failure legible instead of merely loud: the colleague gives up first then the asking agent then the supervisor while the channel waits longest because it alone cannot see Morgana's phases.
    /// </remarks>
    /// <param name="Turn">The pipeline's budget on one turn and the outermost of the three that Morgana's own parties share.</param>
    /// <param name="Caller">Wait of the agent asking, on the wire.</param>
    /// <param name="Callee">Wait of the installation answering, on the actor that serves it.</param>
    /// <param name="Channel">How long a channel waits on a silent turn before giving it up.</param>
    public record TurnWaits(TimeSpan Turn, TimeSpan Caller, TimeSpan Callee, TimeSpan Channel)
    {
        /// <summary>Share of the turn left to the asking agent, the rest being what the turn keeps to answer with.</summary>
        private const double CallerShareOfTurn = 0.9;

        /// <summary>Share of the turn left to the answering installation, one step further in.</summary>
        private const double CalleeShareOfTurn = 0.8;

        /// <summary>Phases that can pass before a channel sees the first word: the guard, the classifier and the agent can each spend a whole <c>Turn</c>.</summary>
        private const int PhasesBeforeFirstWord = 3;

        /// <summary>The time a webhook delivery is retried for, so a reply that Morgana is still delivering lands before the channel gives the turn up.</summary>
        private static readonly TimeSpan DeliveryAllowance = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Reads the whole ladder off the pipeline's turn budget, the only thing configured, as proportions and allowances rather than subtracted seconds.
        /// </summary>
        /// <param name="configuration">Application configuration, read for the turn budget.</param>
        public static TurnWaits From(IConfiguration configuration)
        {
            // The turn budget is the one configured number every other rung derives from.
            TimeSpan turn = TimeSpan.FromSeconds(configuration.GetValue("Morgana:ActorSystem:TimeoutSeconds", 180));

            // Callee and caller sit inside the turn while the channel sits outside it, so it always expires after Morgana and never together with her.
            return new TurnWaits(turn, turn * CallerShareOfTurn, turn * CalleeShareOfTurn, turn * PhasesBeforeFirstWord + DeliveryAllowance);
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
    /// What MorganaAgentAdapter builds for one agent: the agent that runs its turns and the two providers those turns read and write.
    /// </summary>
    /// <param name="Agent">Agent that runs the turns.</param>
    /// <param name="ContextProvider">Provider holding the session variables and the shared-context write callback.</param>
    /// <param name="HistoryProvider">Provider exposing the chat history of the session.</param>
    public record CreatedAgent(AIAgent Agent, MorganaAIContextProvider ContextProvider, MorganaChatHistoryProvider HistoryProvider);

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
    /// <param name="CallerIntent">Intent of the asking agent, or <c>null</c> when the A2A caller is
    /// not an agent of this installation and named none.</param>
    /// <param name="Question">The colleague's question, already carrying the declaration spliced in
    /// front of it, since the answering agent's prompt says nothing about serving a colleague.</param>
    /// <param name="TurnContext">OTel context of the user turn that triggered the consultation.</param>
    /// <param name="TurnCancellation">Stops the work of this request once whoever asked for it stops waiting.</param>
    public record PeerConsultation(
        string ConversationId,
        string? CallerIntent,
        string Question,
        ActivityContext TurnContext = default,
        CancellationToken TurnCancellation = default);

    /// <summary>
    /// A colleague's answer, both as the actor reply and — serialized — as the tool result the
    /// asking agent's model reads, which is why every member carries an explicit JSON name.
    /// </summary>
    /// <param name="Answer">The colleague's own words, as its turn wrote them for whoever asked.</param>
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
    /// LLM-generated presentation: the two texts of Morgana's presentation, deserialized from the JSON
    /// the model returns. The buttons beneath them are never the model's: they are the configured intents.
    /// </summary>
    /// <param name="Message">Welcome/presentation message text (2-3 sentences)</param>
    /// <param name="Reintroduction">The same presentation without greeting, for a user who asks again what Morgana can do.</param>
    public record PresentationResponse(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("reintroduction")] string Reintroduction);

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
    /// <param name="Reintroduction">
    /// The same presentation without greeting, served when a request lands on the complement intent.
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
        string Reintroduction,
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
        /// The buttons beneath the welcome, one per displayable intent, as the presenter built them;
        /// null sends the welcome with none.
        /// </summary>
        public List<QuickReply>? QuickReplies { get; init; }
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
    /// <param name="Label">User-facing label with emoji (e.g., "📄 Billing") for quick replies; required</param>
    /// <param name="DefaultValue">Sample user message for this intent (used in quick reply value); required</param>
    public record IntentDefinition(
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Description")] string Description,
        [property: JsonPropertyName("Label")] string Label,
        [property: JsonPropertyName("DefaultValue")] string DefaultValue);

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
        /// Returns intents for presentation quick replies, excluding <see cref="Constants.Intents.Other"/> and intents
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
    /// <param name="ID">Prompt identifier: framework=one of <see cref="Constants.Prompts.All"/>, domain=intent name</param>
    /// <param name="Target">Core prompt text: role definition, capabilities statement, operational boundaries</param>
    /// <param name="Instructions">Behavioral rules, operational order, response constraints, tool-usage doctrine</param>
    /// <param name="Formatting">Output formatting rules: markdown usage, quick reply format, rich card rendering</param>
    /// <param name="Personality">Optional tone/character: formality, voice, domain-specific persona traits</param>
    /// <param name="Territory">What falls to this agent, addressed to a colleague who might consult it</param>
    /// <param name="Language">BCP 47 language code (e.g., "en-US", "it-IT")</param>
    /// <param name="Version">Prompt version string for tracking iteration history and regression detection</param>
    /// <param name="AdditionalProperties">List of structured properties: Tools, GlobalPolicies, ErrorAnswers, FallbackMessage, etc</param>
    public record Prompt(
        string ID,
        string Target,
        string Instructions,
        string Formatting,
        string? Personality,
        string? Territory,
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
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties ?? [])
            {
                // A declared property arrives as the JSON element the prompt file holds for it.
                if (additionalProperties.TryGetValue(additionalPropertyName, out object? value) && value is JsonElement element)
                {
                    // A property declared as null is a defect of the prompt file, reported like a missing one.
                    return element.Deserialize<T>()
                        ?? throw new InvalidOperationException($"AdditionalProperty with key '{additionalPropertyName}' is null in the prompt with id='{ID}'");
                }
            }
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
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // A plugin's agents.json may still open a section with its own label: it is kept once, never doubled.
            string trimmed = text.Trim();
            return trimmed.StartsWith(label, StringComparison.Ordinal) ? trimmed : $"{label} {trimmed}";
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
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties ?? [])
            {
                // A declared property arrives as the JSON element the prompt file holds for it.
                if (additionalProperties.TryGetValue(additionalPropertyName, out object? value) && value is JsonElement element)
                {
                    // A property declared as null counts as one left out.
                    return element.Deserialize<T>() ?? defaultValue;
                }
            }
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

    /// <summary>
    /// One framework-level template, spliced at the single site where it has a referent — a tool
    /// description, an agent's own instructions, the turn serving a colleague. Never rendered among
    /// the policies, where it would instruct against nothing.
    /// </summary>
    /// <remarks>
    /// It is the array an entry lives in that says which of the two it is, so a rule and a template
    /// cannot be confused for one another by anything anybody writes inside an entry. Carries no
    /// priority: each is fetched by name and no two are ever read together.
    /// </remarks>
    public record Injection(
        string Name,
        string Description)
    {
        /// <summary>
        /// Resolves a template's text by name (see <see cref="Constants.Injections"/>); returns an
        /// empty string when the prompt layer declares none, which every splice site reads as
        /// "inject nothing".
        /// </summary>
        public static string ResolveTemplate(IEnumerable<Injection> injections, string name)
            => injections.FirstOrDefault(injection =>
                   string.Equals(injection.Name, name, StringComparison.OrdinalIgnoreCase))?.Description ?? "";
    }

    /// <summary>
    /// Error message template with named identifier.
    /// Used to provide consistent, user-friendly error messages across the system.
    /// </summary>
    /// <param name="Name">Error identifier (e.g., "GenericError", "LLMServiceError")</param>
    /// <param name="Content">Error message template (may contain placeholders</param>
    public record ErrorAnswer(
        string Name,
        string Content);

    // ==========================================================================
    // TOOL CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>Where a tool parameter's value comes from.</summary>
    public enum ToolScope
    {
        /// <summary>Asked of the user on the turn it is needed.</summary>
        Request,

        /// <summary>Held in this agent's context, looked up before the user is asked.</summary>
        Context,

        /// <summary>Held in the context every agent of the conversation reads, looked up before the user is asked.</summary>
        SharedContext
    }

    /// <summary>A callable tool as projected from its method: name, description and parameters.</summary>
    /// <param name="Name">Tool method name (must match actual method name in MorganaTool class)</param>
    /// <param name="Description">Tool description for LLM understanding</param>
    /// <param name="Parameters">List of tool parameter definitions</param>
    public record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters);

    /// <summary>A tool parameter as projected from its method: Scope is null for a value the model itself authors and Shared applies to a context parameter only.</summary>
    public record ToolParameter(
        string Name,
        string Description,
        bool Required,
        string? Scope,
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