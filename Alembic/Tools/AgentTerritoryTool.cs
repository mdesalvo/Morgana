using System.ComponentModel;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;
using Morgana.AI.Attributes;

namespace Alembic.Tools;

/// <summary>The tools of the pass that writes what a colleague reads before consulting an agent.</summary>
public sealed class AgentTerritoryTool : InterviewTool
{
    /// <summary>Binds the toolset to one interview.</summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The checks decidable by reading the Draft.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    public AgentTerritoryTool(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
        : base(interviewState, draftStateService, draftValidationService, recapService)
    {
    }

    /// <inheritdoc />
    protected override string NextStep => "the agent's own instructions and the way it presents what its tools return.";

    /// <inheritdoc />
    [Description("Returns the agent as it currently stands: the intent it answers, what it is for, where it stops, how it sounds and what it can reach. Each step runs with a fresh memory and only the configuration carries over, so this is the whole of what you know about it. On an agent already in the client's domain, all of it was written before today.")]
    public override string GetAgentSoFar() => base.GetAgentSoFar();

    /// <inheritdoc />
    [Description("Returns the toolkit as it stands, tools and parameters together. Call it before completing the pass.")]
    public override string GetToolkit() => base.GetToolkit();

    /// <inheritdoc />
    [Description("Returns the name and description of every intent already in the domain. Call it before settling an intent description: two overlapping descriptions produce a collision the classifier cannot resolve.")]
    public override string GetExistingIntents() => base.GetExistingIntents();

    /// <summary>
    /// Records what a colleague reads before consulting this agent, overwriting it and reporting its shape as <see cref="AgentTargetTool.SetAgentTarget"/> does.
    /// It names a territory and never an inventory, since a caller handed an inventory rules questions out instead of asking them.
    /// </summary>
    [Description("Records what a colleague reads before consulting this agent: the scope of its agent, addressed to another agent instead of to itself. A territory, in one or two sentences — what falls here — never a list of what the agent can do, which a caller reads as grounds to rule its question out. Never asked of the client and never shown to them: you write it from the TARGET you have just settled.")]
    public string SetAgentTerritory(
        [Description("The statement, without its section marker.")] [ToolParameter(Records.ToolScope.Request)] string territory)
    {
        // The last call stands; a territory is short by nature, one to three sentences.
        interviewState.Agent.Territory = territory?.Trim();
        return Shaped("Territory", territory, 1, 3);
    }

    /// <summary>
    /// Rewrites the routing description of the intent in hand in the sharper words the territory settled.
    /// Only this entry's own: every other description is settled and reading them back keeps this one distinct.
    /// </summary>
    [Description("Rewrites what this entry routes on, in the sharper words the territory settled. The classifier reads it against every other entry's description, so read those back first and leave this one distinct from all of them. Only this entry: no other intent of the domain is yours.")]
    public string SetIntentDescription(
        [Description("What lands on this agent, said for the classifier: the subject and the kinds of request that belong to it, in the client's own words. Never the agent's boundaries, its tools or its voice.")] string description)
    {
        // The sentence as the model wrote it, without the spaces a payload carries at its ends.
        string descriptionText = (description ?? string.Empty).Trim();

        // An empty description would leave the intent nothing to route on, so the old one stays.
        if (descriptionText.Length == 0)
            return "Nothing changed: an intent with no description is one the classifier cannot route to.";

        // The map's entry is the one the domain receives, so the classifier reads the new sentence.
        interviewState.Intent.Description = descriptionText;

        // The model reads back what now routes here.
        return $"'{interviewState.Intent.Name}' now routes on: {descriptionText}";
    }

    /// <summary>
    /// Returns the card this agent will present to whoever might consult it.
    /// A colleague reads its description and nothing else, so an inventory or a recycled routing phrase is visible here and nowhere else.
    /// </summary>
    [Description("Returns the card this agent will present to anyone who might consult it: what it answers for and the skills it advertises. Call it once the territory is written — a colleague weighing a question reads that description and nothing else, so this is where a sentence that reads as a list of functions is visible.")]
    public string GetAgentCard()
    {
        // Projected as Morgana projects the card she publishes, so what the model weighs is what a caller will be handed.
        return CardProjection.Render(interviewState.Intent, interviewState.Agent);
    }

    /// <inheritdoc />
    [Description("Returns everything wrong with this agent that can be decided without a model: a name that cannot be a C# identifier, an intent nothing routes to, a section left empty. Fix what it reports rather than explaining it away.")]
    public override string GetFindings() => base.GetFindings();

    /// <inheritdoc />
    [Description("Shows the client, word for word, the prose you have just written for their agent. Call it on the turn that asks whether a section is right, before the question: what they are approving is those exact words, so those words stand on their own and your own sentence introduces them rather than containing them. 'I have written a fond and careful voice that follows…' is one stretch of prose in which nothing marks where you stop speaking and their agent's text begins. An approval given to that approves nothing in particular. Only what is actually written — never a paraphrase, never a summary of it.")]
    public override string ShowWhatIsWritten(
        [Description("The section's prose exactly as it now stands, with nothing added around it and no quotation marks of your own.")] string written)
        => base.ShowWhatIsWritten(written);

    /// <inheritdoc />
    [Description("Puts a worked example in the answer box under the question you are about to ask, greyed out, for them to take and cut about or to type over. Call it before every question that asks them for something new — the one that opens the step and every follow-up after it — so the box is never empty under an open question. Not on the confirming turn that carries the button: there the answer that adds nothing already stands on the screen.")]
    public override string SetExample(
        [Description("An answer somebody in THEIR trade might have given to the question you are about to ask, cut to the size of that question — forty to sixty words where it opens a step, ten to twenty where it follows one up — in their vocabulary and about the agent you are building — never about another business, never a description of what to write and never the question itself put into their mouth: an example that says what the question says adds nothing to the screen it stands on. It teaches the register, the length and the level of detail at once. Send it unquoted: it stands in the box as text they can send as it is, so quotation marks around it arrive as part of their own answer. Make it close enough to their work to be worth editing and specific enough that it cannot be agreed with as it stands: an example that fits them exactly is one they will press past without adding what only they know.")] string example)
        => base.SetExample(example);

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