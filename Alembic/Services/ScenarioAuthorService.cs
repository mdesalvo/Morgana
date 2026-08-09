using System.Reflection;
using System.Text;
using Alembic.Interfaces;
using Alembic.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IScenarioAuthorService"/>: one call per agent, split into files here.
/// </summary>
/// <remarks>
/// One call and several files, because the scenarios for one agent are written against each other:
/// what the first one covers is exactly what the second should not repeat. Splitting afterwards, on
/// the YAML document separator, keeps that while leaving the file names Alembic's — a path is not a
/// thing to take from a model's output.
/// </remarks>
public class ScenarioAuthorService : IScenarioAuthorService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> that governs scenario authoring.
    /// </summary>
    private const string ScenariosPromptId = "Scenarios";

    /// <summary>
    /// Everything Alembic knows about the harness, assembled once from this assembly alone.
    /// </summary>
    /// <remarks>
    /// The backpack, and it is packed at compile time: the briefing prose, the scenarios that
    /// actually ship with the suite, and a key table generated off the harness's own type. At
    /// runtime nothing is read from a filesystem, because Alembic lives wherever the client deployed
    /// it and there is no <c>PromptHarness/</c> beside it there.
    /// </remarks>
    private static readonly Lazy<string> Briefing = new(LoadBriefing);

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

        // The briefing rides on the request rather than the system prompt: it is reference material
        // about an instrument, not a rule about how to behave, and the four sections are for rules.
        string request = Briefing.Value
                         + "\n\n" + ScenarioSchema.Vocabulary()
                         + "\n\n# The agent to write scenarios for\n\n"
                         + Describe(agent, intentName);

        string yaml = await StreamedCompletion.RunAsync(
            chatClient, system, request,
            length => logger.LogInformation(
                "The scenarios for {AgentId} were cut at the provider's limit after {Length} characters; resuming",
                agent.ID, length),
            cancellationToken);

        return [.. Split(yaml, intentName)];
    }

    /// <summary>
    /// Assembles the briefing: the prose, the real exemplars, then the generated vocabulary.
    /// </summary>
    private static string LoadBriefing()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        StringBuilder sb = new StringBuilder();
        sb.AppendLine(Read(assembly, ".harness.md"));

        // Spliced where the prose stops, in file-name order so the briefing is byte-stable across
        // builds: the same domain then produces the same request, which is what makes a difference
        // in the output attributable to the domain rather than to the exemplars shuffling.
        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Exemplars.", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            sb.AppendLine("```yaml");
            sb.AppendLine(Read(assembly, name).TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads one embedded resource, matched by the suffix of its manifest name.
    /// </summary>
    /// <remarks>
    /// By suffix rather than by the namespace-prefixed manifest name, the way
    /// <c>ConfigurationPromptResolverService</c> finds <c>morgana.json</c>: renaming the assembly
    /// must not silently empty the backpack.
    /// </remarks>
    private static string Read(Assembly assembly, string suffix)
    {
        string resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"No embedded resource ending in '{suffix}' in Alembic.");

        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using StreamReader reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

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

    /// <summary>
    /// Cuts a multi-document answer into files, naming each from its own <c>id:</c>.
    /// </summary>
    /// <remarks>
    /// A document without a readable <c>id:</c> is dropped rather than saved under a made-up name.
    /// <c>ScenarioLoader</c> finds a scenario by file name and the test that runs it names the same
    /// string, so a file whose name Alembic invented is one nothing will ever load.
    /// </remarks>
    private static IEnumerable<EmittedFile> Split(string yaml, string intentName)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            yield break;

        foreach (string document in yaml.Split("\n---", StringSplitOptions.RemoveEmptyEntries))
        {
            string text = document.TrimStart('-', '\n', '\r', ' ').TrimEnd() + "\n";

            (string? id, string? problem) = ScenarioSchema.Verify(text);

            // Without an id there is no file name anybody would load it under, and inventing one
            // would produce a scenario nothing ever runs. Dropped, and the caller logs it.
            if (string.IsNullOrWhiteSpace(id))
                continue;

            // The path is Alembic's, always: the harness loads a scenario by file name, and a name
            // taken from a model's output is a name nothing agrees on.
            string safe = new string([.. id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);

            if (safe.Length == 0)
                safe = intentName.ToLowerInvariant();

            // A scenario the strict parse rejected still ships, and ships saying so at the top. The
            // failure it guards against is silent: the harness's own loader is forgiving, so the
            // same file would load, run, pass, and assert less than it appears to.
            string content = problem is null
                ? text
                : $"# ⚠ Alembic could not verify this scenario against PromptHarness's own schema:\n"
                  + $"# {problem}\n"
                  + "# It may still load — the harness ignores what it does not recognise — but any line\n"
                  + "# it cannot bind asserts nothing. Fix it before trusting a pass.\n"
                  + text;

            yield return new EmittedFile($"Scenarios/{safe}.yaml", content, FileOwnership.Client);
        }
    }
}
