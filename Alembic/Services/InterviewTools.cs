using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Model;
using Morgana.Contracts;

namespace Alembic.Services;

/// <summary>
/// The tools Alembic calls while conducting a pass.
/// </summary>
/// <remarks>
/// <para>
/// Every method here returns a sentence <b>to the model</b>, not to the client. That return channel
/// is the point of having tools at all rather than a structured reply: a section that comes back
/// the wrong shape is reported to Alembic in the same turn, and it corrects itself before the
/// client ever sees anything. A single malformed structured reply, by contrast, costs the client a
/// turn of their own interview.
/// </para>
/// <para>
/// The write tools also carry the <b>section labels</b>. Both composed layers use the same four,
/// which is exactly why the framework fences them, so a domain layer arriving unlabelled leaves
/// half the prompt without the markers the other half has. A label says which section this is, not
/// what it means: it is structure, and structure is not left to a model remembering a rule.
/// </para>
/// <para>
/// Which of these exist in a given pass is decided by <c>alembic.json</c>, not by prose. The
/// functional pass has no tool for an agent's instructions or formatting, so it cannot write them
/// — the constraint is the absence of a tool rather than a sentence asking for restraint.
/// </para>
/// </remarks>
public class InterviewTools
{
    /// <summary>Section label carried by an agent's Target.</summary>
    private const string TargetMarker = "[TARGET]";

    /// <summary>Section label carried by an agent's Personality.</summary>
    private const string PersonalityMarker = "[PERSONALITY]";

    /// <summary>Section label carried by an agent's Instructions.</summary>
    private const string InstructionsMarker = "[INSTRUCTIONS]";

    /// <summary>Section label carried by an agent's Formatting.</summary>
    private const string FormattingMarker = "[FORMATTING]";

    /// <summary>
    /// The intent name the framework reserves for the classifier's fallback. No authored agent may
    /// take it.
    /// </summary>
    private const string ReservedFallbackIntent = "other";

    /// <summary>
    /// The scope of a parameter Morgana resolves from the session's own context variables.
    /// </summary>
    private const string ContextScope = "context";

    /// <summary>
    /// The scope of a parameter the agent obtains from the user in conversation.
    /// </summary>
    private const string RequestScope = "request";

    private readonly InterviewState state;
    private readonly IDraftStateService draftStateService;
    private readonly IDraftValidationService draftValidationService;
    private readonly IRecapService recapService;

    /// <summary>
    /// Binds the toolset to one interview.
    /// </summary>
    /// <param name="state">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The deterministic checks.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    public InterviewTools(
        InterviewState state,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
    {
        this.state = state;
        this.draftStateService = draftStateService;
        this.draftValidationService = draftValidationService;
        this.recapService = recapService;
    }

    /// <summary>
    /// Records the intent that routes to this agent.
    /// </summary>
    public string SetIntent(string name, string description, string label, string defaultValue)
    {
        string cleanName = (name ?? string.Empty).Trim();

        // Refused rather than reported. 'other' is the classifier's own fallback — the intent it
        // falls back to when it cannot place a message at all — and it is the single name
        // HandlesIntentAgentRegistryService exempts from needing an agent. An authored agent taking
        // it would shadow the fallback for the whole domain, so this is the one name that must not
        // reach the Draft even provisionally.
        if (string.Equals(cleanName, ReservedFallbackIntent, StringComparison.OrdinalIgnoreCase))
            return $"Nothing recorded: '{ReservedFallbackIntent}' is reserved. It is the intent the classifier "
                   + "falls back to when it cannot place a message, and no agent may claim it. Call this again with a name from the domain.";

        state.Intent.Name = cleanName;
        state.Intent.Description = description?.Trim();
        state.Intent.Label = label?.Trim();
        state.Intent.DefaultValue = defaultValue?.Trim();
        state.Agent.ID = cleanName;

        // Reported rather than corrected: the name is the client's domain vocabulary, and silently
        // rewriting it would leave Alembic telling them one thing while the configuration says
        // another. A shape complaint it can act on is worth more than a fix it never learns about.
        bool bareLowercaseWord = cleanName.Length > 0
                                 && cleanName.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

        return bareLowercaseWord
            ? $"Intent recorded as '{cleanName}'."
            : $"Intent recorded as '{cleanName}', but that is not a bare lowercase word. "
              + "It becomes a C# attribute argument and a prompt ID, so call this tool again with one that is.";
    }

    /// <summary>
    /// Records the agent's Target section.
    /// </summary>
    public string SetAgentTarget(string target)
    {
        state.Agent.Target = Marked(TargetMarker, target);
        return Shaped("Target", target, 2, 4);
    }

    /// <summary>
    /// Records the agent's Personality section.
    /// </summary>
    public string SetAgentPersonality(string personality)
    {
        state.Agent.Personality = Marked(PersonalityMarker, personality);
        return Shaped("Personality", personality, 2, 3);
    }

    /// <summary>
    /// Records the agent's Instructions section.
    /// </summary>
    public string SetAgentInstructions(string instructions)
    {
        state.Agent.Instructions = Marked(InstructionsMarker, instructions);
        return Shaped("Instructions", instructions, 4, 12);
    }

    /// <summary>
    /// Records the agent's Formatting section.
    /// </summary>
    public string SetAgentFormatting(string formatting)
    {
        state.Agent.Formatting = Marked(FormattingMarker, formatting);
        return Shaped("Formatting", formatting, 2, 5);
    }

    /// <summary>
    /// Opens a tool, or revises the description of one already open.
    /// </summary>
    /// <remarks>
    /// Revising keeps the parameters. A tool's contract is settled in several turns — the name and
    /// what it does come out of one answer, its inputs out of the next — and re-declaring it to
    /// sharpen the description must not silently empty it.
    /// </remarks>
    public string DeclareTool(string name, string description)
    {
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "No tool recorded: a tool must have a name, because the name is what pairs it with its C# method.";

        ToolDraft? existing = Find(cleanName);
        bool revision = existing is not null;

        ToolDraft tool = existing ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };
        tool.Description = description?.Trim();

        if (!revision)
            state.Agent.Tools.Add(tool);

        // Reported, never rewritten: the name is domain vocabulary, and a silent correction leaves
        // Alembic telling the client one word while the configuration carries another.
        string complaint = IdentifierComplaint(cleanName, "tool name", pascalCase: true);

        return (revision ? $"'{cleanName}' revised." : $"'{cleanName}' declared.")
               + (complaint.Length > 0 ? " " + complaint : string.Empty)
               + (string.IsNullOrWhiteSpace(description)
                   ? " It has no description, and the description is what the model reads when it decides whether to call this tool at all."
                   : string.Empty);
    }

    /// <summary>
    /// Adds a parameter to a tool, or revises one already there.
    /// </summary>
    /// <remarks>
    /// Revision is by name and in place, so the declaration order survives — which matters, because
    /// that order becomes the C# method's parameter order, and C# cannot declare a required
    /// parameter after an optional one.
    /// </remarks>
    public string SetToolParameter(string toolName, string name, string description, string scope, bool required, bool shared)
    {
        if (Find(toolName) is not { } tool)
            return $"No parameter recorded: no tool named '{toolName}' has been declared yet.";

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "No parameter recorded: a parameter must have a name, because the adapter pairs it with the C# method's parameter by name and not by position.";

        // "none" is spelled out because a model asked for an empty string tends to send the word.
        string cleanScope = (scope ?? string.Empty).Trim().ToLowerInvariant();
        string? resolvedScope = cleanScope switch
        {
            ContextScope => ContextScope,
            RequestScope => RequestScope,
            "" or "none" or "null" => null,
            _ => cleanScope
        };

        ToolParameterDraft? existing = tool.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, cleanName, StringComparison.Ordinal));

        bool revision = existing is not null;
        ToolParameterDraft parameter = existing ?? new ToolParameterDraft { Name = cleanName };

        parameter.Description = description?.Trim();
        parameter.Scope = resolvedScope;
        parameter.Required = required;
        parameter.Shared = shared;

        if (!revision)
            tool.Parameters.Add(parameter);

        List<string> complaints = [];

        string identifier = IdentifierComplaint(cleanName, "parameter name", pascalCase: false);
        if (identifier.Length > 0)
            complaints.Add(identifier);

        if (resolvedScope is not null and not ContextScope and not RequestScope)
            complaints.Add($"'{cleanScope}' is not a scope: a parameter resolving an input declares '{ContextScope}' or '{RequestScope}', and one carrying a value you author yourself declares none.");

        if (shared && resolvedScope != ContextScope)
            complaints.Add($"Shared only means something alongside scope '{ContextScope}': it publishes a resolved context variable so other agents can hydrate from it.");

        // The order is the signature, so an optional parameter followed by a required one is not a
        // preference: MorganaToolAdapter.AddTool refuses the pair, and C# could not declare it.
        int firstOptional = tool.Parameters.FindIndex(p => !p.Required);
        if (firstOptional >= 0 && tool.Parameters.Skip(firstOptional).Any(p => p.Required))
            complaints.Add("A required parameter now sits after an optional one, which C# cannot declare. Reorder them by dropping and re-adding, or make the earlier one required.");

        return $"'{cleanName}' recorded on {tool.Name}."
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty);
    }

    /// <summary>
    /// Removes a parameter from a tool.
    /// </summary>
    public string DropToolParameter(string toolName, string parameterName)
    {
        if (Find(toolName) is not { } tool)
            return $"Nothing dropped: no tool named '{toolName}' has been declared.";

        int removed = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? $"'{parameterName}' dropped from {tool.Name}."
            : $"Nothing dropped: {tool.Name} has no parameter named '{parameterName}'.";
    }

    /// <summary>
    /// Removes a tool and everything on it.
    /// </summary>
    public string DropTool(string toolName)
    {
        int removed = state.Agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? $"'{toolName}' dropped, with its parameters."
            : $"Nothing dropped: no tool named '{toolName}' has been declared.";
    }

    /// <summary>
    /// Returns the toolkit as it currently stands.
    /// </summary>
    public string GetToolkit()
    {
        if (state.Agent.Tools.Count == 0)
            return "This agent declares no tools yet. That is a legal end state — an agent whose tools "
                   + "all arrive from an MCP server declares none here — but it must be a conclusion you reached by asking.";

        IEnumerable<string> rendered = state.Agent.Tools.Select(t =>
            $"- {t.Name}: {t.Description ?? "(no description)"}"
            + (t.Parameters.Count == 0
                ? "\n    (takes nothing)"
                : string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? "authored by you"}"
                    + (p.Required ? "" : ", optional")
                    + (p.Shared ? ", shared" : "")
                    + $"]: {p.Description ?? "(no description)"}"))));

        return "The toolkit as it stands:\n" + string.Join("\n", rendered);
    }

    /// <summary>
    /// Returns what earlier passes settled about this agent.
    /// </summary>
    /// <remarks>
    /// Each pass is a fresh agent with a fresh session, so nothing of the previous conversation
    /// carries over — deliberately, because a toolkit pass that still has the whole functional
    /// interview in its context spends it re-litigating decisions already taken. What must carry
    /// over is the configuration, and the configuration is exactly what this returns.
    /// </remarks>
    public string GetAgentSoFar()
    {
        if (string.IsNullOrWhiteSpace(state.Intent.Name))
            return "Nothing settled yet: this agent has no intent.";

        List<string> sections =
        [
            $"Intent '{state.Intent.Name}': {state.Intent.Description}",
            $"Opening sentence a user would send: {state.Intent.DefaultValue}",
            state.Agent.Target ?? "(no target)",
            state.Agent.Personality ?? "(no personality)"
        ];

        if (!string.IsNullOrWhiteSpace(state.Agent.Instructions))
            sections.Add(state.Agent.Instructions);

        if (!string.IsNullOrWhiteSpace(state.Agent.Formatting))
            sections.Add(state.Agent.Formatting);

        return "Settled in the earlier passes, and not yours to reopen:\n\n"
               + string.Join("\n\n", sections);
    }

    /// <summary>
    /// Attaches buttons to the question about to be asked.
    /// </summary>
    public string SetChoices(string choices)
    {
        try
        {
            List<QuickReply>? parsed = JsonSerializer.Deserialize<List<QuickReply>>(
                choices, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed is not { Count: > 0 })
                return "No choices recorded: the payload held no buttons.";

            state.PendingChoices.Clear();
            state.PendingChoices.AddRange(parsed);

            return $"{parsed.Count} choices will be drawn under your question. "
                   + "The text box stays open, so the answer may still come in its own words.";
        }
        catch (JsonException ex)
        {
            return $"No choices recorded: the payload is not a JSON array of buttons ({ex.Message}).";
        }
    }

    /// <summary>
    /// Returns the intents already in the domain.
    /// </summary>
    public string GetExistingIntents()
    {
        List<IntentDraft> existing =
            [.. (draftStateService.Current?.Intents ?? [])
                .Where(i => !string.Equals(i.Name, state.Intent.Name, StringComparison.OrdinalIgnoreCase))];

        return existing.Count == 0
            ? "The domain holds no other intents yet: this is the first, and nothing can collide with it."
            : "Intents already in this domain, with the descriptions the classifier weighs yours against:\n"
              + string.Join("\n", existing.Select(i => $"- {i.Name}: {i.Description}"));
    }

    /// <summary>
    /// Returns the prompt this agent's model will really read.
    /// </summary>
    public async Task<string> GetComposedPrompt()
    {
        if (string.IsNullOrWhiteSpace(state.Agent.Target))
            return "Nothing to compose yet: the agent has no target.";

        AgentRecap recap = await recapService.ComposeAsync(state.Agent);

        return "This is the whole of what this agent's model will read:\n\n" + recap.SystemPrompt;
    }

    /// <summary>
    /// Returns everything wrong with this agent that is decidable without a model.
    /// </summary>
    /// <remarks>
    /// Checked against a probe domain — what the client already has, plus the agent under
    /// construction — because half of these rules are relational: an intent nothing routes to and a
    /// name colliding with a framework prompt are both invisible when an agent is examined alone.
    /// Findings about the client's other agents are filtered out: they are real, but they are not
    /// this pass's business and Alembic cannot fix them from here.
    /// </remarks>
    public string GetFindings()
    {
        if (string.IsNullOrWhiteSpace(state.Intent.Name))
            return "Nothing to check yet: the intent has no name.";

        DomainDraft existing = draftStateService.Current ?? new DomainDraft();

        DomainDraft probe = new DomainDraft
        {
            Intents = [.. existing.Intents, state.Intent],
            Agents = [.. existing.Agents, state.Agent]
        };

        string mine = state.Intent.Name!;

        List<ValidationFinding> findings =
            [.. draftValidationService.Validate(probe)
                .Where(f => f.Where.Contains($"'{mine}'", StringComparison.OrdinalIgnoreCase)
                            || f.Where.StartsWith($"{mine}.", StringComparison.OrdinalIgnoreCase)
                            || f.Where == "domain")];

        return findings.Count == 0
            ? "Nothing to report: every deterministic check passes for this agent."
            : string.Join("\n", findings.Select(f => $"[{f.Severity}] {f.Where}: {f.Message} — {f.Because}"));
    }

    /// <summary>
    /// Declares the pass settled.
    /// </summary>
    /// <remarks>
    /// Believed only as far as the state machine can confirm it. Which fields are set is a fact,
    /// and facts are not a model's to assert.
    /// </remarks>
    public string SetPassCompleted()
    {
        IReadOnlyList<string> missing = state.Missing();

        if (missing.Count > 0)
            return $"Not completed: {string.Join(", ", missing)} still unset. "
                   + (missing.Count == 1 ? "Set it and call this again." : "Set them and call this again.");

        state.ReadyForReview = true;

        return "This pass is settled. Say it is done and what comes next: "
               + state.Pass switch
               {
                   InterviewPass.Functional => "the toolkit — what this agent has to reach for outside the conversation.",
                   InterviewPass.Toolkit => "the agent's own instructions and the way it presents what its tools return.",
                   _ => "the agent joins the domain, and they can review or export it."
               };
    }

    /// <summary>
    /// Finds a declared tool by exact name.
    /// </summary>
    /// <remarks>
    /// Ordinal, because the name becomes a C# method name and <c>MorganaToolAdapter.AddTool</c>
    /// pairs the two exactly. Two tools differing only in case are two tools here and one collision
    /// at startup, which is a finding rather than something to paper over by matching loosely.
    /// </remarks>
    private ToolDraft? Find(string? toolName) =>
        state.Agent.Tools.FirstOrDefault(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// Says what is wrong with a name that has to survive into C#, or nothing if it is fine.
    /// </summary>
    /// <remarks>
    /// Shape only, and deliberately not the compiler: the point is to catch what a domain
    /// conversation actually produces — a space, a hyphen, a leading digit — while it is still free
    /// to change. The casing check is separate because it is a convention rather than a rule, and it
    /// is worth stating: the generated method and the declaration have to read like the framework's
    /// own.
    /// </remarks>
    private static string IdentifierComplaint(string name, string what, bool pascalCase)
    {
        bool shapeOk = name.Length > 0
                       && (char.IsAsciiLetter(name[0]) || name[0] == '_')
                       && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        if (!shapeOk)
            return $"But '{name}' cannot be a C# identifier, and the {what} becomes C# verbatim: "
                   + "it must start with a letter and carry only letters, digits and underscores. Call again with one that can.";

        bool casingOk = pascalCase ? char.IsAsciiLetterUpper(name[0]) : char.IsAsciiLetterLower(name[0]);

        return casingOk
            ? string.Empty
            : $"But the {what} should be {(pascalCase ? "PascalCase" : "camelCase")}, the way the framework's own are. Call again to fix it.";
    }

    /// <summary>
    /// Guarantees a section carries its label. Idempotent.
    /// </summary>
    private static string? Marked(string marker, string? value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith(marker, StringComparison.Ordinal)
            ? value?.Trim()
            : $"{marker} {value.Trim()}";

    /// <summary>
    /// Reports whether a section landed inside the size its doctrine gives it.
    /// </summary>
    /// <remarks>
    /// Recorded either way. The size is a shape, not a gate: a Target of five sentences is still
    /// better than no Target, and Alembic is told so it can tighten rather than blocked so it must.
    /// </remarks>
    private static string Shaped(string section, string? value, int minimum, int maximum)
    {
        int sentences = CountSentences(value);

        if (sentences >= minimum && sentences <= maximum)
            return $"{section} recorded.";

        return $"{section} recorded, but it runs to {sentences} "
               + (sentences == 1 ? "sentence" : "sentences")
               + $" where this section's shape is {minimum} to {maximum}. "
               + (sentences < minimum
                   ? "It is saying less than the section is for. Fill it out and call again."
                   : "Tighten it and call again.");
    }

    /// <summary>
    /// Counts sentences crudely — terminal punctuation followed by a space or the end.
    /// </summary>
    private static int CountSentences(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string trimmed = value.Trim();
        int count = 0;

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] is not ('.' or '!' or '?'))
                continue;

            if (i == trimmed.Length - 1 || char.IsWhiteSpace(trimmed[i + 1]))
                count++;
        }

        // Prose that never reaches terminal punctuation is still one sentence, not none.
        return count == 0 ? 1 : count;
    }
}
