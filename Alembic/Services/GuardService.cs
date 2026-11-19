using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Models;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IGuardService"/>: one completion per judgment, on the Efficiency tier, answered as JSON.
/// Alembic turns what the client types or uploads into the prose of agents that speak to their own users and a mock runs unread on their machine, so each is read before it is used.
/// </summary>
public class GuardService : IGuardService
{
    /// <summary>The prompt in <c>alembic.json</c> that governs judging what the client writes or uploads.</summary>
    private const string AuthorGuardPromptId = "AuthorGuard";

    /// <summary>The prompt in <c>alembic.json</c> that governs judging a mock against its tools' descriptions.</summary>
    private const string CodeGuardPromptId = "CodeGuard";

    /// <summary>The property of the <c>AuthorGuard</c> prompt listing the sentences it shares verbatim with Morgana's UserGuard.</summary>
    private const string UserGuardSentencesProperty = "UserGuardSentences";

    /// <summary>The opening of every placeholder the <c>AuthorGuard</c> instructions hold for a sentence of Morgana's UserGuard.</summary>
    private const string UserGuardPlaceholderPrefix = "((userguard_";

    /// <summary>Resolves the guards' prompts.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Resolves Morgana's UserGuard, whose doctrine of malice the AuthorGuard shares.</summary>
    private readonly IPromptResolverService promptResolverService;

    /// <summary>Supplies the chat client, on the Efficiency tier like all of Alembic's model work.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Records a judgment that was resumed or went silent.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the guards to their prompts, Morgana's UserGuard and the model.</summary>
    /// <param name="alembicPromptService">Resolves the <c>AuthorGuard</c> and <c>CodeGuard</c> prompts from <c>alembic.json</c>.</param>
    /// <param name="promptResolverService">Resolves Morgana's UserGuard from <c>morgana.json</c>.</param>
    /// <param name="llmService">Supplies the chat client on the Efficiency tier.</param>
    /// <param name="logger">Records a judgment that was resumed or went silent.</param>
    public GuardService(
        IAlembicPromptService alembicPromptService,
        IPromptResolverService promptResolverService,
        ILanguageModelService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.promptResolverService = promptResolverService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <summary>Judges an answer the client has just typed, together with the question it replies to.</summary>
    public async Task<GuardVerdict> JudgeAnswerAsync(string? question, string answer, CancellationToken cancellationToken = default)
    {
        // A question is sent only where there was one: a null property would read as a question the client was asked and left empty.
        AnswerRequest request = new AnswerRequest(
            "answer", string.IsNullOrWhiteSpace(question) ? null : question, answer);

        // Serialized as Morgana's own guards serialize theirs, so quotes and accents arrive as typed rather than as escapes the guard could take for an encoding.
        return await JudgeAsync(AuthorGuardPromptId, JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions), cancellationToken);
    }

    /// <summary>Judges a file the client has uploaded.</summary>
    public async Task<GuardVerdict> JudgeUploadAsync(string uploadedText, CancellationToken cancellationToken = default)
    {
        // An upload answers no question, so its request carries none.
        UploadRequest request = new UploadRequest("upload", uploadedText);

        // Serialized like an answer, so the file reaches the guard as it was written.
        return await JudgeAsync(AuthorGuardPromptId, JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions), cancellationToken);
    }

    /// <summary>Judges a mock against the generated half that carries its tools' descriptions.</summary>
    public async Task<GuardVerdict> JudgeMockAsync(string contract, string? target, string mock, CancellationToken cancellationToken = default)
    {
        // The target is sent only where the agent has one written: a null property would read as a purpose declared empty.
        MockRequest request = new MockRequest(
            contract, string.IsNullOrWhiteSpace(target) ? null : target, mock);

        // Serialized like an answer, so the source reaches the guard as it was written.
        return await JudgeAsync(CodeGuardPromptId, JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions), cancellationToken);
    }

    /// <summary>
    /// Puts one request to one guard and reads its verdict, throwing <see cref="InvalidOperationException"/> when no verdict can be read.
    /// A text the guard could not judge is never passed on the strength of that failure: the caller stops instead.
    /// </summary>
    private async Task<GuardVerdict> JudgeAsync(string promptId, string request, CancellationToken cancellationToken)
    {
        // The guard's own prose, which defines the JSON it answers.
        Records.Prompt guard = alembicPromptService.Resolve(promptId);

        // The AuthorGuard carries Morgana's own doctrine of malice, spliced in word for word so the two guards cannot drift apart.
        if (string.Equals(promptId, AuthorGuardPromptId, StringComparison.Ordinal))
        {
            // The sentences the AuthorGuard declares it shares, in the order its placeholders number them.
            List<string> sentences = guard.GetAdditionalPropertyOrDefault<List<string>>(UserGuardSentencesProperty, []);

            // Morgana's UserGuard as the framework runs it, the one place the doctrine is actually written.
            Records.Prompt userGuard = await promptResolverService.ResolveAsync(Constants.Prompts.UserGuard);

            // A sentence Morgana's guard no longer carries would leave the two guards judging by different rules.
            foreach (string sentence in sentences)
                if (!userGuard.Instructions.Contains(sentence, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The sentence \"{sentence}\" is not in the UserGuard of morgana.json: the AuthorGuard no longer shares it with morgana.json.");

            // Each placeholder takes the sentence its number names.
            string instructions = guard.Instructions;

            for (int number = 1; number <= sentences.Count; number++)
                instructions = instructions.Replace(
                    string.Create(CultureInfo.InvariantCulture, $"{UserGuardPlaceholderPrefix}{number}))"),
                    sentences[number - 1], StringComparison.Ordinal);

            // A placeholder left over would reach the model as literal text where a sentence of the doctrine belongs.
            if (instructions.Contains(UserGuardPlaceholderPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The AuthorGuard holds a placeholder that {UserGuardSentencesProperty} does not fill.");

            // The guard is composed from the instructions that now carry Morgana's sentences.
            guard = guard with { Instructions = instructions };
        }

        // A one-shot JSON answer speaks to nobody, so the guard carries no personality.
        string system = string.Join("\n\n",
            new[]
            {
                Records.Prompt.Labeled(Constants.SectionLabels.Target, guard.Target),
                Records.Prompt.Labeled(Constants.SectionLabels.Instructions, guard.Instructions),
                Records.Prompt.Labeled(Constants.SectionLabels.Formatting, guard.Formatting)
            }.Where(section => section.Length > 0));

        // Reading a text for what it hides is the job the interview's tier is chosen for.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        // The guard's answer, read below once the call has finished.
        string answer;

        try
        {
            // The text under judgment, resumed until the guard finishes its answer.
            answer = await StreamedCompletion.RunAsync(
                chatClient, system, request,
                length => logger.LogInformation("The {Guard} verdict was cut at {Length} characters; resuming", promptId, length),
                length => logger.LogWarning("The {Guard} went silent after {Length} characters; retrying once", promptId, length),
                cancellationToken);
        }
        catch (Exception ex) when (llmService.GetLanguageModel(Records.LLMTier.Efficiency).IsContentFilterRefusal(ex))
        {
            // The provider refusing to read the text at all is a verdict and not a failure to judge: a text that
            // no model will read is not passed and the client reads the guard's own sentence for it.
            logger.LogWarning(ex, "The provider refused to read the text put to the {Guard}; refusing it", promptId);

            // The sentence that the guard's prompt words for this refusal, since the guard never wrote a verdict of its own.
            string? contentFilteredMessage = guard.GetAdditionalPropertyOrDefault<string?>(Constants.PromptProperties.ContentFilteredMessage, null);

            // A guard whose prompt words no such sentence would hand the client a refusal that says nothing.
            if (string.IsNullOrWhiteSpace(contentFilteredMessage))
                throw new InvalidOperationException(
                    $"The {promptId} prompt declares no {Constants.PromptProperties.ContentFilteredMessage} property: the refusal of the provider cannot be worded.");

            // The refusal reaches the client as any other refused text.
            return new GuardVerdict(false, contentFilteredMessage);
        }

        // An empty answer judges nothing and a guard that said nothing never passes a text.
        if (answer.Length == 0)
            throw new InvalidOperationException("The guard returned nothing.");

        // Read inside the try below, which turns an answer in any other shape into a failure to judge.
        Verdict? verdict;

        try
        {
            // The answer in the shape the guard's Formatting asks for.
            verdict = JsonSerializer.Deserialize<Verdict>(
                answer, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            // An answer that is not JSON is no verdict: the text it was about is not passed.
            throw new InvalidOperationException($"The guard did not answer in the agreed shape: {ex.Message}", ex);
        }

        // A JSON null is no verdict either.
        if (verdict is null)
            throw new InvalidOperationException("The guard did not answer in the agreed shape: the answer was null.");

        // A refusal the client cannot read the reason of is one nobody can act on.
        if (!verdict.Compliant && string.IsNullOrWhiteSpace(verdict.Violation))
            throw new InvalidOperationException("The guard refused without saying why.");

        // A passed text carries no sentence and a refused one carries the sentence the client reads.
        return new GuardVerdict(verdict.Compliant, verdict.Compliant ? null : verdict.Violation!.Trim());
    }

    /// <summary>What the guard answers: whether the text passes and, when it does not, the sentence the client reads.</summary>
    private sealed record Verdict(
        [property: JsonPropertyName("compliant")] bool Compliant,
        [property: JsonPropertyName("violation")] string? Violation);

    /// <summary>The request the AuthorGuard reads for an answer the client typed.</summary>
    private sealed record AnswerRequest(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("question"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Question,
        [property: JsonPropertyName("text")] string Text);

    /// <summary>The request the AuthorGuard reads for a file the client uploaded.</summary>
    private sealed record UploadRequest(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("text")] string Text);

    /// <summary>The request the CodeGuard reads for a mock and the contract it implements.</summary>
    private sealed record MockRequest(
        [property: JsonPropertyName("contract")] string Contract,
        [property: JsonPropertyName("target"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target,
        [property: JsonPropertyName("mock")] string Mock);
}