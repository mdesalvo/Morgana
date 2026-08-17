using System.Text;
using Distiller.Harness;
using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Distiller.Services;

/// <summary>
/// Default <see cref="IScenarioAuthorService"/>: one derivation per behavioural use-case.
/// </summary>
/// <remarks>
/// <para>
/// One call per template, and that is the design rather than an implementation detail. A single
/// call asking for "two or three scenarios" makes the model choose which behaviours matter, which
/// is the one decision it is worst placed to make — it has seen this domain for the length of one
/// request, and the answer is the same three shapes every time. The use-cases are chosen here,
/// once, by people who know what breaks a domain agent; what the model does is the part it is good
/// at, which is putting this domain's words into a shape already decided.
/// </para>
/// <para>
/// It also gives the model somewhere to say no. A template that this domain has no instance of
/// comes back <c>not-applicable</c> and is dropped, which is only possible because each one is
/// asked about on its own.
/// </para>
/// </remarks>
public class ScenarioAuthorService : IScenarioAuthorService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> that governs scenario derivation.
    /// </summary>
    private const string ScenariosPromptId = "HarnessMocker";

    /// <summary>
    /// How a placeholder is written in a template, quoted back to the model as its own notation.
    /// </summary>
    private const string Placeholder = "{{double braces}}";

    private readonly IAlembicPromptService alembicPromptService;
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the scenario author.
    /// </summary>
    public ScenarioAuthorService(
        IAlembicPromptService alembicPromptService,
        ILLMService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmittedFile>> AuthorAsync(
        AgentDraft agent,
        string intentName,
        CancellationToken cancellationToken = default)
    {
        Records.Prompt scenarios = alembicPromptService.Resolve(ScenariosPromptId);

        string system = string.Join("\n\n",
            new[] { scenarios.Target, scenarios.Instructions, scenarios.Formatting }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        // The domain is described once and repeated on every request. Each derivation is its own
        // session with no memory of the last, which is what keeps the scenarios from converging:
        // a model that has just written the happy path will otherwise write the refusal around it.
        string domain = Describe(agent, intentName);

        List<EmittedFile> files = [];

        // A template that fails is dropped the way one that does not apply is dropped, rather than
        // taking the agent's whole set with it: each is a separate request about a separate
        // use-case, so the ones that answered are worth keeping. The list is abandoned after the
        // second failure all the same — one template failing is a template, two is the provider,
        // and walking the rest to time out against it twice apiece serves nobody. Whether that
        // leaves the client anything is what decides how it ends, below.
        int failures = 0;

        foreach (ScenarioTemplate template in ScenarioTemplateLibrary.For(agent))
        {
            string id = $"{intentName}-{template.Name}";
            string answer;

            try
            {
                answer = await StreamedCompletion.RunAsync(
                    chatClient,
                    system,
                    Request(template, domain),
                    length => logger.LogInformation(
                        "The {Template} derivation for {AgentId} was cut at the provider's limit after {Length} characters; resuming",
                        template.Name, agent.ID, length),
                    length => logger.LogWarning(
                        "The {Template} derivation for {AgentId} went silent after {Length} characters; retrying once",
                        template.Name, agent.ID, length),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "The {Template} derivation for {AgentId} could not be run", template.Name, agent.ID);

                if (++failures <= 1)
                    continue;

                // Nothing derived means nothing to hand over, and a client who asked for scenarios
                // and silently received none has been told nothing: that case is worth the caller's
                // FAILED.txt. With something in hand it is not, since a template dropping out is
                // already how a use-case this domain has no instance of leaves the set.
                if (files.Count == 0)
                    throw;

                break;
            }

            DerivedScenario derivation = ScenarioDerivation.Check(template.Keys, id, answer);

            if (derivation.NotApplicable is { } reason)
            {
                // Not a failure and not silent: a use-case the domain has no instance of is a fact
                // about the domain, and the client's README says which ones came back empty.
                logger.LogInformation(
                    "{AgentId} has no instance of {Template}: {Reason}", agent.ID, template.Name, reason);

                continue;
            }

            if (derivation.Content is null)
            {
                logger.LogWarning(
                    "The {Template} derivation for {AgentId} produced nothing usable: {Problem}",
                    template.Name, agent.ID, derivation.Problem);

                continue;
            }

            if (derivation.Problem is { } problem)
                logger.LogWarning(
                    "The {Template} derivation for {AgentId} ships flagged: {Problem}",
                    template.Name, agent.ID, problem);

            files.Add(new EmittedFile($"Scenarios/{id}.yaml", Flag(derivation, template.Name), FileOwnership.Client));
        }

        files.AddRange(await DiscretionaryAsync(chatClient, system, domain, intentName, agent, files, cancellationToken));

        return files;
    }

    /// <summary>
    /// Asks for what only this domain needs, once the templated base is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base protects what is worth protecting in any domain, which is the part known in advance
    /// and therefore the part that belongs in a template. It cannot protect the rule that holds only
    /// here — the step that must never be taken twice, the two things this business never says in
    /// the same breath — because nobody knew about it until the interview was over. This is where
    /// that gets written, by the one reader who has just seen the whole domain.
    /// </para>
    /// <para>
    /// It runs last and is handed the base in full, for the one constraint that matters: an extra
    /// scenario must neither repeat what is already asserted nor contradict it. A suite where two
    /// files disagree about the same turn is worse than one that never covered it, because the
    /// failing one gets deleted and nobody remembers which was right.
    /// </para>
    /// <para>
    /// Held to <see cref="ScenarioTemplateLibrary.Vocabulary"/> rather than to one template's keys:
    /// a scenario nobody anticipated may need any key Alembic can vouch for, and none it cannot.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<EmittedFile>> DiscretionaryAsync(
        IChatClient chatClient,
        string system,
        string domain,
        string intentName,
        AgentDraft agent,
        IReadOnlyList<EmittedFile> baseline,
        CancellationToken cancellationToken)
    {
        // With no base to sit beside, there is nothing to be non-redundant against and nothing to
        // read the idiom from — that domain's problem is the base, and this would not fix it.
        if (baseline.Count == 0)
            return [];

        string answer = await StreamedCompletion.RunAsync(
            chatClient,
            system,
            DiscretionaryRequest(domain, baseline),
            length => logger.LogInformation(
                "The discretionary scenarios for {AgentId} were cut at the provider's limit after {Length} characters; resuming",
                agent.ID, length),
            length => logger.LogWarning(
                "The discretionary scenarios for {AgentId} went silent after {Length} characters; retrying once",
                agent.ID, length),
            cancellationToken);

        List<EmittedFile> files = [];
        HashSet<string> taken = [.. baseline.Select(f => f.Path)];

        foreach (string document in Documents(answer))
        {
            // The model's own id says what it thought it was writing, which is the only name a
            // reader would look for the file under. The shape of it is Alembic's, always: the
            // harness loads by file name, and a name out of a model's output is one nothing agrees on.
            string id = Identify(document, intentName, taken);

            DerivedScenario derivation = ScenarioDerivation.Check(
                ScenarioTemplateLibrary.Vocabulary, id, document);

            if (derivation.NotApplicable is not null || derivation.Content is null)
            {
                if (derivation.Content is null && derivation.NotApplicable is null)
                    logger.LogWarning(
                        "A discretionary scenario for {AgentId} produced nothing usable: {Problem}",
                        agent.ID, derivation.Problem);

                continue;
            }

            if (derivation.Problem is { } problem)
                logger.LogWarning(
                    "The discretionary scenario {Id} for {AgentId} ships flagged: {Problem}",
                    id, agent.ID, problem);

            taken.Add($"Scenarios/{id}.yaml");
            files.Add(new EmittedFile($"Scenarios/{id}.yaml", Flag(derivation, "domain"), FileOwnership.Client));
        }

        return files;
    }

    /// <summary>
    /// Assembles the discretionary request: the domain, the base in full, and the one constraint.
    /// </summary>
    private static string DiscretionaryRequest(string domain, IReadOnlyList<EmittedFile> baseline) =>
        $"""
         # The agent

         {domain}

         # What is already being tested

         These scenarios are written and will ship. Read them as the floor you are standing on.

         {string.Join("\n\n", baseline.Select(f => $"```yaml\n{f.Content.Trim()}\n```"))}

         # What is missing

         Those cover what is worth protecting in any domain. You have read this one, and nobody else
         has. If there is a rule here that holds nowhere else — a step that must never happen twice,
         an order this business insists on, something it never says in the same breath as something
         else — write the scenario that would catch it being broken.

         Every one you write must be true of this domain and of no other, must assert something none
         of the above already asserts, and must not contradict any of them about the same turn. Use
         only keys that appear in the scenarios above.

         Two at the very most, and none is the ordinary answer: a domain whose behaviour the base
         already describes is a well-designed domain, not a gap. If that is what you find, reply with
         `{ScenarioDerivation.NotApplicableMarker} ` and say so in one line.
         """;

    /// <summary>
    /// Splits a multi-document answer.
    /// </summary>
    private static IEnumerable<string> Documents(string answer) =>
        answer.Split("\n---", StringSplitOptions.RemoveEmptyEntries)
            .Select(document => document.TrimStart('-', '\n', '\r', ' ').TrimEnd())
            .Where(document => document.Length > 0);

    /// <summary>
    /// Builds the id for a discretionary scenario: the intent, the model's own words, unique.
    /// </summary>
    private static string Identify(string document, string intentName, IReadOnlySet<string> taken)
    {
        string? declared = document.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("id:", StringComparison.Ordinal))
            ?[3..]
            .Trim()
            .Trim('"', '\'');

        string slug = new string([.. (declared ?? "domain").ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');

        // Whatever the model called it, the file is namespaced by the intent the way every other
        // scenario in the domain is — including when the model already did it itself.
        if (!slug.StartsWith(intentName + "-", StringComparison.OrdinalIgnoreCase))
            slug = $"{intentName}-{slug}";

        string candidate = slug;

        for (int n = 2; taken.Contains($"Scenarios/{candidate}.yaml"); n++)
            candidate = $"{slug}-{n}";

        return candidate;
    }

    /// <summary>
    /// Assembles one derivation request: the use-case, the brief, the template, the domain.
    /// </summary>
    /// <remarks>
    /// In that order on purpose. The template is the last thing read before the domain, so the shape
    /// is fresh when the words arrive; the domain is last of all, because it is the material and
    /// everything above it is instruction about what to do with material.
    /// </remarks>
    private static string Request(ScenarioTemplate template, string domain) =>
        $"""
         # The behaviour to protect

         {template.UseCase}

         # What deriving it decides

         {template.Derive}

         # The template

         Everything in `{Placeholder}` is a placeholder for this domain's own words. Replace
         every one of them, keep the rest — the keys, their order, the thresholds and the comments —
         and return the result. Drop a key the domain gives you nothing to put in; never add one.

         ```yaml
         {template.Body}
         ```

         # The agent this is for

         {domain}
         """;

    /// <summary>
    /// Writes an unverifiable derivation's problem across the top of the file.
    /// </summary>
    /// <remarks>
    /// The failure it guards against is a silent one, so the warning has to live in the artifact
    /// rather than in a log the client never sees: the harness's own loader is forgiving, and the
    /// same file would load, run, pass, and assert less than it appears to.
    /// </remarks>
    private static string Flag(DerivedScenario derivation, string source) =>
        derivation.Problem is null
            ? derivation.Content!
            : $"# ⚠ Alembic could not verify this scenario against the {source} vocabulary:\n"
              + $"# {derivation.Problem}\n"
              + "# It ships anyway, because you should see it. Fix it before trusting a pass.\n"
              + derivation.Content;

    /// <summary>
    /// States the agent exactly as the agent's own model will read it.
    /// </summary>
    /// <remarks>
    /// The prose and the toolkit, and nothing about what the tools return: a scenario asserts what a
    /// user would see and which tools ran, never a value out of a mock nobody has to keep. An
    /// assertion on mock data is a scenario that fails the day the client wires the real system in —
    /// which is the one day the suite most needs to still work.
    /// </remarks>
    private static string Describe(AgentDraft agent, string intentName)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine($"Agent class: {agent.Code.AgentClassName ?? intentName + "Agent"}");
        sb.AppendLine($"Intent: {intentName}");
        sb.AppendLine();
        sb.AppendLine(agent.Target);
        sb.AppendLine();
        sb.AppendLine(agent.Instructions);
        sb.AppendLine();
        sb.AppendLine(agent.Formatting);
        sb.AppendLine();

        if (agent.Tools.Count == 0)
        {
            sb.AppendLine("It declares no native tools: its tools arrive at runtime from an MCP server, so their names are not knowable here.");

            return sb.ToString();
        }

        sb.AppendLine("Its tools:");

        foreach (ToolDraft tool in agent.Tools)
        {
            sb.AppendLine($"- {tool.Name}: {tool.Description}");

            foreach (ToolParameterDraft parameter in tool.Parameters)
                sb.AppendLine($"    {parameter.Name} [{parameter.Scope ?? "authored by the agent"}{(parameter.Required ? "" : ", optional")}{(parameter.Shared ? ", shared" : "")}]: {parameter.Description}");
        }

        return sb.ToString();
    }
}
