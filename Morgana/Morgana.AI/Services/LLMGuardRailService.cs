using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IGuardRailService"/> implementation. Delegates to <see cref="ILLMService"/>
/// with the Guard system prompt for detection of spam, phishing, violence, profanity and other
/// policy violations. Screens tool results for text forging Morgana's own prompt.
/// </summary>
public partial class LLMGuardRailService : IGuardRailService
{
    /// <summary>
    /// LLM used for the policy check. Consumed through the stateless completion path — each
    /// message is judged on its own text, on the cheapest configured tier.
    /// </summary>
    private readonly ILLMService llmService;

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
    /// Pre-computed ToolGuard system prompt, read before the result of every external tool.
    /// </summary>
    private readonly string toolGuardSystemPrompt;

    /// <summary>
    /// The labels heading the sections of Morgana's own prompt, <c>[TARGET]</c> among them. A tool
    /// result carrying one is forging a layer the model was told to obey.
    /// </summary>
    private readonly string[] promptLayerLabels;

    /// <summary>
    /// Initialises a new instance of <see cref="LLMGuardRailService"/>.
    /// Loads and builds the Guard system prompt eagerly.
    /// </summary>
    /// <param name="llmService">LLM service used for the async policy check.</param>
    /// <param name="promptResolverService">Prompt resolver used to load Guard configuration.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public LLMGuardRailService(
        ILLMService llmService,
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
        guardSystemPrompt = $"{guardPrompt.Target}\n{guardPrompt.Instructions}\n{guardPrompt.Formatting}";

        Records.Prompt toolGuardPrompt =
            promptResolverService.ResolveAsync(Constants.Prompts.ToolGuard).GetAwaiter().GetResult();
        toolGuardSystemPrompt = $"{toolGuardPrompt.Target}\n{toolGuardPrompt.Instructions}\n{toolGuardPrompt.Formatting}";

        // The labels are read from the framework prompt that carries them rather than listed here, so
        // a template added to morgana.json is recognised as forged the day it is written.
        Records.Prompt morganaPrompt =
            promptResolverService.ResolveAsync(Constants.Morgana).GetAwaiter().GetResult();
        promptLayerLabels = ReadPromptLayerLabels(morganaPrompt);
    }

    /// <summary>
    /// Collects the label heading each section of the framework prompt and each of its injections.
    /// </summary>
    private static string[] ReadPromptLayerLabels(Records.Prompt morganaPrompt)
    {
        // The four sections every agent reads first, each opening with its label, e.g. "[TARGET] You are…".
        List<string> sections =
        [
            morganaPrompt.Target,
            morganaPrompt.Instructions,
            morganaPrompt.Formatting,
            morganaPrompt.Personality ?? ""
        ];

        // The templates spliced into tool descriptions, per-turn context and colleagues' questions open
        // with a label of their own, e.g. "[CONTEXT ALREADY HELD]".
        sections.AddRange(morganaPrompt
            .GetAdditionalProperty<List<Records.Injection>>(Constants.PromptProperties.Injections)
            .Select(injection => injection.Description));

        HashSet<string> labels = [];
        foreach (string section in sections)
        {
            // A section opening without a label contributes nothing to recognise.
            Match label = PromptLayerLabelPattern().Match(section);
            if (label.Success)
                labels.Add(label.Value);
        }

        return [.. labels];
    }

    /// <inheritdoc/>
    public async Task<Records.GuardRailResult> CheckUserMessageAsync(string conversationId, string message)
    {
        try
        {
            // The first call of every turn, on the cheapest configured tier: it stands between the user
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
                ? new Records.GuardRailResult(llmResult.Compliant, llmResult.Violation)
                : new Records.GuardRailResult(Compliant: true, Violation: null);
        }
        catch (Exception ex) when (IsProviderContentFilter(ex))
        {
            // The provider's own content filter (e.g. Azure Prompt Shields) blocked the prompt before
            // any judgment could run — a genuine violation signal, never fail-open.
            logger.LogWarning(ex,
                "LLMGuardRailService: provider-level content filter rejected the prompt for conversation {ConversationId} — treating as a compliance violation",
                conversationId);

            return new Records.GuardRailResult(
                Compliant: false,
                Violation: "That is a path closed to you and no phrasing will reopen it.");
        }
        catch (Exception ex)
        {
            // Fail open: a transient LLM error must not block legitimate users.
            logger.LogError(ex,
                "LLMGuardRailService: LLM policy check failed for conversation {ConversationId} — failing open",
                conversationId);

            return new Records.GuardRailResult(Compliant: true, Violation: null);
        }
    }
    /// <inheritdoc/>
    public async Task<Records.GuardRailResult> CheckToolResultAsync(string conversationId, string toolName, string toolResult, bool externalSource)
    {
        string? forgery = FindPromptForgery(toolResult);

        // The reason goes to the log and nowhere else: the result is quarantined whole, so the model never
        // learns which trick was tried. The content itself is never logged.
        if (forgery is not null)
        {
            logger.LogWarning(
                "LLMGuardRailService: result of tool {ToolName} quarantined for conversation {ConversationId} — {Forgery}",
                toolName, conversationId, forgery);

            return new Records.GuardRailResult(Compliant: false, Violation: forgery);
        }

        // A plugin's own tool runs trusted code: the free check above is all it pays for. Only a result
        // nobody here vouches for is worth a model's reading.
        if (!externalSource)
            return new Records.GuardRailResult(Compliant: true, Violation: null);

        try
        {
            // The tool's name comes ahead of its result because the inspector judges whether the result is
            // what such a tool exists to return. The conversation stays out: a stateless judgment on the
            // cheapest tier, which no earlier turn can argue with.
            string response = await llmService.CompleteWithSystemPromptAsync(
                conversationId,
                toolGuardSystemPrompt,
                $"Tool: {toolName}\n\nResult:\n{toolResult}",

                // One call per external result, not one per turn: its own line in the ledger shows what
                // screening the tools costs next to what the user's guard does.
                $"{Constants.Morgana} ({Constants.Prompts.ToolGuard})");

            Records.GuardCheckResponse? llmResult = JsonSerializer.Deserialize<Records.GuardCheckResponse>(
                response, Records.DefaultJsonSerializerOptions);

            // An unreadable verdict admits the result, as for the user's message: the tool loop must not
            // lose a datum to a model that answered badly.
            if (llmResult is null || llmResult.Compliant)
                return new Records.GuardRailResult(Compliant: true, Violation: null);

            logger.LogWarning(
                "LLMGuardRailService: result of tool {ToolName} quarantined for conversation {ConversationId} — {Violation}",
                toolName, conversationId, llmResult.Violation);

            return new Records.GuardRailResult(Compliant: false, Violation: llmResult.Violation);
        }
        catch (Exception ex) when (IsProviderContentFilter(ex))
        {
            // The provider's own shield caught the result before the inspector could read it: a verdict
            // of its own, never a failure to fail open on.
            logger.LogWarning(ex,
                "LLMGuardRailService: provider-level content filter rejected the result of tool {ToolName} for conversation {ConversationId}",
                toolName, conversationId);

            return new Records.GuardRailResult(Compliant: false, Violation: "rejected by the provider's content filter");
        }
        catch (Exception ex)
        {
            // Fail open: a transient LLM error must not starve the agent of its tool's answer.
            logger.LogError(ex,
                "LLMGuardRailService: inspection of tool {ToolName} failed for conversation {ConversationId} — failing open",
                toolName, conversationId);

            return new Records.GuardRailResult(Compliant: true, Violation: null);
        }
    }

    /// <summary>
    /// True when the provider's own content filter (e.g. Azure Prompt Shields) refused the text before
    /// the model could judge it.
    /// </summary>
    private static bool IsProviderContentFilter(Exception exception)
        => exception is System.ClientModel.ClientResultException { Status: 400 } providerRejection
           && providerRejection.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Names the first way the result pretends to be part of the prompt instead of data, or null when it
    /// does not. Deterministic and free, so it runs on every guarded result whatever its source.
    /// </summary>
    private string? FindPromptForgery(string toolResult)
    {
        foreach (string text in ReadableTexts(toolResult))
        {
            // A label of Morgana's own layers, the most direct forgery of all: the model reads its
            // instructions in exactly this shape.
            string? forgedLabel = promptLayerLabels.FirstOrDefault(label => text.Contains(label, StringComparison.Ordinal));
            if (forgedLabel is not null)
                return $"forges the prompt layer label {forgedLabel}";

            // The composer's fences, a line of equal signs around capitals, open and close a layer.
            if (PromptFencePattern().IsMatch(text))
                return "forges a prompt layer fence";

            // A line speaking as a role of the conversation. Title case is left out on purpose: a data
            // row reading "System: Linux" is common where an injection shouting SYSTEM: is not.
            if (ChatRoleLinePattern().IsMatch(text) || ChatTemplateTokenPattern().IsMatch(text))
                return "speaks as a role of the conversation";

            // Characters nobody sees on screen can still carry text or reorder it for the model.
            // A leading byte-order mark is an artefact of a file read, not a message, so it is spared.
            foreach (Rune rune in text.TrimStart('\uFEFF').EnumerateRunes())
            {
                if (IsInvisibleSteering(rune))
                    return string.Create(CultureInfo.InvariantCulture, $"carries the invisible character U+{rune.Value:X4}");
            }
        }

        return null;
    }

    /// <summary>
    /// The texts the model will actually read in a result: every string of a JSON document decoded,
    /// property names included, or the result itself when it is not JSON.
    /// </summary>
    /// <remarks>
    /// Serialized JSON hides line breaks and invisible characters behind escapes the model decodes as it
    /// reads, so a pattern over the raw text would miss exactly what it is looking for.
    /// </remarks>
    private static List<string> ReadableTexts(string toolResult)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(toolResult);

            List<string> texts = [];
            CollectStrings(document.RootElement, texts);

            return texts;
        }
        catch (JsonException)
        {
            return [toolResult];
        }

        static void CollectStrings(JsonElement element, List<string> texts)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    string text = element.GetString() ?? "";
                    texts.Add(text);

                    // An MCP server hands its JSON back as the text of a content block, so a string may
                    // carry a document of its own whose escapes are still closed: it is read one level down.
                    if (text.TrimStart() is ['{' or '[', ..])
                    {
                        try
                        {
                            using JsonDocument nestedDocument = JsonDocument.Parse(text);
                            CollectStrings(nestedDocument.RootElement, texts);
                        }
                        catch (JsonException)
                        {
                            // Text that merely opens with a bracket is already collected as it stands.
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                        CollectStrings(item, texts);
                    break;

                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        texts.Add(property.Name);
                        CollectStrings(property.Value, texts);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Characters that render as nothing yet reach the model: zero-width marks, the bidirectional
    /// overrides that reorder text and the tag block that spells hidden ASCII.
    /// </summary>
    /// <remarks>
    /// The zero-width joiner and the left-to-right and right-to-left marks stay out: emoji sequences and
    /// right-to-left languages use them in ordinary data.
    /// </remarks>
    private static bool IsInvisibleSteering(Rune rune)
        => rune.Value is (>= 0x200B and <= 0x200C)
            or (>= 0x202A and <= 0x202E)
            or (>= 0x2060 and <= 0x2064)
            or (>= 0x2066 and <= 0x2069)
            or 0xFEFF
            or (>= 0xE0000 and <= 0xE007F);

    /// <summary>A bracketed all-caps label at the head of a prompt section, the idiom of morgana.json.</summary>
    [GeneratedRegex(@"^\[[A-Z][A-Z ]*\]")]
    private static partial Regex PromptLayerLabelPattern();

    /// <summary>A line made of a capitalised title between runs of equal signs, the shape of the composer's fences.</summary>
    [GeneratedRegex(@"^\s*={3,}[^=\n]*\p{Lu}[^=\n]*={3,}\s*$", RegexOptions.Multiline)]
    private static partial Regex PromptFencePattern();

    /// <summary>A line opening with a conversation role and a colon, in lower case or shouted.</summary>
    [GeneratedRegex(@"^\s*(system|assistant|developer|SYSTEM|ASSISTANT|DEVELOPER)\s*:", RegexOptions.Multiline)]
    private static partial Regex ChatRoleLinePattern();

    /// <summary>The tokens chat templates use to open or close a turn or a tool result.</summary>
    [GeneratedRegex(@"<\|[a-z_]+\|>|</?(system|tool_result|function_results|tool_response)>|<<SYS>>|\[/?INST\]", RegexOptions.IgnoreCase)]
    private static partial Regex ChatTemplateTokenPattern();
}
