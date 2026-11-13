using System.ComponentModel;
using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Tools;

/// <summary>The tools of the pass that writes an agent's Personality.</summary>
public sealed class AgentPersonalityTool : InterviewTool
{
    /// <summary>Binds the toolset to one interview.</summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The checks decidable by reading the Draft.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    public AgentPersonalityTool(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
        : base(interviewState, draftStateService, draftValidationService, recapService)
    {
    }

    /// <inheritdoc />
    protected override string NextStep => "the toolkit — what this agent has to reach for outside the conversation.";

    /// <inheritdoc />
    [Description("Returns the agent as it currently stands: the intent it answers, what it is for, where it stops, how it sounds and what it can reach. Each step runs with a fresh memory and only the configuration carries over, so this is the whole of what you know about it. On an agent already in the client's domain, all of it was written before today.")]
    public override string GetAgentSoFar() => base.GetAgentSoFar();

    /// <summary>
    /// Offers words for the agent's voice, to be picked from freely, several at a time.
    /// They are not buttons, since none is a whole answer; the model writes them because they must fit this domain and Morgana's voice.
    /// </summary>
    [Description("Offers words for this agent's voice under the question you are about to ask, to be picked from freely: several, one or none. The text box stays open beside them. Call it BEFORE writing the question. Every word must be a way Morgana herself could speak: this section specialises her voice and never replaces it.")]
    public string SetTraits(
        [Description("JSON array of eight to fourteen single adjectives, in the client's language: [\"precise\",\"unhurried\",\"reassuring\"]. Real alternatives rather than shades of one temper, each plausible for THIS domain and none of them a way Morgana would never speak.")] string traits)
    {
        try
        {
            // The words arrive as a JSON array in one string, the only list shape a tool parameter carries reliably.
            List<string>? parsed = JsonSerializer.Deserialize<List<string>>(
                traits, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // Blank entries would draw empty chips.
            List<string> words = [.. (parsed ?? []).Select(w => w.Trim()).Where(w => w.Length > 0)];

            // Nothing to draw: the model is told so rather than left believing the words are on screen.
            if (words.Count == 0)
                return "No words offered: the payload held none.";

            // This call's words replace any offered earlier in the turn.
            interviewState.PendingTraits.Clear();
            interviewState.PendingTraits.AddRange(words);

            // The model learns the text box stays open beside the words.
            return $"{words.Count} words will be drawn under your question, to be picked from freely. "
                   + "The text box stays open, so the answer may still come in the client's own words.";
        }
        catch (JsonException ex)
        {
            // A payload that is not a JSON array is reported with the parser's reason, so the model can resend it right.
            return $"No words offered: the payload is not a JSON array of words ({ex.Message}).";
        }
    }

    /// <summary>
    /// Records the agent's Personality section, overwriting it and reporting its shape as <see cref="AgentTargetTool.SetAgentTarget"/> does.
    /// This pass is the one place in the interview a voice can be written.
    /// </summary>
    [Description("Records the agent's PERSONALITY section: the empathy, language, tone and humanity it meets a conversation with. Voice only — a sentence telling the agent what to DO belongs in its instructions. Name which facet of Morgana this agent is; do not list adjectives.")]
    public string SetAgentPersonality(
        [Description("Two to three sentences, addressed to the agent as 'you'. No section label; that is added for you.")] string personality)
    {
        // The last call stands; the section's shape is two to three sentences of voice.
        interviewState.Agent.Personality = personality?.Trim();
        return Shaped("Personality", personality, 2, 3);
    }

    /// <inheritdoc />
    [Description("Returns everything wrong with this agent that can be decided without a model: a name that cannot be a C# identifier, an intent nothing routes to, a section left empty. Fix what it reports rather than explaining it away.")]
    public override string GetFindings() => base.GetFindings();

    /// <inheritdoc />
    [Description("Returns the complete prompt this agent's model will read, composed as Morgana composes it — her layer, the fences and yours beneath. Reading your own work composed is the only way to see whether your layer contradicts hers or merely repeats her.")]
    public override Task<string> GetComposedPrompt() => base.GetComposedPrompt();

    /// <inheritdoc />
    [Description("Shows the client, word for word, the prose you have just written for their agent. Call it on the turn that asks whether a section is right, before the question: what they are approving is those exact words, so those words stand on their own and your own sentence introduces them rather than containing them. 'I have written a fond and careful voice that follows…' is one stretch of prose in which nothing marks where you stop speaking and their agent's text begins. An approval given to that approves nothing in particular. Only what is actually written — never a paraphrase, never a summary of it.")]
    public override string ShowWhatIsWritten(
        [Description("The section's prose exactly as it now stands, with nothing added around it and no quotation marks of your own.")] string written)
        => base.ShowWhatIsWritten(written);

    /// <inheritdoc />
    [Description("Attaches to the question you are about to ask the one button that answers it without adding anything — agreement or that there is nothing to add — so a client in that position settles the turn with one press instead of a typed sentence. Call it BEFORE writing the question — the button renders below it. EVERY turn that states back what you have just written carries it, without exception: that turn's whole question is whether they agree. A client with nothing to object to should not have to type a sentence to say so. So does every question asking whether something exists — anything missing, anything the agent should be kept off, anything somebody else in their place takes instead — since 'no, nothing' is always an honest answer to it, however it is worded. Never a button carrying an answer about their work, where it would put your words in place of theirs. The text box stays open either way.")]
    public override string SetChoice(
        [Description("What the client reads on the button. It carries the answer that adds nothing and has to answer the question AS YOU PUT IT — 'That's everything' where you asked whether that is everything, 'Nothing missing' where you asked whether anything is missing, 'No, nothing like that' where you asked whether there is something the agent should never do.")] string label,
        [Description("The complete answer the button sends, in the client's register, reading as something they would plausibly have typed.")] string value)
        => base.SetChoice(label, value);

    /// <inheritdoc />
    [Description("Declares this pass settled. The declaration lives in this call and nowhere else: never write a marker, a token or a closing formula into your text to mean it.")]
    public override string SetPassCompleted() => base.SetPassCompleted();
}