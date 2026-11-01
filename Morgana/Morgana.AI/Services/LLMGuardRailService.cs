using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IGuardRailService"/> implementation: the LLM policy check on the user's message, the
/// deterministic check on every guarded tool result and the ToolGuard inspection of the results of external tools.
/// The user's message goes to <see cref="ILanguageModelService"/> with the UserGuard system prompt for detection of
/// spam, phishing, violence, profanity and other policy violations.
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
    /// Pre-computed UserGuard system prompt.
    /// </summary>
    private readonly string userGuardSystemPrompt;

    /// <summary>
    /// Pre-computed ToolGuard system prompt, read by the inspector of the results of external tools.
    /// </summary>
    private readonly string toolGuardSystemPrompt;

    /// <summary>
    /// Application configuration, read per call for the limit on an external result's length.
    /// </summary>
    private readonly IConfiguration configuration;

    /// <summary>
    /// The dust role of the inspection: the ledger reads it under its own name, apart from the pipeline's other framework calls.
    /// </summary>
    private static readonly string toolGuardDustRole = $"{Constants.Morgana} ({Constants.Prompts.ToolGuard})";

    /// <summary>
    /// The longest external result an inspection vouches for, in characters; read per call like the adapter reads its switches.
    /// </summary>
    private int MaxExternalToolResultLength
        => configuration.GetValue("Morgana:ActorSystem:MaxExternalToolResultLength", 32000);

    /// <summary>
    /// The refusal for a message that the provider's own content filter blocked, worded in the UserGuard prompt.
    /// </summary>
    private readonly string contentFilteredRefusal;

    /// <summary>
    /// Initialises a new instance of <see cref="LLMGuardRailService"/>.
    /// Loads and builds the UserGuard system prompt eagerly.
    /// </summary>
    /// <param name="llmService">LLM service used for the async policy check.</param>
    /// <param name="promptResolverService">Prompt resolver used to load UserGuard configuration.</param>
    /// <param name="configuration">Application configuration holding the limit on an external result's length.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public LLMGuardRailService(
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration,
        ILogger logger)
    {
        this.llmService = llmService;
        this.configuration = configuration;
        this.logger = logger;

        // Built once here, not per-call: unlike LLMClassifierService/EmbeddedAgentConfigurationService
        // this isn't deferred behind a Lazy<> — the UserGuard prompt is needed on essentially every
        // turn (guard check gates every user message), so eager beats lazy for the common case.
        Records.Prompt userGuardPrompt =
            promptResolverService.ResolveAsync(Constants.Prompts.UserGuard).GetAwaiter().GetResult();

        // A refusal is Morgana speaking to the user, so it is worded in the voice her agents speak in.
        Records.Prompt morganaPrompt = promptResolverService.ResolveAsync(Constants.Morgana).GetAwaiter().GetResult();

        // What the UserGuard reads before every user message. No placeholder to splice, unlike the
        // classifier's: what is admissible is a policy of the framework, not of a domain.
        userGuardSystemPrompt = string.Join("\n",
            Records.Prompt.Labeled(Constants.SectionLabels.Target, userGuardPrompt.Target),
            Records.Prompt.Labeled(Constants.SectionLabels.Personality, morganaPrompt.Personality),
            Records.Prompt.Labeled(Constants.SectionLabels.Instructions, userGuardPrompt.Instructions),
            Records.Prompt.Labeled(Constants.SectionLabels.Formatting, userGuardPrompt.Formatting));

        // The inspector's prompt, resolved eagerly like the UserGuard's: an external result is inspected on the turn that meets it.
        Records.Prompt toolGuardPrompt =
            promptResolverService.ResolveAsync(Constants.Prompts.ToolGuard).GetAwaiter().GetResult();

        // What the inspector reads before every external result; it has no placeholder to splice either.
        toolGuardSystemPrompt = string.Join("\n",
            Records.Prompt.Labeled(Constants.SectionLabels.Target, toolGuardPrompt.Target),
            Records.Prompt.Labeled(Constants.SectionLabels.Instructions, toolGuardPrompt.Instructions),
            Records.Prompt.Labeled(Constants.SectionLabels.Formatting, toolGuardPrompt.Formatting));

        // The one refusal the guard words itself rather than the model: the provider blocked the message before any judgment.
        contentFilteredRefusal = userGuardPrompt.GetAdditionalProperty<string>(Constants.PromptProperties.ContentFilteredMessage);
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
                userGuardSystemPrompt,
                message);

            // The verdict, in the shape the UserGuard prompt's Formatting section asked for. Nothing here
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
    public async Task<Records.GuardRailResult> CheckToolResultAsync(string conversationId, string toolName, string toolArguments, string toolResult, bool externalSource)
    {
        // What the result carries against the assistant; null when nothing in it does.
        string? finding;

        try
        {
            // The free check, which every guarded result pays for.
            finding = FindForgery(toolResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "LLMGuardRailService: check of the result of tool {ToolName} failed for conversation {ConversationId}",
                toolName, conversationId);

            // A native tool's result that cannot be checked is admitted: a check that cannot evaluate must not silence a legitimate tool.
            if (!externalSource)
                return new Records.GuardRailResult(true, null, Records.GuardVerdictSource.FailOpen);

            // An external result goes on to the inspector, which still reads it.
            finding = null;
        }

        // A forgery withholds the result. Only the finding is logged: the content stays out of every log.
        if (finding != null)
        {
            logger.LogWarning(
                "LLMGuardRailService: result of tool {ToolName} withheld for conversation {ConversationId} — {Finding}",
                toolName, conversationId, finding);

            return new Records.GuardRailResult(false, finding, Records.GuardVerdictSource.Prefilter);
        }

        // A native tool runs code its plugin answers for: the free check is all it pays for.
        if (!externalSource)
            return new Records.GuardRailResult(true, null, Records.GuardVerdictSource.Prefilter);

        // The limit on what an inspection vouches for, read once for this result.
        int maxLength = MaxExternalToolResultLength;

        // On a text that long an inspection is not a verdict to trust and an injection hides easily in its tail.
        if (toolResult.Length > maxLength)
        {
            logger.LogWarning(
                "LLMGuardRailService: result of tool {ToolName} withheld for conversation {ConversationId} — {Length} characters exceed the limit of {Limit}",
                toolName, conversationId, toolResult.Length, maxLength);

            return new Records.GuardRailResult(
                false,
                string.Create(CultureInfo.InvariantCulture, $"is longer than the {maxLength} characters an inspection vouches for"),
                Records.GuardVerdictSource.Prefilter);
        }

        // Nothing forged and within the limit: the inspector reads what the free check cannot.
        return await InspectToolResultAsync(conversationId, toolName, toolArguments, toolResult);
    }

    /// <summary>
    /// Has the ToolGuard inspector judge the result of an external tool; a result that cannot be judged is withheld.
    /// </summary>
    private async Task<Records.GuardRailResult> InspectToolResultAsync(string conversationId, string toolName, string toolArguments, string toolResult)
    {
        try
        {
            // The tool, the arguments it was called with and what it returned, in the three fields the inspector's prompt names.
            Records.ToolGuardSubject subject = new Records.ToolGuardSubject(toolName, ReadJsonElement(toolArguments), ReadJsonElement(toolResult));

            // The inspector reads the subject as one JSON document, in the escaping the tool loop hands to the
            // provider: accented letters and angle brackets stay readable instead of turning into escape codes.
            string subjectJson = JsonSerializer.Serialize(subject, AIJsonUtilities.DefaultOptions);

            // The inspection runs on the framework tier and is charged under its own dust role.
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                toolGuardSystemPrompt,
                subjectJson,
                toolGuardDustRole);

            // The verdict, in the shape the ToolGuard prompt's Formatting section asked for.
            Records.GuardCheckResponse? inspectorVerdict = JsonSerializer.Deserialize<Records.GuardCheckResponse>(
                response, Records.DefaultJsonSerializerOptions);

            // An inspector that does not answer in its shape may have been talked to by the very text it was reading.
            if (inspectorVerdict is null)
            {
                logger.LogWarning(
                    "LLMGuardRailService: inspector returned no readable verdict on the result of tool {ToolName} for conversation {ConversationId} — withholding",
                    toolName, conversationId);

                return new Records.GuardRailResult(false, "the inspector returned no readable verdict", Records.GuardVerdictSource.FailClosed);
            }

            logger.LogInformation(
                "LLMGuardRailService: inspection of the result of tool {ToolName} — compliant={Compliant} for conversation {ConversationId}",
                toolName, inspectorVerdict.Compliant, conversationId);

            // The inspector found nothing against the assistant: the result reaches the model.
            if (inspectorVerdict.Compliant)
                return new Records.GuardRailResult(true, null, Records.GuardVerdictSource.Inspector);

            // The inspector judged the result hostile: only its finding is logged and never the content.
            logger.LogWarning(
                "LLMGuardRailService: result of tool {ToolName} withheld for conversation {ConversationId} — {Violation}",
                toolName, conversationId, inspectorVerdict.Violation);

            return new Records.GuardRailResult(
                false,
                inspectorVerdict.Violation ?? "judged hostile by the inspector",
                Records.GuardVerdictSource.Inspector);
        }
        catch (Exception ex) when (llmService.GetLanguageModel(llmService.FrameworkTier).IsContentFilterRefusal(ex))
        {
            // The provider refused the text before any judgment: a signal against the result, never an admission.
            logger.LogWarning(ex,
                "LLMGuardRailService: provider-level content filter rejected the result of tool {ToolName} for conversation {ConversationId} — withholding",
                toolName, conversationId);

            return new Records.GuardRailResult(false, "refused by the provider's content filter", Records.GuardVerdictSource.ProviderFilter);
        }
        catch (Exception ex)
        {
            // This includes a JsonException from an answer that is not JSON, which is how a framework tier
            // that is down answers (Morgana's error text): an external source is withheld rather than read unjudged.
            logger.LogError(ex,
                "LLMGuardRailService: inspection of the result of tool {ToolName} failed for conversation {ConversationId} — withholding",
                toolName, conversationId);

            return new Records.GuardRailResult(false, "no verdict could be reached on a source nobody here vouches for", Records.GuardVerdictSource.FailClosed);
        }
    }

    /// <summary>
    /// Reads a serialized text as the element the inspector sees; a text that is not JSON becomes one string.
    /// </summary>
    private static JsonElement ReadJsonElement(string serializedText)
    {
        try
        {
            // The text as the tool loop serialized it, cloned so it outlives the document.
            using JsonDocument serializedDocument = JsonDocument.Parse(serializedText);
            return serializedDocument.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A text that is not JSON is one string, handed over whole.
            return JsonSerializer.SerializeToElement(serializedText);
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
