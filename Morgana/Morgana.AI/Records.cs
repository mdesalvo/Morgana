using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Morgana.AI.Attributes;
using Morgana.Contracts;

namespace Morgana.AI;

/// <summary>
/// Central repository of immutable record types (DTOs) used throughout the Morgana framework.
/// Records are organized by functional area: agent communication, classification, prompts, tools and presentation.
/// </summary>
/// <remarks>
/// <para><strong>Design Philosophy:</strong></para>
/// <list type="bullet">
/// <item>Immutable records for thread-safety in actor message passing</item>
/// <item>Explicit types prevent message routing errors in actor system</item>
/// <item>JSON serialization support for LLM interactions and configuration loading</item>
/// <item>Context wrappers preserve sender references across async operations (PipeTo pattern)</item>
/// </list>
/// </remarks>
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
    /// Final response message sent from ConversationSupervisorActor to ConversationManagerActor after processing a user message.
    /// Contains the AI response, metadata, agent information, and optional quick reply buttons.
    /// </summary>
    /// <param name="Response">AI-generated response text</param>
    /// <param name="Classification">Intent classification result (e.g., "billing", "contract")</param>
    /// <param name="Metadata">Additional metadata from classification (confidence, error codes, etc.)</param>
    /// <param name="AgentName">Name of the agent that generated the response (e.g., "Morgana", "Morgana (Billing)")</param>
    /// <param name="AgentCompleted">Flag indicating if the agent completed its multi-turn interaction</param>
    /// <param name="QuickReplies">Optional list of quick reply buttons for guided user interactions</param>
    /// <param name="OriginalTimestamp">Optional timestamp of the message when created at UI level</param>
    /// <param name="RichCard">Optional rich card for structured data visualization</param>
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
    /// Configuration options for conversation persistence.
    /// </summary>
    /// <remarks>
    /// <para><strong>Configuration Example:</strong></para>
    /// <code>
    /// {
    ///   "Morgana": {
    ///     "ConversationPersistence": {
    ///       "StoragePath": "C:/MorganaData",
    ///       "EncryptionKey": "your-base64-encoded-256-bit-key"
    ///     }
    ///   }
    /// }
    /// </code>
    /// <para><strong>Generating an Encryption Key:</strong></para>
    /// <code>
    /// // C# code to generate a secure 256-bit key
    /// using System.Security.Cryptography;
    /// byte[] key = new byte[32];
    /// RandomNumberGenerator.Fill(key);
    /// string base64Key = Convert.ToBase64String(key);
    /// Console.WriteLine(base64Key);
    /// </code>
    /// </remarks>
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

    /// <summary>
    /// Chat message record for UI consumption (Cauldron).
    /// Represents a single message in the conversation history, mapped from Microsoft.Agents.AI.ChatMessage.
    /// </summary>
    /// <remarks>
    /// This record is optimized for Blazor UI rendering and includes UI-specific fields like AgentName, QuickReplies, etc.
    /// </remarks>
    public record MorganaChatMessage
    {
        /// <summary>
        /// Unique identifier of the conversation this message belongs to.
        /// </summary>
        public required string ConversationId { get; init; }

        /// <summary>
        /// Message text content displayed to the user.
        /// Extracted from TextContent blocks in ChatMessage.Content.
        /// </summary>
        public required string Text { get; init; }

        /// <summary>
        /// Timestamp when the message was created or received.
        /// Mapped from ChatMessage.CreatedAt.
        /// </summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// Type of message determining styling and behavior.
        /// Derived from ChatMessage.Role (user/assistant).
        /// </summary>
        public required MessageType Type { get; init; }

        /// <summary>
        /// Gets the message role for CSS styling ("user" or "assistant").
        /// </summary>
        public string Role => Type switch
        {
            MessageType.User => "user",
            _ => "assistant"
        };

        /// <summary>
        /// Name of the agent that generated this message.
        /// Examples: "User", "Morgana (billing)", "Morgana (contract)", ...
        /// </summary>
        public required string AgentName { get; init; }

        /// <summary>
        /// Indicates whether the agent has completed its task.
        /// Mapped from SQLite is_active column: true when is_active = 0, false when is_active = 1.
        /// </summary>
        public required bool AgentCompleted { get; init; }

        /// <summary>
        /// Optional list of quick reply buttons attached to this message.
        /// Reconstructed from SetQuickReplies tool calls when loading conversation history.
        /// </summary>
        public List<QuickReply>? QuickReplies { get; init; }

        /// <summary>
        /// Optional flag indicating that this is the last message of a resumed conversation.
        /// </summary>
        public bool? IsLastHistoryMessage { get; init; }

        /// <summary>
        /// Optional rich card attached to this message.
        /// Reconstructed from SetRichCard tool calls when loading conversation history.
        /// </summary>
        public RichCard? RichCard { get; init; }
    }

    /// <summary>
    /// Enumeration of message types for styling and behavior differentiation.
    /// </summary>
    public enum MessageType
    {
        /// <summary>
        /// Message from the user.
        /// Displayed on the right side with user styling.
        /// </summary>
        User,

        /// <summary>
        /// Regular response from an agent.
        /// Displayed on the left side with agent avatar and assistant styling.
        /// </summary>
        Assistant
    }

    // ==========================================================================
    // RATE LIMITING
    // ==========================================================================

    /// <summary>
    /// Configuration options for conversation rate limiting.
    /// </summary>
    /// <remarks>
    /// <para><strong>Configuration Example:</strong></para>
    /// <code>
    /// {
    ///   "Morgana": {
    ///     "RateLimiting": {
    ///       "Enabled": true,
    ///       "MaxMessagesPerMinute": 5,
    ///       "MaxMessagesPerHour": 30,
    ///       "MaxMessagesPerDay": 80,
    ///       "ErrorMessagePerMinute": "✋ Whoa there! You're casting spells too quickly...",
    ///       "ErrorMessagePerHour": "⏰ You've reached your hourly spell quota...",
    ///       "ErrorMessagePerDay": "🌙 You've exhausted today's magical energy...",
    ///       "ErrorMessageDefault": "⚠️ You're sending messages too quickly..."
    ///     }
    ///   }
    /// }
    /// </code>
    /// </remarks>
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
    /// Per-provider pricing: how many tokens of each direction equal one dust unit, plus
    /// cache cost-weights. Lives under <c>Morgana:LLM:{Provider}:MagicDust</c> because cost
    /// is a property of the concrete model. Zero on either axis means that direction does
    /// not consume dust (e.g. Ollama local models, set both to 0 for "free").
    /// <para>The MEAI Anthropic adapter reports <c>InputTokenCount</c> as the <em>total</em>
    /// prompt (fresh + cache-read + cache-write). Charging it flat would over-count cache
    /// reads (real cost ~0.1×) and under-count 1h cache writes (~2×). The two weights below
    /// let the limiter track real cache economics; defaults are 1.0 (cache-unaware no-op, so
    /// behaviour is unchanged unless a deployment configures them).</para>
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
    /// <see cref="Attributes.RequiresLLMTierAttribute"/>. Ordinal order (Low &lt; Moderate &lt;
    /// High) is load-bearing: it is used to pick the framework-actor default (the cheapest
    /// configured tier). Cross-tier pricing is deliberately NOT assumed to be monotonic — see
    /// <c>Services.RequiresLLMTierValidationService</c> remarks.
    /// </summary>
    public enum LLMTier
    {
        /// <summary>Cheapest/fastest tier. Default for framework actors (Guard, Classifier, Presenter, ChannelAdapter).</summary>
        Low,

        /// <summary>Mid-range tier for agents whose domain requires more than basic reasoning.</summary>
        Moderate,

        /// <summary>Most capable/expensive tier, for agents whose domain requires deep reasoning.</summary>
        High,

        /// <summary>
        /// Deployment-level escape hatch: "there is only one model here, and it serves every
        /// tier." When a provider's <c>Models</c> map contains an <c>Omni</c> key, it must be
        /// the SOLE entry (mixing it with Low/Moderate/High is a startup-fatal
        /// misconfiguration — ambiguous intent). Every <see cref="LLMTier"/> resolution
        /// (<see cref="Interfaces.ILLMService.GetChatClient(LLMTier)"/>,
        /// <see cref="Interfaces.ILLMService.GetPricing(LLMTier)"/>) is transparently
        /// redirected to it regardless of what tier was actually requested — an agent
        /// authored against <c>[RequiresLLMTier(LLMTier.High)]</c> still resolves successfully
        /// against an Omni-only deployment. Meant for small/local deployments (a single Ollama
        /// model is the canonical case: you cannot conjure three distinct models out of one
        /// loaded weights file) or any operator who deliberately opts out of per-agent tiering.
        /// Not meant to be declared on an agent via <see cref="Attributes.RequiresLLMTierAttribute"/>
        /// — it is a deployment override, not an authoring choice — though nothing prevents it
        /// (harmless: Omni would simply always win at that deployment too).
        /// </summary>
        Omni
    }

    /// <summary>
    /// A single named model offered by a provider at a given <see cref="LLMTier"/>, with its
    /// own dust pricing. Lives under <c>Morgana:LLM:{Provider}:Models</c> — a JSON object keyed
    /// by tier name (<c>"Low"</c>/<c>"Moderate"</c>/<c>"High"</c>/<c>"Omni"</c>), not an array:
    /// .NET's configuration binder merges JSON objects across layered sources (appsettings.json,
    /// User Secrets, environment variables) by key, so an override file only needs to repeat the
    /// tiers it actually overrides — the tier name is unambiguous regardless of how many entries
    /// are present in each layer or in what order they're written. An array keyed by ordinal
    /// index instead would merge positionally, silently pairing an override's N-th entry with
    /// the base config's N-th entry even when they name different tiers.
    /// </summary>
    /// <param name="Name">Provider-specific model/deployment identifier (e.g. "claude-haiku-4-5").</param>
    /// <param name="MagicDust">Dust pricing for this specific model — not shared across tiers, since cost is a property of the concrete model, not the provider as a whole.</param>
    public record ModelDefinition(
        string Name,
        MagicDustPricing MagicDust);

    /// <summary>
    /// Sentinel placeholder values used throughout appsettings.json for settings that MUST be
    /// overridden via User Secrets or environment variables before the app is usable (see
    /// CLAUDE.md "Conventions": <c>_SECURE_OVERRIDE_</c> for secrets, <c>_FUNCTIONAL_OVERRIDE_</c>
    /// for non-secret required values). Lives here, next to <see cref="ModelDefinition"/>, rather
    /// than inside <c>MorganaLLM</c>, because recognizing an unfilled placeholder is a config
    /// convention — not LLM-specific behavior — that any consumer of appsettings.json could
    /// reasonably need to check, not just the LLM provider constructors that happen to be the
    /// only ones checking it today.
    /// </summary>
    internal static readonly string[] OverridePlaceholders = ["_SECURE_OVERRIDE_", "_FUNCTIONAL_OVERRIDE_"];

    /// <summary>
    /// Policy for the per-conversation lifetime dust budget — a token-consumption guard
    /// orthogonal to <see cref="RateLimitOptions"/>. The budget is a lifetime resource: no
    /// sliding window, no reset. Once exhausted the conversation is done; the only way
    /// forward is a brand-new conversation.
    /// <para>Message templates are framework-neutral English defaults; deployments override
    /// them in <c>Morgana:DustLimiting</c> with their own copy and personality, exactly like
    /// <see cref="RateLimitOptions"/>. The warning templates support one placeholder,
    /// <c>{percent}</c> (remaining budget as a 0–100 integer — fuel-gauge semantics, the
    /// same number the gauge shows; users reason in "how much is left", not dust units).</para>
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
    /// Configuration options for the Morgana authentication service.
    /// </summary>
    /// <remarks>
    /// <para><strong>Per-Issuer Trust Model:</strong></para>
    /// <para>Each accepted channel is declared as its own <see cref="IssuerOptions"/> entry
    /// with its own signing key. A token is validated against the key of the issuer
    /// declared in its <c>iss</c> claim — so compromise of one channel's key does not
    /// impact the others. Tokens whose <c>iss</c> is not declared here are fail-closed.</para>
    ///
    /// <para><strong>Onboarding a New Channel:</strong></para>
    /// <para>Any channel beyond the reference one (Cauldron) must be registered as an
    /// <see cref="IssuerOptions"/> entry on the destination Morgana instance. Its
    /// <see cref="IssuerOptions.Name"/> must equal the <c>iss</c> claim the channel mints,
    /// and its <see cref="IssuerOptions.SymmetricKey"/> must match the secret the channel
    /// uses to sign tokens. A channel not declared here — or using a different key — is
    /// rejected at the very first request.</para>
    ///
    /// <para><strong>Configuration Example:</strong></para>
    /// <code>
    /// // appsettings.json
    /// {
    ///   "Morgana": {
    ///     "Authentication": {
    ///       "Audience": "morgana.ai",
    ///       "Issuers": [
    ///         { "Name": "cauldron", "SymmetricKey": "your-256-bit-secret-key-here" }
    ///       ]
    ///     }
    ///   }
    /// }
    /// </code>
    /// </remarks>
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
    /// <param name="UserId">The caller's unique identifier (from the <c>sub</c> claim), null if not authenticated</param>
    /// <param name="DisplayName">The caller's display name (from the <c>name</c> claim), null if not authenticated</param>
    /// <param name="Error">Description of why authentication failed, null if authenticated</param>
    public record AuthenticationResult(
        bool IsAuthenticated,
        string? UserId = null,
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
    /// <param name="UserId">Authenticated caller identity, propagated from the HTTP layer for conversation ownership and audit.</param>
    public record UserMessage(
        string ConversationId,
        string Text,
        DateTime Timestamp,
        ActivityContext TurnContext = default,
        string? UserId = null);

    // ==========================================================================
    // GUARD (CONTENT MODERATION) MESSAGES
    // ==========================================================================

    /// <summary>
    /// Request for content moderation check on a user message.
    /// Sent to GuardActor for two-level filtering (profanity + LLM policy check).
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
    /// Result returned by <see cref="Interfaces.IGuardRailService"/> after evaluating
    /// a user message against content and policy rules.
    /// </summary>
    /// <param name="Compliant">
    /// <c>true</c> if the message passes all guard-rail checks; <c>false</c> if it violates a rule.
    /// </param>
    /// <param name="Violation">
    /// Human-readable description of the violated rule when <paramref name="Compliant"/> is <c>false</c>;
    /// <c>null</c> otherwise.
    /// </param>
    /// <remarks>
    /// This record is the public contract of <see cref="Interfaces.IGuardRailService"/> and is
    /// intentionally decoupled from <see cref="GuardCheckResponse"/>, which is an LLM wire-format DTO
    /// used only by <see cref="Services.LLMGuardRailService"/> internally.
    /// </remarks>
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
    /// LLM response from ClassifierActor containing intent classification.
    /// Deserialized from JSON returned by the LLM.
    /// </summary>
    /// <param name="Intent">Classified intent name (e.g., "billing", "contract", "other")</param>
    /// <param name="Confidence">Confidence score from 0.0 to 1.0</param>
    public record ClassificationResponse(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("confidence")] double Confidence);

    /// <summary>
    /// Internal classification result used by the conversation pipeline.
    /// Contains the classified intent and additional metadata for routing and diagnostics.
    /// </summary>
    /// <param name="Intent">Classified intent name</param>
    /// <param name="Metadata">
    /// Additional metadata dictionary (e.g., confidence score, error codes, classification notes)
    /// </param>
    public record ClassificationResult(
        string Intent,
        Dictionary<string, string> Metadata);

    // ==========================================================================
    // AGENT REQUEST/RESPONSE MODELS
    // ==========================================================================

    /// <summary>
    /// Request message sent to MorganaAgent instances for processing user input.
    /// Contains the user's message and optional classification result from ClassifierActor.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Content">User message text to process (null for tool-only interactions)</param>
    /// <param name="Classification">Optional intent classification result (null for follow-up messages to active agents)</param>
    /// <param name="TurnContext">OpenTelemetry activity context for the current turn span.</param>
    /// <param name="Capabilities">
    /// Expressive capabilities advertised by the outbound channel serving this turn. Agents
    /// consult these to avoid engaging features the channel cannot render — most notably,
    /// skipping LLM streaming entirely when <see cref="ChannelCapabilities.SupportsStreaming"/>
    /// is false. When null, agents assume full capabilities (legacy/test paths).
    /// </param>
    public record AgentRequest(
        string ConversationId,
        string? Content,
        ClassificationResult? Classification,
        ActivityContext TurnContext = default,
        ChannelCapabilities? Capabilities = null);

    /// <summary>
    /// Response message from MorganaAgent instances after processing a request.
    /// Indicates the agent's response text, completion status, and optional quick reply buttons.
    /// </summary>
    /// <param name="Response">Agent's response text (may contain #INT# token for multi-turn interactions)</param>
    /// <param name="IsCompleted">
    /// True if agent has completed its task (conversation returns to idle).
    /// False if agent needs more user input (agent becomes active for follow-up messages).
    /// </param>
    /// <param name="QuickReplies">
    /// Optional list of quick reply buttons to display to the user.
    /// Agents can provide guided choices for better UX (e.g., contract sections, invoice selection).
    /// If null, no quick replies are shown.
    /// </param>
    /// <param name="RichCard">
    /// Optional rich card to display to the user.
    /// Agents can provide structured contents for more engaging UX (e.g., contract terms,  invoice details).
    /// If null, no rich cards are shown.
    /// </param>
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
        /// Converts intents to a dictionary mapping intent names to descriptions.
        /// Used by ClassifierActor to format intents for LLM classification prompt.
        /// </summary>
        /// <returns>Dictionary with intent name as key and description as value</returns>
        /// <remarks>
        /// <para><strong>Usage in ClassifierActor:</strong></para>
        /// <code>
        /// IntentCollection intentCollection = new IntentCollection(intents);
        /// Dictionary&lt;string, string&gt; intentDict = intentCollection.AsDictionary();
        ///
        /// // Format for LLM: "billing (requests to view invoices)|contract (requests to summarize contract)"
        /// string formattedIntents = string.Join("|", intentDict.Select(kvp => $"{kvp.Key} ({kvp.Value})"));
        /// </code>
        /// </remarks>
        public Dictionary<string, string> AsDictionary()
        {
            return Intents.ToDictionary(i => i.Name, i => i.Description);
        }

        /// <summary>
        /// Gets intents that should be displayed in presentation quick replies.
        /// Excludes the "other" fallback intent and intents without labels.
        /// </summary>
        /// <returns>List of displayable intent definitions</returns>
        /// <remarks>
        /// <para><strong>Filtering Rules:</strong></para>
        /// <list type="bullet">
        /// <item>Exclude "other" intent (special fallback, not user-selectable)</item>
        /// <item>Exclude intents without Label property (not meant for UI display)</item>
        /// </list>
        /// <para><strong>Usage in Presentation:</strong></para>
        /// <code>
        /// IntentCollection intentCollection = new IntentCollection(allIntents);
        /// List&lt;IntentDefinition&gt; displayable = intentCollection.GetDisplayableIntents();
        ///
        /// // Convert to quick replies for SignalR
        /// List&lt;QuickReply&gt; quickReplies = displayable
        ///     .Select(i => new QuickReply(i.Name, i.Label, i.DefaultValue))
        ///     .ToList();
        /// </code>
        /// </remarks>
        public List<IntentDefinition> GetDisplayableIntents()
        {
            return Intents
                .Where(i => !string.Equals(i.Name, "other", StringComparison.OrdinalIgnoreCase)
                              && !string.IsNullOrEmpty(i.Label))
                .ToList();
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
    /// Prompt definition containing instructions, personality, and metadata for agents and actors.
    /// Loaded from morgana.json (framework prompts) or agents.json (domain prompts).
    /// </summary>
    /// <param name="ID">
    /// Unique prompt identifier.
    /// Framework: "Morgana", "Classifier", "Guard", "Presentation"
    /// Domain: Intent names like "billing", "contract", "monkeys"
    /// </param>
    /// <param name="Type">Prompt type (e.g., "SYSTEM", "INTENT")</param>
    /// <param name="SubType">Prompt subtype (e.g., "AGENT", "ACTOR", "PRESENTATION")</param>
    /// <param name="Target">Core prompt text defining role and capabilities</param>
    /// <param name="Instructions">Behavioral rules and guidelines</param>
    /// <param name="Formatting">Output formatting rules (markdown, length limits, etc.)</param>
    /// <param name="Personality">Optional tone and character traits</param>
    /// <param name="Language">Language code (e.g., "en-US", "it-IT")</param>
    /// <param name="Version">Prompt version for tracking changes</param>
    /// <param name="AdditionalProperties">
    /// List of dictionaries containing additional data like GlobalPolicies, Tools, ErrorAnswers, etc.
    /// </param>
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
        /// Gets a strongly-typed additional property from the prompt configuration.
        /// Searches all AdditionalProperties dictionaries for the specified key.
        /// </summary>
        /// <typeparam name="T">Type to deserialize the property value into</typeparam>
        /// <param name="additionalPropertyName">Name of the property to retrieve (e.g., "Tools", "GlobalPolicies")</param>
        /// <returns>Deserialized property value</returns>
        /// <exception cref="KeyNotFoundException">Thrown if property not found in any AdditionalProperties dictionary</exception>
        /// <remarks>
        /// <para><strong>Common Additional Properties:</strong></para>
        /// <list type="bullet">
        /// <item><term>Tools</term><description>List&lt;ToolDefinition&gt; - Tool configurations for agents</description></item>
        /// <item><term>GlobalPolicies</term><description>List&lt;GlobalPolicy&gt; - Framework-level behavioral policies</description></item>
        /// <item><term>ErrorAnswers</term><description>List&lt;ErrorAnswer&gt; - Error message templates</description></item>
        /// <item><term>ProfanityTerms</term><description>List&lt;string&gt; - Terms for content moderation</description></item>
        /// <item><term>FallbackMessage</term><description>string - Default presentation message</description></item>
        /// </list>
        /// <para><strong>Usage Examples:</strong></para>
        /// <code>
        /// Prompt morganaPrompt = await promptResolver.ResolveAsync("Morgana");
        ///
        /// // Get global policies
        /// List&lt;GlobalPolicy&gt; policies = morganaPrompt.GetAdditionalProperty&lt;List&lt;GlobalPolicy&gt;&gt;("GlobalPolicies");
        ///
        /// // Get tools
        /// ToolDefinition[] tools = morganaPrompt.GetAdditionalProperty&lt;ToolDefinition[]&gt;("Tools");
        ///
        /// // Get error messages
        /// List&lt;ErrorAnswer&gt; errors = morganaPrompt.GetAdditionalProperty&lt;List&lt;ErrorAnswer&gt;&gt;("ErrorAnswers");
        /// </code>
        /// </remarks>
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
    }

    /// <summary>
    /// Global policy definition specifying framework-level behavioral rules.
    /// Applied to all agents and actors to enforce consistent behavior.
    /// </summary>
    /// <param name="Name">Policy name (e.g., "ContextHandling", "InteractiveToken")</param>
    /// <param name="Description">Detailed policy description with enforcement rules</param>
    /// <param name="Type">Policy type ("Critical" or "Operational")</param>
    /// <param name="Priority">Priority level (lower number = higher priority)</param>
    public record GlobalPolicy(
        string Name,
        string Description,
        string Type,
        int Priority);

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
    public record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters);

    /// <summary>
    /// Tool parameter definition specifying parameter name, description, and behavior.
    /// Controls whether parameter comes from context or request, and if it's shared across agents.
    /// </summary>
    /// <param name="Name">Parameter name (must match method parameter name)</param>
    /// <param name="Description">Parameter description for LLM understanding</param>
    /// <param name="Required">Whether the parameter is required (true) or optional (false)</param>
    /// <param name="Scope">
    /// Parameter scope: "context" (retrieve via GetContextVariable) or "request" (use directly from user input)
    /// </param>
    /// <param name="Shared">
    /// Whether this context variable should be persisted in the conversation-scoped
    /// <c>shared_context</c> registry so other agents of the same conversation can hydrate it
    /// at the start of their next turn. Only applies when Scope="context". Default: false.
    /// </param>
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