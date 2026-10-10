using Morgana.AI;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting that the morgana.json shipped inside Morgana.AI carries every entry the
/// framework fetches by name: the sections an agent's prompt is composed of, the templates, the tool
/// results, the framework replies and the messages Morgana says in her own voice.
/// </summary>
/// <remarks>
/// <para>Deterministic and free. The framework reads these entries by name at the moment it needs them,
/// so one lost to a bad edit fails a conversation in mid-turn, long after a host that boots cleanly
/// passed its health probe. Here it fails at once, naming the entry.</para>
///
/// <para>Every name is spelled out rather than read from <c>Constants</c>, as the wire-contract groups
/// do: the point is to notice the configuration losing an entry, not a constant equal to itself.</para>
/// </remarks>
public sealed class FrameworkPromptTests
{
    /// <summary>The framework prompts, read through the resolver from the embedded morgana.json.</summary>
    private readonly ConfigurationPromptResolverService resolver = new ConfigurationPromptResolverService(new NoDomain());

    [Fact]
    public async Task FrameworkPrompt_Morgana_prompt_carries_every_section_an_agent_is_composed_of()
    {
        Records.Prompt morgana = await resolver.ResolveAsync("Morgana");

        Assert.NotEmpty(morgana.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies"));
        Assert.DoesNotContain(morgana.AdditionalProperties, properties => properties.ContainsKey(Constants.PromptProperties.Tools));

        string[] promptInjections = [.. morgana.GetAdditionalProperty<List<Records.Injection>>("PromptInjections").Select(injection => injection.Name)];
        Assert.Equivalent(new[]
        {
            "ColleaguesDeclaration", "PeerConsultationDeclaration", "PeerConsultationGuardrail", "TurnClosureRequest"
        }, promptInjections);
    }

    [Fact]
    public async Task FrameworkPrompt_Morgana_prompt_words_every_text_the_model_reads_as_part_of_a_tool()
    {
        string[] toolInjections = [.. (await resolver.ResolveAsync("Morgana"))
            .GetAdditionalProperty<List<Records.Injection>>("ToolInjections").Select(injection => injection.Name)];

        Assert.Equivalent(new[]
        {
            "TurnClosed", "ContextValueMissing", "ReplyWithoutText", "CardTooDeep", "CardTooLarge", "ConsultationChained",
            "ConsultationRoundsExhausted", "ColleagueCouldNotAnswer", "PeerAtCapacity", "PeerOutOfBudget", "PeerTimedOut", "PeerFailed",
            "WorkflowStarted", "ToolNotAtThisStep", "StepActionsRequired",
            "ReplyNotAccepted", "EarlierToolResult", "ExecutionApprovalGuidance", "WorkflowStepReached", "WorkflowEnded"
        }, toolInjections);
    }

    [Fact]
    public async Task FrameworkPrompt_Morgana_prompt_authors_the_framework_replies_by_the_ids_the_channels_act_on()
    {
        List<Records.FrameworkReplySet> sets = (await resolver.ResolveAsync("Morgana"))
            .GetAdditionalProperty<List<Records.FrameworkReplySet>>(Constants.PromptProperties.FrameworkReplies);
        Assert.Equal(
            [Constants.FrameworkReplySets.Closure, Constants.FrameworkReplySets.Escape, Constants.FrameworkReplySets.Approval],
            sets.Select(set => set.Name));
        Records.FrameworkReplies buttons = Records.FrameworkReplies.From(sets);

        Assert.Equal(["continue_agent", "exit_agent"], buttons.Closure.Select(button => button.Id));
        Assert.Equal(["continue_agent", "exit_agent"], buttons.Escape.Select(button => button.Id));
        Assert.Equal(["approve_action", "reject_action"], buttons.Approval!.Select(button => button.Id));
        Assert.True(buttons.Closure[1].Termination);
    }

    [Theory]
    [InlineData("Morgana", "AgentExit")]
    [InlineData("Morgana", "Approval")]
    [InlineData("Morgana", "GenericError")]
    [InlineData("Morgana", "Timeout")]
    [InlineData("Presentation", "Fallback")]
    [InlineData("Presentation", "NoAgents")]
    [InlineData("Classifier", "Disambiguation")]
    [InlineData("Classifier", "UnrecognizedIntent")]
    [InlineData("Guard", "ContentFiltered")]
    public async Task FrameworkPrompt_Every_message_morgana_says_in_her_own_voice_is_authored(string promptId, string message)
        => Assert.False(string.IsNullOrWhiteSpace((await resolver.ResolveAsync(promptId)).GetMessage(message)));

    [Theory]
    [InlineData("Morgana")]
    [InlineData("Classifier")]
    [InlineData("Guard")]
    [InlineData("Presentation")]
    [InlineData("ChannelAdapter")]
    public async Task FrameworkPrompt_Sections_carry_no_label_of_their_own(string promptId)
    {
        Records.Prompt prompt = await resolver.ResolveAsync(promptId);

        // The label is put in front by whoever composes the prompt: one written here would be one more
        // place to forget it, which is exactly what composing it in code exists to rule out.
        foreach (string? section in new[] { prompt.Target, prompt.Personality, prompt.Instructions, prompt.Formatting })
            Assert.False(section?.TrimStart().StartsWith('[') == true, $"{promptId}: {section}");
    }

    [Fact]
    public void FrameworkPrompt_A_section_is_labeled_once_whether_or_not_it_was_authored_with_its_label()
    {
        Assert.Equal("[TARGET] Answer billing questions.", Records.Prompt.Labeled("[TARGET]", "Answer billing questions."));
        Assert.Equal("[TARGET] Answer billing questions.", Records.Prompt.Labeled("[TARGET]", "[TARGET] Answer billing questions."));
        Assert.Equal("", Records.Prompt.Labeled("[TARGET]", "  "));
    }

    /// <summary>A deployment with no domain: only the framework prompts are resolved.</summary>
    private sealed class NoDomain : IAgentConfigurationService
    {
        public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

        public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
    }
}
