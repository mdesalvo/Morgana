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
/// Every immutable record of the framework: actor messages, configuration and the JSON shapes that models read and write.
/// Actors match messages by type, so each message is a record of its own and never a shared envelope.
/// </summary>
public static class Records
{
    /// <summary>The JSON options Morgana reads and writes its own shapes with: camel-case enums, any casing and metadata in any order.</summary>
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

    /// <summary>The turn's answer as the supervisor hands it to the manager for the channel.</summary>
    /// <param name="Response">The answer's text.</param>
    /// <param name="Classification">The intent the turn was classified as; null when no classification ran.</param>
    /// <param name="Metadata">The classification's metadata.</param>
    /// <param name="AgentName">The agent's display name, such as <c>Morgana (Billing)</c>.</param>
    /// <param name="AgentCompleted">Whether the agent handed the conversation back to Morgana.</param>
    /// <param name="QuickReplies">The buttons beneath the answer.</param>
    /// <param name="RecordedTimestamp">
    /// The instant the agent recorded this reply in its session, so the reply pushed live and the same reply read back later are recognisably one;
    /// null when Morgana said it herself (a refusal or a disambiguation) and dates it on her own record.
    /// </param>
    /// <param name="RichCard">The card beneath the answer.</param>
    internal record ConversationResponse(
        string Response,
        string? Classification,
        Dictionary<string, string>? Metadata,
        string? AgentName = null,
        bool AgentCompleted = false,
        List<QuickReply>? QuickReplies = null,
        DateTime? RecordedTimestamp = null,
        RichCard? RichCard = null);

    /// <summary>
    /// Starts a conversation: creates its supervisor and has Morgana present herself.
    /// The handshake is already settled on record by the time it is sent, so it carries none.
    /// </summary>
    /// <param name="ConversationId">The new conversation.</param>
    public record CreateConversation(
        string ConversationId);

    /// <summary>Ends a conversation, stopping every actor it runs.</summary>
    /// <param name="ConversationId">The conversation to end.</param>
    public record TerminateConversation(
        string ConversationId);

    // ==========================================================================
    // CONVERSATION PERSISTENCE
    // ==========================================================================

    /// <summary>The policy of <c>Morgana:ConversationPersistence</c>: where each conversation's database lives and the key its rows are encrypted with.</summary>
    public record ConversationPersistenceOptions
    {
        /// <summary>The directory holding one database per conversation, created when missing.</summary>
        /// <example>C:/MorganaData</example>
        public string StoragePath { get; set; } = string.Empty;

        /// <summary>
        /// The AES-256 key of every row, base64 over 32 bytes.
        /// It arrives through User Secrets or the environment and is never committed.
        /// </summary>
        /// <example>3q2+7w8e9r0t1y2u3i4o5p6a7s8d9f0g1h2j3k4l5z6x7c8v9b0n1m2==</example>
        public string EncryptionKey { get; set; } = string.Empty;
    }

    /// <summary>Asks the router for the actor of the agent a conversation was left with, answered by a <see cref="RestoreAgentResponse"/>.</summary>
    /// <param name="AgentIntent">The intent of the agent to restore.</param>
    internal record RestoreAgentRequest(string AgentIntent);

    /// <summary>The router's answer to a <see cref="RestoreAgentRequest"/>.</summary>
    /// <param name="AgentIntent">The intent that was asked for.</param>
    /// <param name="AgentRef">The agent's actor; null when no agent of this installation handles the intent.</param>
    internal record RestoreAgentResponse(string AgentIntent, IActorRef? AgentRef);

    // ==========================================================================
    // RATE LIMITING
    // ==========================================================================

    /// <summary>The policy of <c>Morgana:RateLimiting</c>: the switch, a cap per sliding window and the refusal each window answers with.</summary>
    public record RateLimitOptions
    {
        /// <summary>Whether a conversation's requests are limited at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The requests a conversation may send in a sliding minute; 0 lifts this cap.</summary>
        /// <example>5</example>
        public int MaxMessagesPerMinute { get; set; } = 5;

        /// <summary>The requests a conversation may send in a sliding hour; 0 lifts this cap.</summary>
        /// <example>30</example>
        public int MaxMessagesPerHour { get; set; } = 30;

        /// <summary>The requests a conversation may send in a sliding day; 0 lifts this cap.</summary>
        /// <example>80</example>
        public int MaxMessagesPerDay { get; set; } = 80;

        /// <summary>What the user reads when the minute's cap is reached; <c>{limit}</c> stands for the cap.</summary>
        /// <example>✋ Whoa there! You're casting spells too quickly. Please wait a moment before trying again.</example>
        public string ErrorMessagePerMinute { get; set; } =
            "✋ Whoa there! You're casting spells too quickly. Please wait a moment before trying again.";

        /// <summary>What the user reads when the hour's cap is reached; <c>{limit}</c> stands for the cap.</summary>
        /// <example>⏰ You've reached your hourly spell quota. The magic cauldron needs time to recharge!</example>
        public string ErrorMessagePerHour { get; set; } =
            "⏰ You've reached your hourly spell quota. The magic cauldron needs time to recharge!";

        /// <summary>What the user reads when the day's cap is reached; <c>{limit}</c> stands for the cap.</summary>
        /// <example>🌙 You've exhausted today's magical energy. Return tomorrow for more spells!</example>
        public string ErrorMessagePerDay { get; set; } =
            "🌙 You've exhausted today's magical energy. Return tomorrow for more spells!";

        /// <summary>What the user reads when a refusal names no window.</summary>
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

    /// <summary>The verdict of a rate check: an admission or the window whose cap was reached, with when to retry.</summary>
    /// <param name="IsAllowed">Whether the request may proceed.</param>
    /// <param name="ViolatedWindow">The window whose cap was reached (null if allowed): it picks the refusal the user reads.</param>
    /// <param name="ViolatedCap">The cap of that window (null if allowed), which the refusal may quote.</param>
    /// <param name="RetryAfterSeconds">The seconds until that window has room again (null if allowed).</param>
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
    /// <c>MorganaLanguageModel.ConvertToMorganaDust</c> derives it from the tier's <see cref="ModelPricing"/>, so it is never configured.
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
    /// The three models an agent declares itself against through <see cref="Attributes.RequiresLLMTierAttribute"/>, with no fallback from one to another.
    /// Morgana's own actors run on the tier that <c>Morgana:ActorSystem:Tier</c> names.
    /// </summary>
    public enum LLMTier
    {
        /// <summary>The cheapest model, for routine work that needs no deliberate reasoning.</summary>
        Economy,

        /// <summary>
        /// The tier of any domain agent handling ordinary work.
        /// An agent belongs here unless its author can point to an existential need for what only Performance provides.
        /// </summary>
        Efficiency,

        /// <summary>
        /// The most capable model, reserved for agents whose author declares an existential need for deep reasoning.
        /// It is a requirement the agent cannot function without, never a nicer-to-have upgrade from Efficiency.
        /// </summary>
        Performance
    }

    /// <summary>A tier as it is served: its client, the provider that built the client and its dust.</summary>
    internal record ServedTier(IChatClient Client, MorganaLanguageModel LanguageModel, MagicDustPricing MorganaDust);

    /// <summary>
    /// The options of <see cref="ChatOptions"/> that a model carries, in a provider's <c>models.json</c> and on a tier alike.
    /// Tools, tool mode, instructions and response format are left out, so a deployment cannot force a tool call or rewrite what an agent was told.
    /// </summary>
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
    internal record ModelDefinition(ModelOptions Options, ModelPricing Pricing);

    /// <summary>
    /// The policy of <c>Morgana:DustLimiting</c>: a conversation's lifetime budget (never reset) and what the user reads as it runs out.
    /// In each message <c>{percent}</c> stands for what is left, from 0 to 100.
    /// </summary>
    public record DustLimitingOptions
    {
        /// <summary>Whether the budget applies; off, every turn and command is admitted unmetered.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The dust a conversation may consume over its lifetime.
        /// Zero is a valid budget, spent from the start, so no turn or command is ever admitted.
        /// </summary>
        public double BudgetPerConversation { get; set; }

        /// <summary>The notice shown once when consumption crosses 70%.</summary>
        public string Warning70Message { get; set; } =
            "🧙‍♀️ The magic dust in the cauldron is running low ({percent}% left)...";

        /// <summary>The notice shown once when consumption crosses 90%.</summary>
        public string Warning90Message { get; set; } =
            "🧙‍♀️ The cauldron is almost empty — wrap up soon, the dust is nearly gone ({percent}% left).";

        /// <summary>The refusal shown once the budget is spent.</summary>
        public string ErrorMessage { get; set; } =
            "🧙‍♀️ The magic dust from this conversation has run out. This conversation is now complete — start a new one to keep going.";
    }

    // ==========================================================================
    // AUTHENTICATION
    // ==========================================================================

    /// <summary>
    /// The policy of <c>Morgana:Authentication</c>: the channels admitted with a key each and the audience every token must name.
    /// One key per channel keeps a leak to one channel, while a partner is declared under <see cref="PartnerOptions"/> and never here.
    /// </summary>
    public record AuthenticationOptions
    {
        /// <summary>The channels this installation accepts tokens from; a token whose <c>iss</c> names none of them and no partner is refused.</summary>
        public List<IssuerOptions> Issuers { get; set; } = [];

        /// <summary>The <c>aud</c> claim every token must carry.</summary>
        /// <example>morgana.ai</example>
        public string Audience { get; set; } = "morgana.ai";
    }

    /// <summary>One channel this installation serves: the name its tokens carry in <c>iss</c> and the key they are signed with.</summary>
    public record IssuerOptions
    {
        /// <summary>The channel's name as its tokens carry it in <c>iss</c>, such as <c>cauldron</c>.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The HMAC-SHA256 key the channel signs with, at least 256 bits.
        /// It arrives through User Secrets or the environment and is never committed.
        /// </summary>
        public string SymmetricKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// One installation this one federates with: who it is, where it answers, the secret both share and what each direction allows.
    /// Both directions are off until a policy opens them, so an entry alone grants nothing; it opens as many colleagues as the partner serves.
    /// </summary>
    public record PartnerOptions
    {
        /// <summary>
        /// The name the partner is known by here: what <c>[ConsultsAgent]</c> writes and, unless a policy overrides it, the <c>iss</c> its calls arrive under.
        /// It is never a hostname, since where an agent runs is deployment; <c>morgana</c> is reserved for this installation's own agents.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The absolute address the partner answers on, ahead of the agent path the consulted intent appends; required only to call it.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// The HMAC-SHA256 secret both installations share, at least 256 bits: calls to the partner are signed with it and its calls are proven against it.
        /// One key per partner, so a leak costs this relationship and no other.
        /// </summary>
        public string SymmetricKey { get; set; } = string.Empty;

        /// <summary>
        /// Whether the partner exists at all.
        /// Off parks the relationship, its agents unreachable and its calls refused, without deleting the declaration.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Whether and how this installation may call the partner; absent, it may not.</summary>
        public PartnerOutboundPolicy? OutboundPolicy { get; set; }

        /// <summary>Whether and how far the partner may call this installation; absent, it may not.</summary>
        public PartnerInboundPolicy? InboundPolicy { get; set; }
    }

    /// <summary>What this installation may do toward a partner: consult its published agents or nothing at all.</summary>
    public record PartnerOutboundPolicy
    {
        /// <summary>
        /// Whether agents here may consult the partner's.
        /// Off, a <c>[ConsultsAgent]</c> naming it is refused at startup rather than failing on the first conversation.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The name the partner knows this installation by, which its gate expects in <c>iss</c>; unset only for a partner whose card asks no credentials.
        /// A card served to everyone cannot state the name one caller was filed under, so it travels out of band with its key.
        /// </summary>
        public string? Issuer { get; set; }

        /// <summary>
        /// The audience the partner validates a token against, when it is not this installation's own.
        /// Unset in the ordinary case, since two installations share the shipped default until one changes it.
        /// </summary>
        public string? Audience { get; set; }
    }

    /// <summary>What a partner may do toward this installation: which agents it reaches and how often it may open an exchange at them.</summary>
    public record PartnerInboundPolicy
    {
        /// <summary>
        /// Whether the partner is admitted at all.
        /// Off, its key proves who it is and opens nothing, which is what a purely outbound relationship looks like.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>The name the partner's calls arrive under, when it signs under another than the one it is known by here; unset in the ordinary case.</summary>
        public string? Issuer { get; set; }

        /// <summary>
        /// The published agents the partner may consult; <c>null</c> admits it to every one.
        /// Publication stays whole and what narrows is admission.
        /// </summary>
        public List<string>? OnAgents { get; set; }

        /// <summary>
        /// How often the partner may open an exchange here.
        /// It is required whenever the partner is admitted, since an absent declaration is never read as licence to spend without limit.
        /// </summary>
        public PartnerRateLimitingOptions? RateLimiting { get; set; }
    }

    /// <summary>
    /// How many conversations one admitted partner may open within a sliding hour.
    /// A partner names the conversation it is served on, so rotating names would draw a fresh budget each time: what is bounded is how many may start.
    /// </summary>
    public record PartnerRateLimitingOptions
    {
        /// <summary>
        /// Whether the limit applies.
        /// Off is a deployment saying in as many words that the partner may open what it likes, which an omission never says.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>The conversations the partner may open within a sliding hour; required when the limit applies.</summary>
        public int? MaxConversationsPerHour { get; set; }

        /// <summary>
        /// What the partner is told once it has opened all it may.
        /// It reaches the asking agent as the colleague's own answer, an agent that cannot take the question rather than an error code.
        /// </summary>
        public string ErrorMessagePerHour { get; set; } =
            "This agent cannot take on further conversations right now. Proceed without it.";
    }

    /// <summary>The verdict on a bearer token: who the caller is and which list proved it, otherwise why it was refused.</summary>
    /// <param name="IsAuthenticated">Whether the token was proven.</param>
    /// <param name="CallerId">The caller from the <c>sub</c> claim; null when refused.</param>
    /// <param name="DisplayName">The caller's name from the <c>name</c> claim; null when refused.</param>
    /// <param name="Error">Why the token was refused; null when proven.</param>
    /// <param name="Issuer">The proven <c>iss</c>, set only on success, so a gate can admit some issuers and not others.</param>
    /// <param name="IsPartner">
    /// Whether a partner's key proved the caller rather than a channel's, which follows from the list the key was found in:
    /// the conversation API serves channels, the A2A door serves partners and neither takes the other's key.
    /// </param>
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

    /// <summary>A user's message entering the turn pipeline.</summary>
    /// <param name="ConversationId">The conversation it belongs to.</param>
    /// <param name="Text">What the user wrote.</param>
    /// <param name="Timestamp">When the message arrived.</param>
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

    /// <summary>Asks the guard actor for its verdict on a user's message.</summary>
    /// <param name="ConversationId">The conversation the message belongs to.</param>
    /// <param name="Message">What the user wrote.</param>
    /// <param name="TurnCancellation">Stops the work of this request once whoever asked for it stops waiting.</param>
    internal record GuardCheckRequest(
        string ConversationId,
        string Message,
        CancellationToken TurnCancellation = default);

    /// <summary>
    /// The verdict in the JSON shape the Guard prompt asks the model for; read by <c>LLMGuardRailService</c> alone.
    /// </summary>
    /// <param name="Compliant">Whether the text passes.</param>
    /// <param name="Violation">The finding when it does not.</param>
    internal record GuardCheckResponse(
        [property: JsonPropertyName("compliant")] bool Compliant,
        [property: JsonPropertyName("violation")] string? Violation);

    /// <summary>
    /// What the ToolGuard inspector reads: the tool's name, the arguments the agent called it with and what it returned, as a JSON object.
    /// </summary>
    /// <remarks>The three JSON names are a contract with the ToolGuard prompt's Target sentence.</remarks>
    /// <param name="Tool">The name of the tool that answered; together with "arguments" it is the call the result answers.</param>
    /// <param name="Arguments">The arguments the agent called the tool with; for a partner consultation they are the question.</param>
    /// <param name="Result">What the tool returned.</param>
    internal record ToolGuardSubject(
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("arguments")] JsonElement Arguments,
        [property: JsonPropertyName("result")] JsonElement Result);

    /// <summary>
    /// What the UserGuard reads: the answer the user received last and the message they wrote after it, as a JSON object.
    /// </summary>
    /// <remarks>The two JSON names are a contract with the UserGuard prompt's Target sentence.</remarks>
    /// <param name="Answer">The last answer the channel delivered to the conversation; left out when it is not at hand, since a null would read as a claim that nobody has spoken.</param>
    /// <param name="Message">What the user wrote.</param>
    internal record UserGuardSubject(
        [property: JsonPropertyName("answer")] UserGuardAnswer? Answer,
        [property: JsonPropertyName("message")] string Message);

    /// <summary>
    /// The answer as the user saw it, which the UserGuard reads to tell what a message refers to.
    /// </summary>
    /// <param name="Text">The text of the answer.</param>
    /// <param name="Card">The rich card the answer carried; omitted when there was none.</param>
    /// <param name="Buttons">The labels of the buttons the user saw; null when the answer carried none.</param>
    internal record UserGuardAnswer(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("card")] RichCard? Card,
        [property: JsonPropertyName("buttons")] IReadOnlyList<string>? Buttons);

    /// <summary>
    /// What the ToolGuard inspector reads when an external source offers tools: the source and the tools it offers, as a JSON object.
    /// </summary>
    /// <remarks>The JSON names are a contract with the ToolGuard prompt's Target sentence about the offer.</remarks>
    /// <param name="Source">The server or partner that makes the offer.</param>
    /// <param name="Tools">The tools the source offers.</param>
    internal record ToolGuardOffer(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("tools")] IReadOnlyList<ToolGuardOfferedTool> Tools);

    /// <summary>
    /// One tool of an offer, as the source describes it.
    /// </summary>
    /// <remarks>The JSON names are a contract with the ToolGuard prompt's Target sentence about the offer.</remarks>
    /// <param name="Name">The name under which the model would call the tool.</param>
    /// <param name="Description">The description the source gives the tool.</param>
    /// <param name="Parameters">The tool's input schema as the source sent it; null for a partner's function, whose schema is Morgana's own.</param>
    internal record ToolGuardOfferedTool(
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
    internal record PeerGuardSubject(
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

        /// <summary>No judgment could be reached and the text was refused.</summary>
        FailClosed,

        /// <summary>The guard is switched off and the text was admitted unjudged.</summary>
        Disabled
    }

    /// <summary>
    /// The verdict of a guard: whether the text passes, the finding when it does not, which layer decided and the technical cause when no judgment was reached.
    /// </summary>
    /// <param name="Compliant">Whether the text passes every check.</param>
    /// <param name="Violation">The finding when it does not.</param>
    /// <param name="Source">The layer that decided.</param>
    /// <param name="ErrorType">The technical cause when no judgment could be reached: a timeout, an unreadable verdict or the exception's full name; null on every judgment.</param>
    public record GuardRailResult(
        bool Compliant,
        string? Violation,
        GuardVerdictSource Source,
        string? ErrorType = null);

    /// <summary>
    /// Tells the supervisor that the provider of the agent's tier refused the request on its own content filter.
    /// The supervisor answers it as it answers a guard's refusal.
    /// </summary>
    internal record ContentFilterRejection;

    // ==========================================================================
    // CLASSIFICATION MESSAGES
    // ==========================================================================

    /// <summary>
    /// The classifier model's answer: every intent it judged plausible, most confident first.
    /// One entry for a clean match, two or more only for a genuine collision (see <see cref="Services.LLMClassifierService"/>).
    /// </summary>
    /// <param name="Intents">The candidate intents, most confident first.</param>
    internal record ClassificationResponse(
        [property: JsonPropertyName("intents")] List<IntentScore> Intents);

    /// <summary>One candidate intent with the confidence the classifier model gave it.</summary>
    /// <param name="Intent">The intent, such as <c>billing</c> or <see cref="Constants.Intents.Other"/>.</param>
    /// <param name="Confidence">How well this intent matches, from 0 to 1, never a rank.</param>
    internal record IntentScore(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("confidence")] double Confidence);

    /// <summary>The classification as the pipeline routes on it: the intent and the metadata the supervisor and the router read.</summary>
    /// <param name="Intent">The most confident candidate.</param>
    /// <param name="Metadata">The keys of <see cref="Constants.ClassificationMetadata"/>: the confidence, the colliding intents and the failure mark.</param>
    public record ClassificationResult(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("metadata")] Dictionary<string, string> Metadata);

    // ==========================================================================
    // AGENT REQUEST/RESPONSE MODELS
    // ==========================================================================

    /// <summary>A turn handed to an agent: the user's message with what the agent needs to answer it.</summary>
    /// <param name="ConversationId">Conversation the request belongs to.</param>
    /// <param name="Content">The user's message.</param>
    /// <param name="Classification">The classified intent; null for a follow-up to the active agent.</param>
    /// <param name="TurnContext">OpenTelemetry activity context of the current turn span.</param>
    /// <param name="Capabilities">What the channel can show; null means full capability.</param>
    /// <param name="ContentAlreadyStored">True when the user's phrase was already saved as Morgana's own.</param>
    /// <param name="IsLanding">True when the user chose this agent by pressing Morgana's button, so the turn opens with the agent presenting itself.</param>
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

    /// <summary>An agent's answer to its turn.</summary>
    /// <param name="Response">The answer's text.</param>
    /// <param name="IsCompleted">Whether the agent hands the conversation back to Morgana; false keeps it in service.</param>
    /// <param name="QuickReplies">The buttons beneath the answer.</param>
    /// <param name="RichCard">The card beneath the answer.</param>
    /// <param name="RecordedTimestamp">The instant the agent's session keeps this reply under, so the history and the live push date it identically; null when nothing was recorded.</param>
    public record AgentResponse(
        string Response,
        bool IsCompleted = true,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null,
        DateTime? RecordedTimestamp = null);

    /// <summary>An agent's answer as the router hands it to the supervisor, with the actor that wrote it, which the next turn goes to.</summary>
    /// <param name="Response">The answer's text.</param>
    /// <param name="IsCompleted">Whether the agent hands the conversation back to Morgana.</param>
    /// <param name="AgentRef">The agent's actor.</param>
    /// <param name="QuickReplies">The buttons beneath the answer.</param>
    /// <param name="RichCard">The card beneath the answer.</param>
    /// <param name="RecordedTimestamp">The instant the agent's session keeps this reply under.</param>
    internal record ActiveAgentResponse(
        string Response,
        bool IsCompleted,
        IActorRef AgentRef,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null,
        DateTime? RecordedTimestamp = null);

    /// <summary>A piece of an agent's answer as it streams, pushed to the channel as it arrives.</summary>
    /// <param name="Text">The text of this piece.</param>
    internal record AgentStreamChunk(
        string Text);

    /// <summary>
    /// Reports that an agent's turn is advancing on something the user cannot see, such as a tool running or a colleague being consulted.
    /// The supervisor's wait is a budget on silence that every chunk renews, so without it a consultation would read as a dead agent.
    /// </summary>
    internal record AgentStillWorking;

    // ==========================================================================
    // PEER CONSULTATION MODELS
    // ==========================================================================

    /// <summary>
    /// How long each party to a turn waits, as one ladder derived from the pipeline's own turn budget.
    /// The colleague gives up first, then the asking agent, then the supervisor, while the channel waits longest because it alone cannot see Morgana's phases.
    /// </summary>
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

        /// <summary>Reads the whole ladder off the pipeline's turn budget, the only thing configured.</summary>
        /// <param name="configuration">Application configuration, read for the turn budget.</param>
        public static TurnWaits From(IConfiguration configuration)
        {
            // The turn budget is the one configured number every other rung derives from.
            TimeSpan turn = TimeSpan.FromSeconds(configuration.GetValue("Morgana:ActorSystem:TimeoutSeconds", 180));

            // Callee and caller sit inside the turn while the channel sits outside it, so it always expires after Morgana and never together with her.
            return new TurnWaits(turn, turn * CallerShareOfTurn, turn * CalleeShareOfTurn, turn * PhasesBeforeFirstWord + DeliveryAllowance);
        }
    }

    /// <summary>Names one consultable colleague by the pair of its intent and its partner.</summary>
    /// <param name="Intent">The intent the colleague handles, as its own installation publishes it.</param>
    /// <param name="Instance">The partner publishing it, declared in <c>Morgana:AgentToAgent:Partners</c>; <c>null</c> for an agent of this installation.</param>
    public record PeerReference(string Intent, string? Instance = null);

    /// <summary>
    /// What MorganaAgentAdapter builds for one agent: the agent that runs its turns and the two providers those turns read and write.
    /// </summary>
    /// <param name="Agent">Agent that runs the turns.</param>
    /// <param name="ContextProvider">Provider holding the session variables and the shared-context write callback.</param>
    /// <param name="HistoryProvider">Provider exposing the chat history of the session.</param>
    internal record CreatedAgent(AIAgent Agent, MorganaAIContextProvider ContextProvider, MorganaChatHistoryProvider HistoryProvider);

    /// <summary>
    /// Whether a partner may open one more exchange here and what it is told when it may not.
    /// </summary>
    /// <param name="IsAdmitted">Whether the conversation may be opened.</param>
    /// <param name="RefusalMessage">What the partner reads instead, set only on a refusal and written by the deployment on that partner's own entry.</param>
    public record PeerAdmissionResult(bool IsAdmitted, string? RefusalMessage = null);

    /// <summary>A question put to an agent of this installation over A2A, answered with a <see cref="PeerConsultationResponse"/>.</summary>
    /// <param name="ConversationId">The conversation the question is served on, which scopes the answering agent's session.</param>
    /// <param name="CallerIntent">The intent of the asking agent; <c>null</c> when the caller named none.</param>
    /// <param name="Question">The question with the consultation declaration in front of it, since the answering agent's prompt says nothing about serving a colleague.</param>
    /// <param name="TurnContext">The trace context of the turn that asked.</param>
    /// <param name="TurnCancellation">Stops the work of this request once whoever asked for it stops waiting.</param>
    public record PeerConsultation(
        string ConversationId,
        string? CallerIntent,
        string Question,
        ActivityContext TurnContext = default,
        CancellationToken TurnCancellation = default);

    /// <summary>
    /// A colleague's answer, both as the actor's reply and, serialized, as the tool result the asking agent's model reads.
    /// Every member carries an explicit JSON name because the asking model reads those names.
    /// </summary>
    /// <param name="Answer">The colleague's own words, as its turn wrote them for whoever asked.</param>
    /// <param name="ColleagueAwaitsYourReply">True when the colleague is waiting, i.e. the exchange is unfinished.</param>
    /// <param name="Options">Options offered, as data to choose from — never buttons to render.</param>
    /// <param name="Card">Structured data presented, as data to read — never a card to render.</param>
    /// <param name="DustConsumed">What answering cost the colleague, set only for a caller that declared itself an agent of a Morgana and taken off before any model reads the answer.</param>
    public record PeerConsultationResponse(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("colleagueAwaitsYourReply")] bool ColleagueAwaitsYourReply,
        [property: JsonPropertyName("options")] List<QuickReply>? Options = null,
        [property: JsonPropertyName("card")] RichCard? Card = null,
        [property: JsonPropertyName("dustConsumed")] double? DustConsumed = null);

    /// <summary>
    /// The presentation model's answer: the two texts of Morgana's presentation.
    /// The buttons beneath them are never the model's: they are the configured intents.
    /// </summary>
    /// <param name="Message">The welcome.</param>
    /// <param name="Reintroduction">The same presentation without greeting, for a user who asks again what Morgana can do.</param>
    internal record PresentationResponse(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("reintroduction")] string Reintroduction);

    /// <summary>The ChannelAdapter model's rewrite of a message the channel cannot show as it is.</summary>
    /// <param name="Text">The rewritten text, within the channel's capabilities.</param>
    /// <param name="QuickReplies">The buttons the rewrite kept; null when it kept none.</param>
    internal record ChannelAdapterResponse(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("quickReplies")] List<QuickReply>? QuickReplies);

    /// <summary>Morgana's presentation as <see cref="Interfaces.IPresenterService.GenerateAsync"/> returns it, with the buttons of the displayable intents.</summary>
    /// <param name="Message">The welcome.</param>
    /// <param name="Reintroduction">The same presentation without greeting, served when a request lands on the complement intent.</param>
    /// <param name="QuickReplies">One button per displayable intent; empty when none is configured, never null.</param>
    public record PresentationResult(
        string Message,
        string Reintroduction,
        List<QuickReply> QuickReplies);

    // ==========================================================================
    // PRESENTATION FLOW MESSAGES
    // ==========================================================================

    /// <summary>Has the supervisor generate and send Morgana's welcome, as it does when the conversation is created.</summary>
    internal record GeneratePresentationMessage;

    /// <summary>The generated welcome, sent by the supervisor to itself so it reaches the channel from the actor's own mailbox.</summary>
    /// <param name="Message">The welcome, generated or the fallback.</param>
    /// <param name="Intents">The displayable intents.</param>
    internal record PresentationContext(
        string Message,
        List<IntentDefinition> Intents)
    {
        /// <summary>The buttons beneath the welcome, one per displayable intent; null sends the welcome with none.</summary>
        public List<QuickReply>? QuickReplies { get; init; }
    }

    // ==========================================================================
    // TURN CONTEXTS
    // ==========================================================================
    // An asynchronous step loses the sender it was asked by, so these carry it through the actor's states.

    /// <summary>The turn as the supervisor carries it through its states: the message, who to answer and what each state added.</summary>
    /// <param name="OriginalMessage">The user's message.</param>
    /// <param name="OriginalSender">The actor the answer goes back to, the conversation's manager.</param>
    /// <param name="ChannelCapabilities">The channel's capabilities for this turn, stamped on every agent request.</param>
    /// <param name="Classification">The classification, once the classifier has answered.</param>
    /// <param name="TurnContext">The trace context of the turn span.</param>
    /// <param name="UserMessageAlreadyStored">
    /// True when no agent was active as the turn arrived, which is when Morgana saves the phrase on her own side.
    /// It is settled once at ingress, since an agent engaged a moment later would otherwise make the answer unreadable.
    /// </param>
    internal record ProcessingContext(
        UserMessage OriginalMessage,
        IActorRef OriginalSender,
        ChannelCapabilities ChannelCapabilities,
        ClassificationResult? Classification = null,
        ActivityContext TurnContext = default,
        bool UserMessageAlreadyStored = false);

    /// <summary>A failed agent turn, sent by the agent to itself so it answers the failure from its own mailbox and still knows who asked.</summary>
    /// <param name="Failure">The failure carrying the exception.</param>
    /// <param name="OriginalSender">The actor that sent the turn, the router.</param>
    internal record FailureContext(
        Status.Failure Failure,
        IActorRef OriginalSender);

    // ==========================================================================
    // INTENT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>One intent as a plugin's agents.json declares it: what the classifier reads and the button that offers it.</summary>
    /// <param name="Name">The intent, lowercase, such as <c>billing</c>.</param>
    /// <param name="Description">What the classifier model reads about the intent.</param>
    /// <param name="Label">The text of the button that offers the intent, such as <c>📄 Billing</c>.</param>
    /// <param name="DefaultValue">The message a press of that button sends.</param>
    public record IntentDefinition(
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Description")] string Description,
        [property: JsonPropertyName("Label")] string Label,
        [property: JsonPropertyName("DefaultValue")] string DefaultValue);

    /// <summary>The configured intents, read by the classifier as descriptions and by the presenter as buttons.</summary>
    /// <param name="Intents">Every configured intent.</param>
    internal record IntentCollection(List<IntentDefinition> Intents)
    {
        /// <summary>Each intent's description by name, as the classifier prompt lists them.</summary>
        public Dictionary<string, string> AsDictionary()
            => Intents.ToDictionary(intent => intent.Name, intent => intent.Description);

        /// <summary>The intents a button may offer: every one with a label except the complement intent.</summary>
        public List<IntentDefinition> GetDisplayableIntents()
            => [.. Intents.Where(intent => !string.Equals(intent.Name, Constants.Intents.Other, StringComparison.OrdinalIgnoreCase)
                                           && !string.IsNullOrEmpty(intent.Label))];
    }

    // ==========================================================================
    // PROMPT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>The root of morgana.json and of a plugin's agents.json.</summary>
    /// <param name="Prompts">The prompts the file declares.</param>
    public record PromptCollection(
        Prompt[] Prompts);

    /// <summary>One prompt as morgana.json or a plugin's agents.json declares it: its authored sections and the properties beside them.</summary>
    /// <param name="ID">One of <see cref="Constants.Prompts.All"/> for a framework prompt, the intent for a domain one.</param>
    /// <param name="Target">What the agent is for.</param>
    /// <param name="Instructions">How the agent goes about its work.</param>
    /// <param name="Formatting">How the agent presents what it finds.</param>
    /// <param name="Personality">How the agent speaks.</param>
    /// <param name="Territory">What falls to the agent, addressed to a colleague who might consult it.</param>
    /// <param name="Language">The prompt's BCP 47 language, such as <c>en-US</c>.</param>
    /// <param name="Version">The prompt's version.</param>
    /// <param name="AdditionalProperties">The properties beside the sections, keyed by <see cref="Constants.PromptProperties"/>.</param>
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
        /// <summary>Reads a property the prompt must declare, throwing when it is absent or null.</summary>
        public T GetAdditionalProperty<T>(string additionalPropertyName)
        {
            // The properties arrive as a list of objects, each declaring some of them.
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

            // A property the framework needs and the prompt does not declare is a defect of the prompt file.
            throw new KeyNotFoundException($"AdditionalProperty with key '{additionalPropertyName}' was not found in the prompt with id='{ID}'");
        }

        /// <summary>Puts a section's label in front of its text, as every composed prompt shows it; empty for a section that says nothing.</summary>
        /// <param name="label">The section's label.</param>
        /// <param name="text">The section as authored.</param>
        public static string Labeled(string label, string? text)
        {
            // An empty section adds neither label nor text, so no prompt shows a bare label.
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // A plugin's agents.json may still open a section with its own label: it is kept once, never doubled.
            string trimmed = text.Trim();
            return trimmed.StartsWith(label, StringComparison.Ordinal) ? trimmed : $"{label} {trimmed}";
        }

        /// <summary>Reads a property the prompt may leave out, answering <paramref name="defaultValue"/> when it does.</summary>
        /// <typeparam name="T">The type the property is read into.</typeparam>
        /// <param name="additionalPropertyName">The property to read.</param>
        /// <param name="defaultValue">The answer when the prompt does not declare it.</param>
        public T GetAdditionalPropertyOrDefault<T>(string additionalPropertyName, T defaultValue)
        {
            // The properties arrive as a list of objects, each declaring some of them.
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties ?? [])
            {
                // A declared property arrives as the JSON element the prompt file holds for it.
                if (additionalProperties.TryGetValue(additionalPropertyName, out object? value) && value is JsonElement element)
                {
                    // A property declared as null counts as one left out.
                    return element.Deserialize<T>() ?? defaultValue;
                }
            }

            // Left out, which is a legitimate authoring choice for this property.
            return defaultValue;
        }
    }

    /// <summary>
    /// One framework rule, rendered among the global policies of every agent's prompt.
    /// Every policy binds equally, so all that is left to declare is where it is read.
    /// </summary>
    /// <param name="Name">The policy's name.</param>
    /// <param name="Description">The rule as the model reads it.</param>
    /// <param name="Priority">Where the policy is rendered in the block, lowest first.</param>
    public record GlobalPolicy(
        string Name,
        string Description,
        int Priority);

    /// <summary>
    /// One framework template, spliced at the single site where it has a referent and never rendered among the policies.
    /// The array an entry lives in says whether it is a rule or a template, so nothing written inside an entry can confuse the two.
    /// </summary>
    /// <param name="Name">The template's name, which code fetches it by.</param>
    /// <param name="Description">The template's text.</param>
    public record Injection(
        string Name,
        string Description)
    {
        /// <summary>Resolves a template's text by name; empty when none is declared, which every splice site reads as nothing to inject.</summary>
        public static string ResolveTemplate(IEnumerable<Injection> injections, string name)
            => injections.FirstOrDefault(injection =>
                   string.Equals(injection.Name, name, StringComparison.OrdinalIgnoreCase))?.Description ?? "";
    }

    /// <summary>One apology the Morgana prompt declares for a turn that failed, picked by name.</summary>
    /// <param name="Name">One of <see cref="Constants.ErrorAnswers"/>.</param>
    /// <param name="Content">What the user reads.</param>
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
    /// <param name="Name">The method's name, which is the tool's.</param>
    /// <param name="Description">The method's <c>[Description]</c>.</param>
    /// <param name="Parameters">The method's parameters in signature order.</param>
    public record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters);

    /// <summary>A tool parameter as projected from its method.</summary>
    /// <param name="Name">The parameter's name.</param>
    /// <param name="Description">The parameter's <c>[Description]</c>.</param>
    /// <param name="Required">False for a parameter with a default value.</param>
    /// <param name="Scope">One of <see cref="Constants.Scopes"/>; null for a value the model itself authors.</param>
    /// <param name="Shared">Whether a context parameter's value reaches the other agents of the conversation.</param>
    public record ToolParameter(
        string Name,
        string Description,
        bool Required,
        string? Scope,
        bool Shared = false);

    /// <summary>What the scan for tools found: the tool of each intent and one message per intent claimed by two tools.</summary>
    /// <param name="ToolTypesByIntent">The tool type of each intent, lowercased and looked up ignoring case.</param>
    /// <param name="RegistrationErrors">One message per intent that a second tool claimed after the first.</param>
    internal record ToolDiscovery(
        Dictionary<string, Type> ToolTypesByIntent,
        List<string> RegistrationErrors);

    // ==========================================================================
    // MODEL CONTEXT PROTOCOL
    // ==========================================================================

    /// <summary>How <see cref="UsesMCPServerAttribute"/> reaches its MCP server.</summary>
    public enum MCPTransport
    {
        /// <summary>A server reached at a URL over HTTP or HTTPS.</summary>
        Http,

        /// <summary>A local executable started as a child process and spoken to over its standard input and output.</summary>
        Stdio
    }
}