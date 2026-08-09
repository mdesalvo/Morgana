using System.Text;
using Alembic.Interfaces;
using Alembic.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IToolMockService"/>: one completion per agent, on the Performance tier.
/// </summary>
/// <remarks>
/// A single completion rather than an agent with tools, because nothing here is a conversation:
/// there is one input, the toolkit, and one output, a file. The interview needed tools to hold a
/// state machine steady across many turns; this needs none of that, and giving it an agent would be
/// machinery in place of a call.
/// </remarks>
public class ToolMockService : IToolMockService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> that governs mock authoring.
    /// </summary>
    private const string MockPromptId = "Mock";

    private readonly IAlembicPromptService alembicPromptService;
    private readonly ICodeEmitService codeEmitService;
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the mock service.
    /// </summary>
    public ToolMockService(
        IAlembicPromptService alembicPromptService,
        ICodeEmitService codeEmitService,
        ILLMService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.codeEmitService = codeEmitService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> AuthorAsync(AgentDraft agent, string intentName, CancellationToken cancellationToken = default)
    {
        Records.Prompt mock = alembicPromptService.Resolve(MockPromptId);

        // Composed without Morgana's layer, unlike every interview pass. Her Personality is how she
        // speaks to someone; this call speaks to nobody and emits a file. Handing it a voice would
        // be the same defect the interview doctrine warns about, pointed the other way.
        string system = string.Join("\n\n",
            new[] { mock.Target, mock.Instructions, mock.Formatting }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // The generated half IS the specification: it carries the exact signatures the answer must
        // implement, the class name, the namespace and the base class. Describing them in prose as
        // well would be a second, drifting statement of something already exact.
        EmittedFile signatures = codeEmitService.Emit(agent, intentName)
            .First(f => f.Path.Contains("/Tools/", StringComparison.Ordinal) || f.Path.StartsWith("Tools/", StringComparison.Ordinal));

        StringBuilder request = new StringBuilder();
        request.AppendLine("This is the generated half of the tool class. Write the other half.");
        request.AppendLine();
        request.AppendLine(signatures.Content);
        request.AppendLine();
        request.AppendLine($"The agent this toolkit belongs to exists for: {agent.Target}");

        if (!string.IsNullOrWhiteSpace(agent.Formatting))
        {
            request.AppendLine();
            request.AppendLine($"It presents what these tools return like this, so return data that makes it possible: {agent.Formatting}");
        }

        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        string authored = await StreamedCompletion.RunAsync(
            chatClient, system, request.ToString(),
            length => logger.LogInformation(
                "The mock for {AgentId} was cut at the provider's limit after {Length} characters; resuming",
                agent.ID, length),
            cancellationToken);

        // Thrown rather than returned, so the packager's per-agent catch writes a file that says
        // what happened instead of one that is silently empty. An empty source file is the one
        // outcome here that looks like success and is not.
        if (authored.Length == 0)
            throw new InvalidOperationException(
                $"The model returned no source for {agent.Code.ToolClassName ?? intentName}: the whole response was "
                + "reasoning and no text. That is what a MaxOutputTokens too small for a source file produces — Alembic's "
                + "own tier declares a generous one for exactly this reason, so check what the deployment configures.");

        return authored;
    }

}
