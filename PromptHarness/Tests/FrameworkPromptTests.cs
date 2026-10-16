using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Contracts;
using PromptHarness.Infrastructure;
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
    /// <summary>The framework prompts, read through the resolver from the embedded morgana.json over a domain with no agents.</summary>
    private readonly ConfigurationPromptResolverService resolver = new ConfigurationPromptResolverService(new ScriptedDomain());

    [Fact]
    public async Task FrameworkPrompt_Morgana_prompt_carries_every_section_an_agent_is_composed_of()
    {
        Records.Prompt morgana = await resolver.ResolveAsync("Morgana");

        Assert.NotEmpty(morgana.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies"));
        Assert.DoesNotContain(morgana.AdditionalProperties, properties => properties.ContainsKey(Constants.PromptProperties.Tools));

        string[] promptInjections = [.. morgana.GetAdditionalProperty<List<Records.Injection>>("PromptInjections").Select(injection => injection.Name)];
        Assert.Equivalent(new[]
        {
            "ColleaguesDeclaration", "PeerConsultationDeclaration", "PeerConsultationGuardrail", "PeerConsultationQuestion", "TurnClosureRequest", "LandingDeclaration"
        }, promptInjections);
    }

    [Fact]
    public async Task FrameworkPrompt_Morgana_prompt_words_every_text_the_model_reads_as_part_of_a_tool()
    {
        string[] toolInjections = [.. (await resolver.ResolveAsync("Morgana"))
            .GetAdditionalProperty<List<Records.Injection>>("ToolInjections").Select(injection => injection.Name)];

        Assert.Equivalent(new[]
        {
            "TurnClosed", "ContextValueMissing", "ColleagueCouldNotAnswer", "CallNotAvailable",
            "ReplyNotAccepted", "EarlierToolResult", "ReturnContract", "ExecutionApprovalGuidance", "WorkflowStepReached", "WorkflowEnded"
        }, toolInjections);
    }

    [Fact]
    public async Task FrameworkPrompt_Landing_declaration_names_the_actions_of_the_agent_s_menu()
    {
        List<Records.Injection> promptInjections = (await resolver.ResolveAsync("Morgana"))
            .GetAdditionalProperty<List<Records.Injection>>("PromptInjections");

        // The labels are spliced into this placeholder, so a text that lost it would introduce an agent with an empty menu.
        Assert.Contains("((actions))", promptInjections.Single(injection => injection.Name == "LandingDeclaration").Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrameworkPrompt_Consultation_question_is_fenced_around_its_placeholder()
    {
        List<Records.Injection> promptInjections = (await resolver.ResolveAsync("Morgana"))
            .GetAdditionalProperty<List<Records.Injection>>("PromptInjections");
        string fence = promptInjections.Single(injection => injection.Name == "PeerConsultationQuestion").Content;
        string guardrail = promptInjections.Single(injection => injection.Name == "PeerConsultationGuardrail").Content;

        // The question is spliced between markers naming who asked, so a fence that lost either placeholder would mark nothing.
        Assert.Contains("((question))", fence, StringComparison.Ordinal);
        Assert.Contains("((caller))", fence, StringComparison.Ordinal);

        // The rules travel in the run's instructions: a question placeholder there would put somebody else's words among them.
        Assert.DoesNotContain("((question))", guardrail, StringComparison.Ordinal);
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

    [Fact]
    public async Task FrameworkPrompt_ChannelAdapter_prompt_authors_one_sentence_per_thing_a_channel_cannot_show()
    {
        string[] constraints = [.. (await resolver.ResolveAsync("ChannelAdapter"))
            .GetAdditionalProperty<List<Records.Injection>>("PromptInjections").Select(injection => injection.Name)];

        Assert.Equal(["NoCards", "NoButtons", "NoMarkdown", "MaxLength"], constraints);
    }

    [Theory]
    [InlineData(false, false, false, 200, "This channel shows no cards. This channel shows no buttons. This channel renders no markdown. Its messages hold at most 200 characters.")]
    [InlineData(true, false, true, null, "This channel shows no buttons.")]
    [InlineData(true, true, true, 1000, "Its messages hold at most 1000 characters.")]
    [InlineData(true, false, true, 0, "This channel shows no buttons.")]
    public async Task FrameworkPrompt_ChannelAdapter_tells_the_model_the_facts_of_the_channel_in_plain_sentences(
        bool supportsRichCards, bool supportsQuickReplies, bool supportsMarkdown, int? maxMessageLength, string expectedConstraints)
    {
        CapturingModel model = new CapturingModel();
        MorganaChannelAdapter adapter = new MorganaChannelAdapter(model, resolver, NullLogger.Instance);

        // Buttons on a message longer than the limit make every capability set above fall short, so the model is always asked.
        ChannelMessage message = new ChannelMessage
        {
            ConversationId = "conversation-1",
            Text = new string('a', 1500),
            QuickReplies = [new QuickReply("id", "Label", "value")]
        };
        await adapter.AdaptAsync(message, new ChannelCapabilities(supportsRichCards, supportsQuickReplies, false, supportsMarkdown, maxMessageLength));

        // The sentences stand where the prompt reserved them, with no capability object and no placeholder left.
        Assert.Contains($"its card and its buttons. {expectedConstraints}\n", model.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("((", model.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrameworkPrompt_Classifier_holds_the_model_to_the_intents_of_the_domain_and_the_complement()
    {
        CapturingModel model = new CapturingModel();
        LLMClassifierService classifier = new LLMClassifierService(
            model,
            resolver,
            new IntentsDomain(
                new Records.IntentDefinition("billing", "Invoices.", "Invoices", "My invoices"),
                new Records.IntentDefinition("contract", "Plans.", "Plans", "My plan")),
            new ConfigurationBuilder().Build(),
            NullLogger.Instance);

        await classifier.ClassifyAsync("conversation-1", "hello");

        JsonElement intentSchema = model.ResponseSchema!.Value
            .GetProperty("properties").GetProperty("intents").GetProperty("items").GetProperty("properties").GetProperty("intent");
        Assert.Equal(["billing", "contract", "other"], intentSchema.GetProperty("enum").EnumerateArray().Select(intent => intent.GetString()));
    }

    [Fact]
    public async Task FrameworkPrompt_A_section_that_says_nothing_leaves_no_label_in_the_composed_prompt()
    {
        ConfigurationPromptComposerService composer = new ConfigurationPromptComposerService(resolver);

        string withFormatting = await composer.ComposeAgentInstructionsAsync(new Records.Prompt("billing", "Target.", "Instructions.", "Plain text.", null, null, "en-US", "1"));
        string withoutFormatting = await composer.ComposeAgentInstructionsAsync(new Records.Prompt("billing", "Target.", "Instructions.", "", null, null, "en-US", "1"));

        // The framework layer keeps its own section, so the domain's absence is exactly one label fewer.
        Assert.Equal(withFormatting.Split("[FORMATTING]").Length - 2, withoutFormatting.Split("[FORMATTING]").Length - 1);
    }

    /// <summary>The framework tier's model as the actors meet it: it answers nothing and keeps what it was asked.</summary>
    private sealed class CapturingModel : ILLMService
    {
        /// <summary>The system prompt of the last call.</summary>
        public string SystemPrompt { get; private set; } = string.Empty;

        /// <summary>The schema that the last call narrowed the answer to; null when it was left to the type.</summary>
        public JsonElement? ResponseSchema { get; private set; }

        /// <inheritdoc />
        public Records.LLMTier FrameworkTier => Records.LLMTier.Economy;

        /// <inheritdoc />
        public Task<T?> CompleteStructuredAsync<T>(string conversationId, string systemPrompt, string userPrompt, JsonElement? responseSchema = null)
        {
            SystemPrompt = systemPrompt;
            ResponseSchema = responseSchema;
            return Task.FromResult<T?>(default);
        }

        /// <inheritdoc />
        public IChatClient GetChatClient(Records.LLMTier tier) => throw new InvalidOperationException("The actors ask for answers, never a chat client");

        /// <inheritdoc />
        public Records.MagicDustPricing GetPricing(Records.LLMTier tier) => new();
    }

    /// <summary>A domain declaring the given intents and no agent prompt.</summary>
    private sealed class IntentsDomain(params Records.IntentDefinition[] intents) : IAgentConfigurationService
    {
        /// <inheritdoc />
        public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>(intents));

        /// <inheritdoc />
        public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
    }
}
