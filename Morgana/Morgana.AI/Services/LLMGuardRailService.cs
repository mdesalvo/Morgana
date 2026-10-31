using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IGuardRailService"/> implementation: the LLM policy check on the user's message and the
/// deterministic check on tool results. The user's message goes to <see cref="ILanguageModelService"/>
/// with the Guard system prompt for detection of spam, phishing, violence, profanity and other
/// policy violations.
/// </summary>
public partial class LLMGuardRailService : IGuardRailService
{
    /// <summary>
    /// LLM used for the policy check. Consumed through the stateless completion path — each
    /// message is judged on its own text, on the framework tier.
    /// </summary>
    private readonly ILanguageModelService llmService;

    /// <summary>
    /// The first line of each prompt fence: the part of a fence that identifies it, whatever follows it.
    /// </summary>
    private static readonly string[] fenceFirstLines =
    [
        FirstLine(Constants.PromptFences.GlobalPoliciesHeader),
        FirstLine(Constants.PromptFences.GlobalPoliciesFooter),
        FirstLine(Constants.PromptFences.FrameworkLayerHeader),
        FirstLine(Constants.PromptFences.FrameworkLayerFooter),
        FirstLine(Constants.PromptFences.DomainLayerHeader),
        FirstLine(Constants.PromptFences.DomainLayerFooter)
    ];

    /// <summary>
    /// The four section labels of Morgana's own prompt, which no tool result has any reason to carry.
    /// </summary>
    private static readonly string[] sectionLabels =
    [
        Constants.SectionLabels.Target,
        Constants.SectionLabels.Personality,
        Constants.SectionLabels.Instructions,
        Constants.SectionLabels.Formatting
    ];

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
    /// The refusal for a message that the provider's own content filter blocked, worded in the Guard prompt.
    /// </summary>
    private readonly string contentFilteredRefusal;

    /// <summary>
    /// Initialises a new instance of <see cref="LLMGuardRailService"/>.
    /// Loads and builds the Guard system prompt eagerly.
    /// </summary>
    /// <param name="llmService">LLM service used for the async policy check.</param>
    /// <param name="promptResolverService">Prompt resolver used to load Guard configuration.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public LLMGuardRailService(
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger)
    {
        this.llmService = llmService;
        this.logger = logger;

        // Built once here, not per-call: unlike LLMClassifierService/EmbeddedAgentConfigurationService
        // this isn't deferred behind a Lazy<> — the Guard prompt is needed on essentially every
        // turn (guard check gates every user message), so eager beats lazy for the common case.
        Records.Prompt guardPrompt =
            promptResolverService.ResolveAsync(Constants.Prompts.Guard).GetAwaiter().GetResult();

        // What the Guard reads before every user message. No placeholder to splice, unlike the
        // classifier's: what is admissible is a policy of the framework, not of a domain.
        guardSystemPrompt = string.Join("\n",
            Records.Prompt.Labeled(Constants.SectionLabels.Target, guardPrompt.Target),
            Records.Prompt.Labeled(Constants.SectionLabels.Instructions, guardPrompt.Instructions),
            Records.Prompt.Labeled(Constants.SectionLabels.Formatting, guardPrompt.Formatting));

        // The one refusal the guard words itself rather than the model: the provider blocked the message before any judgment.
        contentFilteredRefusal = guardPrompt.GetAdditionalProperty<string>(Constants.PromptProperties.ContentFilteredMessage);
    }

    /// <inheritdoc/>
    public async Task<Records.GuardRailResult> CheckUserMessageAsync(string conversationId, string message)
    {
        try
        {
            // The first call of every turn, on the framework tier: it stands between the user
            // and the whole pipeline, so nothing downstream runs until it has answered.
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                guardSystemPrompt,
                message);

            // The verdict, in the shape the Guard prompt's Formatting section asked for. Nothing here
            // is null when the model answered as instructed.
            Records.GuardCheckResponse? llmResult = JsonSerializer.Deserialize<Records.GuardCheckResponse>(
                response, Records.DefaultJsonSerializerOptions);

            // For the log line alone. The answer below is decided on the verdict itself.
            bool compliant = llmResult?.Compliant ?? true;

            logger.LogInformation(
                "LLMGuardRailService: LLM policy check result — compliant={Compliant} for conversation {ConversationId}",
                compliant, conversationId);

            // A verdict that could not be read admits the message. Refusing a user over a model that
            // answered badly would silence a legitimate turn, so the doubt is spent in their favour —
            // the same choice the general catch below makes.
            return llmResult != null
                ? new Records.GuardRailResult(llmResult.Compliant, llmResult.Violation, Records.GuardVerdictSource.Inspector)
                : new Records.GuardRailResult(Compliant: true, Violation: null, Source: Records.GuardVerdictSource.FailOpen);
        }
        catch (Exception ex) when (llmService.GetLanguageModel(llmService.FrameworkTier).IsContentFilterRefusal(ex))
        {
            // The provider's own content filter blocked the prompt before any judgment could run —
            // a genuine violation signal, never fail-open.
            logger.LogWarning(ex,
                "LLMGuardRailService: provider-level content filter rejected the prompt for conversation {ConversationId} — treating as a compliance violation",
                conversationId);

            return new Records.GuardRailResult(
                Compliant: false,
                Violation: contentFilteredRefusal,
                Source: Records.GuardVerdictSource.ProviderFilter);
        }
        catch (Exception ex)
        {
            // Fail open: a transient LLM error must not block legitimate users.
            logger.LogError(ex,
                "LLMGuardRailService: LLM policy check failed for conversation {ConversationId} — failing open",
                conversationId);

            return new Records.GuardRailResult(Compliant: true, Violation: null, Source: Records.GuardVerdictSource.FailOpen);
        }
    }

    /// <inheritdoc/>
    public Task<Records.GuardRailResult> CheckToolResultAsync(string conversationId, string toolName, string toolResult)
    {
        try
        {
            // What the result carries against the assistant; null when nothing in it does.
            string? finding = FindForgery(toolResult);

            // A forgery withholds the result. Only the finding is logged: the content stays out of every log.
            if (finding != null)
            {
                logger.LogWarning(
                    "LLMGuardRailService: result of tool {ToolName} withheld for conversation {ConversationId} — {Finding}",
                    toolName, conversationId, finding);

                return Task.FromResult(new Records.GuardRailResult(false, finding, Records.GuardVerdictSource.Prefilter));
            }

            // Nothing forged: the model reads the result as the tool returned it.
            return Task.FromResult(new Records.GuardRailResult(true, null, Records.GuardVerdictSource.Prefilter));
        }
        catch (Exception ex)
        {
            // Fail open: a check that cannot evaluate must not silence a legitimate tool.
            logger.LogError(ex,
                "LLMGuardRailService: check of the result of tool {ToolName} failed for conversation {ConversationId} — failing open",
                toolName, conversationId);

            return Task.FromResult(new Records.GuardRailResult(true, null, Records.GuardVerdictSource.FailOpen));
        }
    }

    /// <summary>
    /// Describes the first forgery found in a tool result; null when the result carries none.
    /// </summary>
    private static string? FindForgery(string toolResult)
    {
        // Every text a reader of the result would meet, nested JSON included.
        foreach (string text in ReadableTexts(toolResult))
        {
            // A section label anywhere in the text: the labels are Morgana's own and no datum needs them.
            foreach (string label in sectionLabels)
            {
                if (text.Contains(label, StringComparison.Ordinal))
                    return $"forges the prompt section label {label}";
            }

            // The first line of a fence anywhere in the text: enough to pose as the end of a layer.
            foreach (string fenceFirstLine in fenceFirstLines)
            {
                if (text.Contains(fenceFirstLine, StringComparison.Ordinal))
                    return "forges a fence of Morgana's prompt";
            }

            // A token that a chat template uses to open or close a turn.
            if (ChatTemplateTokenPattern().IsMatch(text))
                return "carries a chat-template token";

            // Invisible characters that spell a message a reader cannot see.
            if (HidesTextInTagCharacters(text))
                return "carries text hidden in Unicode tag characters";
        }

        // No text of the result has any of them.
        return null;
    }

    /// <summary>
    /// Collects every text of a result: property names and string values at any depth of the JSON.
    /// </summary>
    private static List<string> ReadableTexts(string toolResult)
    {
        // Filled in reading order, so the first forgery found is the first a reader would meet.
        List<string> texts = [];

        try
        {
            // The result as the tool loop serialized it.
            using JsonDocument document = JsonDocument.Parse(toolResult);
            CollectTexts(document.RootElement, texts);
        }
        catch (JsonException)
        {
            // A result that is not JSON is one text, read whole.
            return [toolResult];
        }

        // Every name and value of the document, nested documents included.
        return texts;
    }

    /// <summary>
    /// Adds the property names and string values under an element to the list; nested JSON carried in a string is descended too.
    /// </summary>
    private static void CollectTexts(JsonElement element, List<string> texts)
    {
        switch (element.ValueKind)
        {
            // An object contributes each property name and everything under each value.
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    texts.Add(property.Name);
                    CollectTexts(property.Value, texts);
                }
                break;

            // An array contributes everything under each item.
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    CollectTexts(item, texts);
                break;

            // A string is a text itself and may carry a serialized document, as a colleague's envelope does.
            case JsonValueKind.String:
                string text = element.GetString() ?? "";
                texts.Add(text);

                // Only a string that opens like a document is worth parsing.
                string trimmedText = text.TrimStart();
                if (trimmedText.StartsWith('{') || trimmedText.StartsWith('['))
                {
                    try
                    {
                        // Its texts join the result's own: a model reading the envelope reads them too.
                        using JsonDocument nestedDocument = JsonDocument.Parse(text);
                        CollectTexts(nestedDocument.RootElement, texts);
                    }
                    catch (JsonException)
                    {
                        // Not a document after all: the string is already collected.
                    }
                }
                break;
        }
    }

    /// <summary>
    /// True when the text carries a Unicode tag character outside a flag emoji's own sequence.
    /// </summary>
    private static bool HidesTextInTagCharacters(string text)
    {
        // The tag block runs from U+E0000 to U+E007F; a flag emoji is the one honest use of it.
        const int tagBlockStart = 0xE0000;
        const int tagBlockEnd = 0xE007F;
        const int blackFlag = 0x1F3F4;
        const int tagSpace = 0xE0020;
        const int tagTilde = 0xE007E;
        const int cancelTag = 0xE007F;

        // The text as the model reads it, one character per code point: a tag character lies outside the
        // basic plane and would otherwise be split across two halves.
        List<int> codePoints = [];
        foreach (Rune rune in text.EnumerateRunes())
            codePoints.Add(rune.Value);

        // Every character in reading order, a closed flag sequence being stepped over whole.
        for (int position = 0; position < codePoints.Count; position++)
        {
            // The character at this point of the text.
            int codePoint = codePoints[position];

            // The black flag opens the one sequence of tag characters that may be innocent.
            if (codePoint == blackFlag)
            {
                // The sequence is the flag followed by tag letters and closed by the cancel tag.
                int cursor = position + 1;
                while (cursor < codePoints.Count && codePoints[cursor] >= tagSpace && codePoints[cursor] <= tagTilde)
                    cursor++;

                // A closed sequence with at least one letter is skipped whole.
                if (cursor > position + 1 && cursor < codePoints.Count && codePoints[cursor] == cancelTag)
                {
                    position = cursor;
                    continue;
                }
            }
            else if (codePoint >= tagBlockStart && codePoint <= tagBlockEnd)
            {
                // A lone tag character: nothing honest puts one here.
                return true;
            }

            // Any tag character after an unclosed flag is met on the next iteration and refused there.
        }

        // No tag character outside a flag: nothing in the text is hidden from its reader.
        return false;
    }

    /// <summary>
    /// The text before the first line break of a fence; the whole fence when it has one line.
    /// </summary>
    private static string FirstLine(string fence)
    {
        // The break that ends the first line, if the fence has more than one.
        int lineBreak = fence.IndexOf('\n', StringComparison.Ordinal);
        return lineBreak < 0 ? fence : fence[..lineBreak];
    }

    /// <summary>
    /// The tokens chat templates use to open or close a turn or a tool exchange, which no business datum carries.
    /// </summary>
    [GeneratedRegex(@"<\|[a-z_]+\|>|\[/?INST\]|<</?SYS>>|</?(tool_call|tool_result|tool_response|function_calls|function_results)>")]
    private static partial Regex ChatTemplateTokenPattern();
}
