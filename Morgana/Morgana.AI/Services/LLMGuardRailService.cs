using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IGuardRailService"/> implementation. Delegates to <see cref="ILanguageModelService"/>
/// with the Guard system prompt for detection of spam, phishing, violence, profanity and other
/// policy violations. A message is judged against the assistant turn it answers, which is never judged itself.
/// </summary>
public class LLMGuardRailService : IGuardRailService
{
    /// <summary>
    /// LLM used for the policy check. Consumed through the stateless completion path — each
    /// message is judged against the assistant turn it answers, on the framework tier.
    /// </summary>
    private readonly ILanguageModelService llmService;

    /// <summary>
    /// Logger for compliance verdicts and for the fail-open path, which admits a message the
    /// check could not evaluate and would otherwise leave no trace.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Pre-computed Guard system prompt.
    /// </summary>
    private readonly string guardSystemPrompt;

    /// <summary>
    /// The refusal for a message that the provider's own content filter blocked, worded in the Guard prompt's messages.
    /// </summary>
    private readonly string contentFilteredRefusal;

    /// <summary>
    /// The record of the conversation, from which the last turn the user saw is read.
    /// </summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>
    /// Initialises a new instance of <see cref="LLMGuardRailService"/>.
    /// Loads and builds the Guard system prompt eagerly.
    /// </summary>
    /// <param name="llmService">LLM service used for the async policy check.</param>
    /// <param name="promptResolverService">Prompt resolver used to load Guard configuration.</param>
    /// <param name="conversationPersistenceService">Record of the conversation, read for the last turn the user saw.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public LLMGuardRailService(
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService conversationPersistenceService,
        ILogger logger)
    {
        this.llmService = llmService;
        this.conversationPersistenceService = conversationPersistenceService;
        this.logger = logger;

        // The guard gates every user message, so its prompt is needed from the first turn and is
        // resolved here rather than on first use.
        Records.Prompt guardPrompt =
            promptResolverService.ResolveAsync(Constants.Prompts.Guard).GetAwaiter().GetResult();

        // A refusal is Morgana speaking to the user, so it is worded in the voice her agents speak in.
        Records.Prompt morganaPrompt =
            promptResolverService.ResolveAsync(Constants.Morgana).GetAwaiter().GetResult();

        // What the Guard reads before every user message. No placeholder to splice, unlike the
        // classifier's: what is admissible is a policy of the framework, not of a domain.
        guardSystemPrompt = string.Join("\n",
            Records.Prompt.Labeled(Constants.SectionLabels.Target, guardPrompt.Target),
            Records.Prompt.Labeled(Constants.SectionLabels.Personality, morganaPrompt.Personality),
            Records.Prompt.Labeled(Constants.SectionLabels.Instructions, guardPrompt.Instructions),
            Records.Prompt.Labeled(Constants.SectionLabels.Formatting, guardPrompt.Formatting));

        // The one refusal the guard words itself rather than the model: the provider blocked the message before any judgment.
        contentFilteredRefusal = guardPrompt.GetMessage(Constants.Messages.ContentFiltered);
    }

    /// <summary>
    /// The assistant's last turn as the user saw it, its text followed by its card and its buttons; null when nobody has answered yet or the record cannot be read.
    /// </summary>
    private async Task<ChatMessage?> GetPrecedingAssistantMessageAsync(string conversationId)
    {
        MorganaChatMessage[] history;
        try
        {
            // The whole record, from which the last assistant turn is picked below.
            history = await conversationPersistenceService.GetConversationHistoryAsync(conversationId);
        }
        catch (Exception ex)
        {
            // A record that cannot be read must not cost the user their turn, so the message is judged without context.
            logger.LogWarning(ex,
                "LLMGuardRailService: the preceding turn could not be read for conversation {ConversationId}; the message is judged alone",
                conversationId);

            // No context is handed to the judgment.
            return null;
        }

        // The user's own phrase may already be on record when the guard runs, which is why the last assistant message is searched rather than the last message.
        MorganaChatMessage? lastAssistantMessage = history.LastOrDefault(message => message.Type == ChatMessageType.Assistant);

        // Nobody has answered yet: the message opens the conversation and has nothing to refer to.
        if (lastAssistantMessage is null)
            return null;

        // The text comes first because it is what the user read before anything else.
        List<AIContent> contents = [new TextContent(lastAssistantMessage.Text)];

        // The card travels as JSON, the form in which the channel received it.
        if (lastAssistantMessage.RichCard is not null)
            contents.Add(new TextContent(JsonSerializer.Serialize(lastAssistantMessage.RichCard, Records.DefaultJsonSerializerOptions)));

        // The buttons travel as the JSON list of their labels, which is all the user saw of them.
        if (lastAssistantMessage.QuickReplies?.Any() == true)
            contents.Add(new TextContent(JsonSerializer.Serialize(lastAssistantMessage.QuickReplies.Select(quickReply => quickReply.Label).ToList(), Records.DefaultJsonSerializerOptions)));

        // The turn as one assistant message, placed before the user's own.
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    /// <inheritdoc/>
    public async Task<Records.GuardRailResult> CheckAsync(string conversationId, string message)
    {
        try
        {
            // The turn the user is answering, so that a reply such as "list them" is read against what it refers to.
            ChatMessage? precedingAssistantMessage = await GetPrecedingAssistantMessageAsync(conversationId);

            // The first call of every turn, on the framework tier: it stands between the user
            // and the whole pipeline, so nothing downstream runs until it has answered.
            // The verdict arrives in the shape of its record, which the provider is held to by schema.
            // It is null only when the answer could not be read.
            Records.GuardCheckResponse? llmResult = await llmService.CompleteStructuredAsync<Records.GuardCheckResponse>(
                conversationId,
                guardSystemPrompt,
                message,
                precedingMessages: precedingAssistantMessage is null ? null : [precedingAssistantMessage]);

            // For the log line alone. The answer below is decided on the verdict itself.
            bool compliant = llmResult?.Compliant ?? true;

            // Leaves the verdict on record for every turn so that a refusal can be traced to its message.
            logger.LogInformation(
                "LLMGuardRailService: LLM policy check result — compliant={Compliant} for conversation {ConversationId}",
                compliant, conversationId);

            // A verdict that could not be read admits the message. Refusing a user over a model that
            // answered badly would silence a legitimate turn, so the doubt is spent in their favour —
            // the same choice the general catch below makes.
            return llmResult != null
                ? new Records.GuardRailResult(llmResult.Compliant, llmResult.Violation)
                : new Records.GuardRailResult(Compliant: true, Violation: null);
        }
        catch (Exception ex) when (llmService.GetLanguageModel(llmService.FrameworkTier).IsContentFilterRefusal(ex))
        {
            // The provider's own content filter blocked the prompt before any judgment could run:
            // a genuine violation signal, never fail-open.
            // Records the block, since the provider's refusal is the only evidence of what was rejected.
            logger.LogWarning(ex,
                "LLMGuardRailService: provider-level content filter rejected the prompt for conversation {ConversationId} — treating as a compliance violation",
                conversationId);

            // The user is refused with the framework's closed-door line and the turn goes no further.
            return new Records.GuardRailResult(
                Compliant: false,
                Violation: contentFilteredRefusal);
        }
        catch (Exception ex)
        {
            // Fail open: a transient LLM error must not block legitimate users.
            // The error is logged because the admission it causes would otherwise leave no trace.
            logger.LogError(ex,
                "LLMGuardRailService: LLM policy check failed for conversation {ConversationId} — failing open",
                conversationId);

            // The message proceeds to classification as if it had passed the check.
            return new Records.GuardRailResult(Compliant: true, Violation: null);
        }
    }
}