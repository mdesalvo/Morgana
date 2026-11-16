using System.Globalization;
using System.Text;
using Alembic.Interfaces;
using Alembic.Models;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="INamespaceProposalService"/>: one short completion over what is known of the business.
/// It never throws because a missing proposal leaves the box empty for the client to type, which is how the card worked before.
/// </summary>
public class NamespaceProposalService : INamespaceProposalService
{
    /// <summary>The prompt in <c>alembic.json</c> that governs naming the project.</summary>
    private const string NamerPromptId = "NamespaceNamer";

    /// <summary>The length every name stays under, the same limit the namer is told.</summary>
    private const int NameLengthLimit = 40;

    /// <summary>Resolves the namer's prompt.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Supplies the chat client, on the Efficiency tier like all of Alembic's model work.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Records provider failures and answers that were not a name.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the proposal to its prompt and the model.</summary>
    /// <param name="alembicPromptService">Resolves the <c>NamespaceNamer</c> prompt from <c>alembic.json</c>.</param>
    /// <param name="llmService">Supplies the chat client on the Efficiency tier.</param>
    /// <param name="logger">Records provider failures and answers that were not a name.</param>
    public NamespaceProposalService(
        IAlembicPromptService alembicPromptService,
        ILanguageModelService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <summary>Asks the model for one namespace segment named after the business; never throws.</summary>
    public async Task<string?> ProposeAsync(DomainDraft draft, CancellationToken cancellationToken = default)
    {
        // The namer's own prose, which defines the single line it answers with.
        Records.Prompt namer = alembicPromptService.Resolve(NamerPromptId);

        // A one-shot answer speaks to nobody, so the namer carries no personality.
        string system = string.Join("\n\n",
            new[]
            {
                Records.Prompt.Labeled(Constants.SectionLabels.Target, namer.Target),
                Records.Prompt.Labeled(Constants.SectionLabels.Instructions, namer.Instructions),
                Records.Prompt.Labeled(Constants.SectionLabels.Formatting, namer.Formatting)
            }.Where(section => section.Length > 0));

        // Weighing what a business is called is the same reading the interview's tier is chosen for.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        try
        {
            // The business and its intents in one request, resumed until the namer finishes its answer.
            string answer = await StreamedCompletion.RunAsync(
                chatClient,
                system,
                NamingRequest(draft),
                length => logger.LogInformation("The namespace proposal was cut at {Length} characters; resuming", length),
                length => logger.LogWarning("The namespace proposal went silent after {Length} characters; retrying once", length),
                cancellationToken);

            // An empty answer offers nothing and the box stays for the client to type in.
            if (answer.Length == 0)
                return null;

            // The answer is the name and nothing else: one carrying quotes or a sentence is refused below, never cut down to a guess.
            string proposal = answer.Trim();

            // A proposal that is not one identifier segment would be written into every generated class's namespace.
            if (!IsSingleSegment(proposal))
            {
                logger.LogWarning("The namespace proposal was not a single C# identifier segment: {Answer}", answer);
                return null;
            }

            // The name the client reads in the box.
            return proposal;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client left the card before the model answered, so there is nobody to offer a name to.
            return null;
        }
        catch (Exception ex)
        {
            // A provider failure costs the head start only: the client types the namespace as before.
            logger.LogError(ex, "The namespace proposal failed");
            return null;
        }
    }

    /// <summary>Whether a name is one C# identifier segment of letters and digits with a letter first.</summary>
    private static bool IsSingleSegment(string name) =>
        name.Length is > 0 and < NameLengthLimit
        && char.IsLetter(name[0])
        && name.All(char.IsLetterOrDigit);

    /// <summary>The business as far as it is known and the intents of the domain, in the two blocks the namer reads.</summary>
    private static string NamingRequest(DomainDraft draft)
    {
        // The request grows block by block, in the order the namer's prose reads them.
        StringBuilder request = new StringBuilder();

        // What the interview or the reading found out about the trade, which is where a business gives its own name.
        if (draft.Learned.Count == 0)
            request.AppendLine("Nothing is known about the business beyond its intents.");
        else
        {
            // Said once, so the namer reads every line under it as the trade's own.
            request.AppendLine("What is known about the business:");

            // Each fact under its subject, the way the interview filed it.
            foreach (KnownFact fact in draft.Learned)
                request.AppendLine(CultureInfo.InvariantCulture, $"- {fact.Subject}: {fact.Fact}");
        }

        // The intents say what the business installs Morgana to run, which names the trade when no fact does.
        request.AppendLine();
        request.AppendLine("The intents of the domain:");

        foreach (IntentDraft intent in draft.Intents.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name)))
        {
            // An intent still without its sentence is listed by name alone.
            if (string.IsNullOrWhiteSpace(intent.Description))
                request.AppendLine(CultureInfo.InvariantCulture, $"- {intent.Name}");
            else
                request.AppendLine(CultureInfo.InvariantCulture, $"- {intent.Name}: {intent.Description}");
        }

        // The whole request, the user message the namer answers.
        return request.ToString();
    }
}