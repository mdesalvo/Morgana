using Distiller.Model;
using Microsoft.Extensions.DependencyInjection;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// A correction that reaches past its own section, and whether the walk that reopened the agent
/// actually closes it.
/// </summary>
/// <remarks>
/// The defect this class exists for came off a live run against the greenhouse Inventory agent: a
/// client added a capability to its <c>Target</c> — issue a coupon once an order closes over a
/// threshold — and the toolkit, which the correction of the day never reached, stayed exactly as it
/// was, with nothing in it that issues anything. The domain was left promising something it cannot
/// do, which is the most expensive prose defect there is, because the person who meets it is a
/// customer and the agent's answer to them is invention.
/// <para>
/// Composing cannot produce it: the toolkit is settled after the Target and against it, so a
/// capability without a tool never outlives the step that would have given it one. Editing used to be
/// able to, because a correction opened and settled one section and stopped. It no longer can: an
/// edit re-enters at the Target and walks every section after it exactly as a fresh compose does, so
/// the same turn that changes what the agent promises is followed, in the same sitting, by the pass
/// that has the tool to back it.
/// </para>
/// </remarks>
[Collection(AddedCapabilityCollection.Name)]
[Trait("Stage", "Editing")]
public sealed class CrossSectionEditTests
{
    private readonly AddedCapabilityFixture added;

    public CrossSectionEditTests(AddedCapabilityFixture added) => this.added = added;

    private Judge Judge => added.Services.GetRequiredService<Judge>();

    [Fact]
    public void The_toolkit_grows_to_cover_the_added_capability()
    {
        // The walk that reopens an edited agent passes through its Toolkit itself, so a capability
        // added at the Target is not merely named as a gap for the client to close later — it is
        // closed in the same sitting, with the same DeclareTool a fresh compose would use.
        Assert.True(added.ToolsAfter.Count > added.ToolsBefore.Count,
            "A capability was added that no tool of this agent backed, and the walk that reopened "
            + $"it reached the Toolkit without adding one:\n\n{added.Transcript}");
    }

    [Fact]
    public async Task The_new_tool_serves_the_added_capability()
    {
        JudgeVerdict verdict = await Judge.EvaluateTurnAsync(
            "Somewhere in what was said, a tool is declared or described that issues a coupon (or "
            + "store credit) to a customer once a qualifying order closes — the capability added at "
            + "the Target. Merely repeating the capability back, or confirming the agent now handles "
            + "it without describing a tool for it, does not count.",
            added.EverythingSaid,
            TestContext.Current.CancellationToken);

        Assert.True(verdict.Holds,
            $"A tool should have been declared to back the added coupon capability: {verdict.Reason}\n\n{added.Transcript}");
    }
}
