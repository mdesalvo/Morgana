using Morgana.AI;
using Morgana.AI.Services;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting that the morgana.json shipped inside Morgana.AI carries every entry the
/// framework fetches by name: the global policies in their reading order, the templates and the
/// placeholders each is filled through, the base tools, the error answers and the messages Morgana
/// says in her own voice.
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
    public async Task Morgana_prompt_binds_the_nine_policies_in_priority_order()
    {
        // The policies are rendered in Priority order, so the order asserted is the one an agent reads them in.
        Records.Prompt morgana = await resolver.ResolveAsync("Morgana");
        List<Records.GlobalPolicy> policies = morgana.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies");

        // Exact equality: a tenth policy or a missing one changes what every agent is bound by.
        Assert.Equal(
            [
                "ContextHandling", "QuickReplyDoctrine", "TurnEnding", "SessionContinuation", "ToolUsage",
                "ToolGrounding", "MandatoryTextualResponse", "RichCardUsage", "PeerConsultation"
            ],
            policies.OrderBy(policy => policy.Priority).Select(policy => policy.Name));
    }

    [Fact]
    public async Task Morgana_prompt_carries_every_template_the_framework_splices()
    {
        // The templates are fetched by name at their splice sites, so a lost one is an empty splice.
        Records.Prompt morgana = await resolver.ResolveAsync("Morgana");
        List<Records.Injection> injections = morgana.GetAdditionalProperty<List<Records.Injection>>("Injections");

        Assert.Equivalent(
            new[]
            {
                "ToolDescriptionContextGuidance", "HeldContextDeclaration", "ColleaguesDeclaration",
                "PeerConsultationDeclaration", "PeerConsultationGuardrail", "LandingDeclaration", "QuarantinedToolResult"
            },
            injections.Select(injection => injection.Name).ToArray());
    }

    [Theory]
    [InlineData("ToolDescriptionContextGuidance")]
    [InlineData("HeldContextDeclaration")]
    [InlineData("ColleaguesDeclaration")]
    [InlineData("PeerConsultationDeclaration")]
    [InlineData("PeerConsultationGuardrail")]
    [InlineData("LandingDeclaration")]
    [InlineData("QuarantinedToolResult")]
    public async Task Every_template_opens_with_its_bracketed_label(string templateName)
    {
        // The template's text as it will be spliced.
        string text = await ResolveTemplateAsync(templateName);

        // A template arrives spliced into somebody else's text, so where it begins has to be visible without being read.
        Assert.StartsWith("[", text, StringComparison.Ordinal);

        // The label closes on the first line: a bracket opened and never closed marks nothing.
        string firstLine = text.Split('\n')[0];
        Assert.Contains("]", firstLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ToolDescriptionContextGuidance", "((context_parameters))")]
    [InlineData("HeldContextDeclaration", "((held_variables))")]
    [InlineData("ColleaguesDeclaration", "((colleagues))")]
    [InlineData("PeerConsultationDeclaration", "((caller))")]
    [InlineData("PeerConsultationGuardrail", "((caller))")]
    [InlineData("PeerConsultationGuardrail", "((question))")]
    [InlineData("QuarantinedToolResult", "((tool))")]
    public async Task Every_template_keeps_the_placeholder_it_is_filled_through(string templateName, string placeholder)
    {
        // The template's text as it will be spliced.
        string text = await ResolveTemplateAsync(templateName);

        // The splice replaces this placeholder, so a text that lost it would carry the datum nowhere.
        Assert.Contains(placeholder, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Presentation", "Instructions", "((intents))")]
    [InlineData("Classifier", "Target", "((formattedIntents))")]
    [InlineData("ChannelAdapter", "Instructions", "((channel_capabilities))")]
    public async Task Framework_sections_keep_the_placeholder_they_are_filled_through(string promptId, string section, string placeholder)
    {
        // The prompt as the actor that owns it resolves it.
        Records.Prompt prompt = await resolver.ResolveAsync(promptId);

        // The section named by the case: the placeholder lives in exactly one of them.
        string text = section switch
        {
            "Target" => prompt.Target,
            "Instructions" => prompt.Instructions,
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "The case names a section that is not asserted")
        };

        // The actor fills this placeholder with the run's data, so a section that lost it would reach the model without that data.
        Assert.Contains(placeholder, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Morgana_prompt_declares_the_base_tools_of_every_agent()
    {
        // The base tools are the ones every agent receives ahead of its own.
        Records.Prompt morgana = await resolver.ResolveAsync("Morgana");
        List<Records.ToolDefinition> tools = morgana.GetAdditionalProperty<List<Records.ToolDefinition>>("Tools");

        // A tool lost here is a tool no agent in the installation can call.
        Assert.Equivalent(
            new[] { "GetContextVariable", "SetContextVariable", "SetTurnEnding", "SetQuickReplies", "SetRichCard" },
            tools.Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public async Task Morgana_prompt_authors_the_error_answers_the_framework_falls_back_on()
    {
        // The answers a failed turn is told in, authored rather than hard-coded.
        Records.Prompt morgana = await resolver.ResolveAsync("Morgana");
        List<Records.ErrorAnswer> errorAnswers = morgana.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");

        // Both are looked up by name when a turn fails: each must say something to the user.
        foreach (string errorName in new[] { "GenericError", "LLMServiceError" })
            Assert.False(
                string.IsNullOrWhiteSpace(errorAnswers.SingleOrDefault(errorAnswer => errorAnswer.Name == errorName)?.Content),
                $"Morgana carries no content for the error answer {errorName}");
    }

    [Theory]
    [InlineData("Morgana", "AgentExitMessage")]
    [InlineData("Presentation", "FallbackMessage")]
    [InlineData("Presentation", "NoAgentsMessage")]
    [InlineData("Classifier", "ClassificationError")]
    [InlineData("Classifier", "DisambiguationMessage")]
    [InlineData("Classifier", "ComplementIntentDescription")]
    public async Task Every_message_the_framework_reads_by_name_is_authored(string promptId, string propertyName)
    {
        // The prompt that owns the message.
        Records.Prompt prompt = await resolver.ResolveAsync(promptId);

        // A missing property throws and a blank one would show the user an empty bubble: both fail here.
        Assert.False(string.IsNullOrWhiteSpace(prompt.GetAdditionalProperty<string>(propertyName)), $"{promptId}.{propertyName} is blank");
    }

    [Fact]
    public async Task Morgana_s_closing_line_names_the_departing_agent()
    {
        // The line Morgana says when an agent hands the conversation back.
        Records.Prompt morgana = await resolver.ResolveAsync("Morgana");
        string closingLine = morgana.GetAdditionalProperty<string>("AgentExitMessage");

        // The supervisor formats the departing agent's display name into this slot.
        Assert.Contains("{0}", closingLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Morgana")]
    [InlineData("Classifier")]
    [InlineData("Guard")]
    [InlineData("Presentation")]
    [InlineData("ChannelAdapter")]
    public async Task Sections_carry_no_label_of_their_own(string promptId)
    {
        Records.Prompt prompt = await resolver.ResolveAsync(promptId);

        // The label is put in front by whoever composes the prompt: one written here would be one more
        // place to forget it, which is exactly what composing it in code exists to rule out.
        foreach (string? section in new[] { prompt.Target, prompt.Personality, prompt.Instructions, prompt.Formatting })
            Assert.False(section?.TrimStart().StartsWith('[') == true, $"{promptId}: {section}");
    }

    [Fact]
    public void A_section_is_labeled_once_whether_or_not_it_was_authored_with_its_label()
    {
        // A section authored bare receives the label.
        Assert.Equal("[TARGET] Answer billing questions.", Records.Prompt.Labeled("[TARGET]", "Answer billing questions."));

        // A section authored with the label already is not given a second one.
        Assert.Equal("[TARGET] Answer billing questions.", Records.Prompt.Labeled("[TARGET]", "[TARGET] Answer billing questions."));

        // A section that says nothing yields nothing: no label stands over an empty body.
        Assert.Equal("", Records.Prompt.Labeled("[TARGET]", "  "));
    }

    [Fact]
    public async Task A_section_that_says_nothing_leaves_no_label_in_the_composed_prompt()
    {
        ConfigurationPromptComposerService composer = new ConfigurationPromptComposerService(resolver);

        string withFormatting = await composer.ComposeAgentInstructionsAsync(
            new Records.Prompt("billing", "Target.", "Instructions.", "Plain text.", null, null, "en-US", "1", []));
        string withoutFormatting = await composer.ComposeAgentInstructionsAsync(
            new Records.Prompt("billing", "Target.", "Instructions.", "", null, null, "en-US", "1", []));

        // The framework layer keeps its own section, so the domain's absence is exactly one label fewer.
        Assert.Equal(withFormatting.Split("[FORMATTING]").Length - 2, withoutFormatting.Split("[FORMATTING]").Length - 1);
    }

    /// <summary>Reads one of Morgana's templates by name; fails naming it when it is not declared exactly once.</summary>
    /// <param name="templateName">The template's name as the framework fetches it.</param>
    private async Task<string> ResolveTemplateAsync(string templateName)
    {
        // Morgana owns every template the framework splices.
        Records.Prompt morgana = await resolver.ResolveAsync("Morgana");
        List<Records.Injection> injections = morgana.GetAdditionalProperty<List<Records.Injection>>("Injections");

        // Single throws when the name is absent or doubled, which is the failure this group exists to surface.
        return injections.Single(injection => injection.Name == templateName).Description;
    }
}
