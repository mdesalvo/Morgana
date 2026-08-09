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

    /// <summary>
    /// What is sent to resume a file the provider cut off mid-write.
    /// </summary>
    private const string ContinuationRequest =
        "That was cut off at the provider's output limit. Here is everything you have written so far.\n\n"
        + "Continue from exactly where it stops — the very next character. Do not repeat a line, do not "
        + "start again, do not explain, and do not open a fence. Just carry on.\n\n";

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

        List<ChatMessage> conversation =
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, request.ToString())
        ];

        StringBuilder source = new StringBuilder();

        // Streamed and resumed until the model stops of its own accord, so no output ceiling is
        // declared here at all. A tool class is a source file and its length is a property of the
        // toolkit, not a number anybody can pick in advance: the shipped InventoryTool is eight
        // tools with a catalog and an order book behind them, and any cap large enough for that one
        // is arbitrary for every other. What the tier configures still holds per request; this
        // simply keeps asking until the file is finished.
        while (true)
        {
            ChatFinishReason? finishReason = null;
            int before = source.Length;

            await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(
                               conversation, cancellationToken: cancellationToken))
            {
                finishReason ??= update.FinishReason;

                // Text only. On a reasoning model the updates also carry the thinking, and appending
                // it would put the model's deliberation inside the client's source file.
                foreach (TextContent text in update.Contents.OfType<TextContent>())
                    source.Append(text.Text);
            }

            if (finishReason != ChatFinishReason.Length)
                break;

            // No progress means resuming is not working, and another round would only spend money
            // to produce the same nothing. This is the loop's only exit besides completion, and it
            // is a fact about the last response rather than a budget.
            if (source.Length == before)
                break;

            logger.LogInformation(
                "The mock for {AgentId} was cut at the provider's limit after {Length} characters; resuming",
                agent.ID, source.Length);

            conversation =
            [
                conversation[0],
                conversation[1],
                new ChatMessage(ChatRole.User, ContinuationRequest + source)
            ];
        }

        string authored = Unfenced(source.ToString());

        // Thrown rather than returned, so the packager's per-agent catch writes a file that says
        // what happened instead of one that is silently empty. An empty source file is the one
        // outcome here that looks like success and is not.
        if (authored.Length == 0)
            throw new InvalidOperationException(
                $"The model returned no source for {agent.Code.ToolClassName ?? intentName}: the whole response was "
                + "reasoning and no text. That is what a MaxOutputTokens too small for a source file produces — Alembic's "
                + "own tier declares none for exactly this reason, so check what the deployment configures.");

        return authored;
    }

    /// <summary>
    /// Strips a markdown code fence if the answer arrived wrapped in one.
    /// </summary>
    /// <remarks>
    /// The prompt asks for bare C#, and a model that complies loses nothing here. This exists
    /// because the failure it prevents is a file that does not compile over three backticks, which
    /// is a poor thing to hand a client at the end of an interview.
    /// </remarks>
    private static string Unfenced(string answer)
    {
        string text = answer.Trim();

        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        text = text[(firstNewline + 1)..];
        int closing = text.LastIndexOf("```", StringComparison.Ordinal);

        return (closing >= 0 ? text[..closing] : text).TrimEnd() + "\n";
    }
}
