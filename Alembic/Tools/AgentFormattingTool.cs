using System.ComponentModel;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Tools;

/// <summary>The tools of the pass that writes an agent's Formatting.</summary>
public sealed class AgentFormattingTool : InterviewTool
{
    /// <summary>Binds the toolset to one interview.</summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The checks decidable by reading the Draft.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    public AgentFormattingTool(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
        : base(interviewState, draftStateService, draftValidationService, recapService)
    {
    }

    /// <inheritdoc />
    protected override string NextStep => "the agent joins the domain and they can review or export it.";

    /// <inheritdoc />
    [Description("Returns the agent as it currently stands: the intent it answers, what it is for, where it stops, how it sounds and what it can reach. Each step runs with a fresh memory and only the configuration carries over, so this is the whole of what you know about it. On an agent already in the client's domain, all of it was written before today.")]
    public override string GetAgentSoFar() => base.GetAgentSoFar();

    /// <inheritdoc />
    [Description("Returns the agent's tools and their parameters. Call it before writing anything: both sections speak about this toolkit and an instruction naming a tool that does not exist cannot be followed.")]
    public override string GetToolkit() => base.GetToolkit();

    /// <summary>
    /// Records the agent's Formatting section, overwriting it and reporting its shape as <see cref="AgentTargetTool.SetAgentTarget"/> does.
    /// The upper bound is wide because this is where each tool's card shape is named exactly, which a vaguer sentence would leave the model to improvise.
    /// </summary>
    [Description("Records the agent's FORMATTING section: how it presents what its own tools return. The one thing no global policy can know for it — Morgana cannot know that an invoice leads with its due date or that this agent's own confirm-or-cancel deserves two buttons. Say nothing about markdown or length, those are hers; if a button or a card belongs here, name it exactly.")]
    public string SetAgentFormatting(
        [Description("Addressed to the agent as 'you', about this agent's own data: normally two to five sentences, longer only where naming an exact card shape per tool or an exact button payload takes the room it takes. No section label; that is added for you.")] string formatting)
    {
        // The last call stands; two to ten sentences, room for a card shape per tool.
        interviewState.Agent.Formatting = formatting?.Trim();
        return Shaped("Formatting", formatting, 2, 10);
    }

    /// <inheritdoc />
    [Description("Returns everything wrong with this agent that can be decided without a model. Fix what it reports rather than explaining it away.")]
    public override string GetFindings() => base.GetFindings();

    /// <inheritdoc />
    [Description("Returns the complete prompt this agent's model will read, composed as Morgana composes it — her layer, the fences and your four sections beneath. Reading your own work composed is the only way to see whether your layer contradicts hers or merely repeats her.")]
    public override Task<string> GetComposedPrompt() => base.GetComposedPrompt();

    /// <inheritdoc />
    [Description("Puts a worked example in the answer box under the question you are about to ask, greyed out, for them to take and cut about or to type over. Call it before every question that asks them for something new — the one that opens the step and every follow-up after it — so the box is never empty under an open question. Not on the confirming turn that carries the button: there the answer that adds nothing already stands on the screen.")]
    public override string SetExample(
        [Description("An answer somebody in THEIR trade might have given to the question you are about to ask, cut to the size of that question — forty to sixty words where it opens a step, ten to twenty where it follows one up — in their vocabulary and about the agent you are building — never about another business, never a description of what to write and never the question itself put into their mouth: an example that says what the question says adds nothing to the screen it stands on. It teaches the register, the length and the level of detail at once. Send it unquoted: it stands in the box as text they can send as it is, so quotation marks around it arrive as part of their own answer. Make it close enough to their work to be worth editing and specific enough that it cannot be agreed with as it stands: an example that fits them exactly is one they will press past without adding what only they know.")] string example)
        => base.SetExample(example);

    /// <inheritdoc />
    [Description("Attaches to the question you are about to ask the one button that answers it without adding anything — agreement or that there is nothing to add — so a client in that position settles the turn with one press instead of a typed sentence. Call it BEFORE writing the question — the button renders below it. In this pass it belongs to the turn that states an inference back and to no other. EVERY turn that states back what you have just written carries it, without exception: that turn's whole question is whether they agree. A client with nothing to object to should not have to type a sentence to say so.")]
    public override string SetChoice(
        [Description("What the client reads on the button. It carries the answer that adds nothing and has to answer the question AS YOU PUT IT — 'That's everything' where you asked whether that is everything, 'Nothing missing' where you asked whether anything is missing, 'No, nothing like that' where you asked whether there is something the agent should never do.")] string label,
        [Description("The complete answer the button sends, in the client's register, reading as something they would plausibly have typed.")] string value)
        => base.SetChoice(label, value);

    /// <inheritdoc />
    [Description("Declares this pass settled and with it the whole interview. The declaration lives in this call and nowhere else: never write a marker, a token or a closing formula into your text to mean it.")]
    public override string SetPassCompleted() => base.SetPassCompleted();
}
