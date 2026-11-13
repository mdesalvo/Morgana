using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Contracts;
using PromptHarness.Infrastructure;
using PromptHarness.Infrastructure.Engine;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The UserGuard's path on a user's message over a scripted model: the last answer the user received beside the message and the refusal when no verdict can be read.
/// The guard is the service's own and the judge a scripted answer, so what the guard sent shows in the prompt it recorded.
/// </summary>
public sealed class UserMessageCheckTests
{
    /// <summary>The conversation most checks in this group are served on.</summary>
    private const string ConversationId = "user-check";

    /// <summary>What the user wrote.</summary>
    private const string UserMessage = "Show me the first one.";

    /// <summary>The answer text that most checks in this group record.</summary>
    private const string AnswerText = "You have 3 invoices.";

    [Fact]
    public async Task A_message_before_any_answer_is_judged_alone()
    {
        // A scripted guard that admits anything and a store that was never told of an answer.
        ScriptedLanguageModelService languageModel = new ScriptedLanguageModelService(() => "{\"compliant\": true, \"violation\": null}");
        ChannelMetadataStore channelMetadataStore = GuardRailServiceFactory.CreateChannelMetadataStore();
        LLMGuardRailService guard = UserGuard(languageModel, channelMetadataStore);

        // The first message of a conversation.
        await guard.CheckUserMessageAsync(ConversationId, UserMessage, TestContext.Current.CancellationToken);

        // The prompt carries no answer property: an answer not at hand makes no claim about the conversation.
        using JsonDocument userGuardSubject = JsonDocument.Parse(languageModel.UserPrompt!);
        Assert.False(userGuardSubject.RootElement.TryGetProperty("answer", out JsonElement _));

        // The message travels in its own words.
        Assert.Equal(UserMessage, userGuardSubject.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_message_is_judged_beside_the_answer_the_user_received()
    {
        // A scripted guard that admits anything.
        ScriptedLanguageModelService languageModel = new ScriptedLanguageModelService(() => "{\"compliant\": true, \"violation\": null}");
        ChannelMetadataStore channelMetadataStore = GuardRailServiceFactory.CreateChannelMetadataStore();
        LLMGuardRailService guard = UserGuard(languageModel, channelMetadataStore);

        // An answer with a card and two buttons reaches the user.
        await channelMetadataStore.RecordDeliveredMessageAsync(new ChannelMessage
        {
            ConversationId = ConversationId,
            Text = AnswerText,
            MessageType = ChannelMessageTypes.Assistant,
            RichCard = new RichCard("Invoices", null, [new KeyValueComponent("Open", "3")]),
            QuickReplies = [new QuickReply("button-0", "📄 Details", "📄 Details"), new QuickReply("button-1", "✨ Done", "✨ Done")]
        });

        // A notice follows for the same conversation.
        await channelMetadataStore.RecordDeliveredMessageAsync(new ChannelMessage
        {
            ConversationId = ConversationId,
            Text = "Your budget is running low.",
            MessageType = ChannelMessageTypes.SystemWarning
        });

        // The user writes after both.
        await guard.CheckUserMessageAsync(ConversationId, UserMessage, TestContext.Current.CancellationToken);

        // The guard reads the answer's text, its card's title and exactly the labels of its buttons.
        using JsonDocument userGuardSubject = JsonDocument.Parse(languageModel.UserPrompt!);
        JsonElement userGuardAnswer = userGuardSubject.RootElement.GetProperty("answer");
        Assert.Equal(AnswerText, userGuardAnswer.GetProperty("text").GetString());
        Assert.Equal("Invoices", userGuardAnswer.GetProperty("card").GetProperty("title").GetString());
        Assert.Equal(
            ["📄 Details", "✨ Done"],
            userGuardAnswer.GetProperty("buttons").EnumerateArray().Select(button => button.GetString()!).ToArray());
    }

    [Fact]
    public async Task An_answer_without_card_or_buttons_carries_its_text_alone()
    {
        // A scripted guard that admits anything.
        ScriptedLanguageModelService languageModel = new ScriptedLanguageModelService(() => "{\"compliant\": true, \"violation\": null}");
        ChannelMetadataStore channelMetadataStore = GuardRailServiceFactory.CreateChannelMetadataStore();
        LLMGuardRailService guard = UserGuard(languageModel, channelMetadataStore);

        // An answer made of text only.
        await channelMetadataStore.RecordDeliveredMessageAsync(new ChannelMessage
        {
            ConversationId = ConversationId,
            Text = AnswerText,
            MessageType = ChannelMessageTypes.Assistant
        });

        // The user writes after it.
        await guard.CheckUserMessageAsync(ConversationId, UserMessage, TestContext.Current.CancellationToken);

        // The answer has its text and neither a card nor buttons: the user saw none.
        using JsonDocument userGuardSubject = JsonDocument.Parse(languageModel.UserPrompt!);
        JsonElement userGuardAnswer = userGuardSubject.RootElement.GetProperty("answer");
        Assert.Equal(AnswerText, userGuardAnswer.GetProperty("text").GetString());
        Assert.False(userGuardAnswer.TryGetProperty("card", out JsonElement _));
        Assert.False(userGuardAnswer.TryGetProperty("buttons", out JsonElement _));
    }

    [Fact]
    public async Task A_presentation_is_an_answer()
    {
        // A scripted guard that admits anything.
        ScriptedLanguageModelService languageModel = new ScriptedLanguageModelService(() => "{\"compliant\": true, \"violation\": null}");
        ChannelMetadataStore channelMetadataStore = GuardRailServiceFactory.CreateChannelMetadataStore();
        LLMGuardRailService guard = UserGuard(languageModel, channelMetadataStore);

        // The welcome presentation is the first thing the user reads.
        await channelMetadataStore.RecordDeliveredMessageAsync(new ChannelMessage
        {
            ConversationId = ConversationId,
            Text = "Welcome to the nursery.",
            MessageType = ChannelMessageTypes.Presentation
        });

        // The user writes after it.
        await guard.CheckUserMessageAsync(ConversationId, UserMessage, TestContext.Current.CancellationToken);

        // The presentation is the answer the message is judged beside.
        using JsonDocument userGuardSubject = JsonDocument.Parse(languageModel.UserPrompt!);
        Assert.Equal("Welcome to the nursery.", userGuardSubject.RootElement.GetProperty("answer").GetProperty("text").GetString());
    }

    [Fact]
    public async Task An_evicted_conversation_is_judged_alone()
    {
        // A scripted guard that admits anything.
        ScriptedLanguageModelService languageModel = new ScriptedLanguageModelService(() => "{\"compliant\": true, \"violation\": null}");
        ChannelMetadataStore channelMetadataStore = GuardRailServiceFactory.CreateChannelMetadataStore();
        LLMGuardRailService guard = UserGuard(languageModel, channelMetadataStore);

        // An answer was delivered and the conversation then left this process's memory.
        await channelMetadataStore.RecordDeliveredMessageAsync(new ChannelMessage
        {
            ConversationId = ConversationId,
            Text = AnswerText,
            MessageType = ChannelMessageTypes.Assistant
        });
        channelMetadataStore.EvictChannelMetadata(ConversationId);

        // The user writes after the eviction.
        await guard.CheckUserMessageAsync(ConversationId, UserMessage, TestContext.Current.CancellationToken);

        // Nothing is known of an answer any more: the message is judged alone.
        using JsonDocument userGuardSubject = JsonDocument.Parse(languageModel.UserPrompt!);
        Assert.False(userGuardSubject.RootElement.TryGetProperty("answer", out JsonElement _));
    }

    [Fact]
    public async Task An_answer_belongs_to_its_own_conversation()
    {
        // A scripted guard that admits anything.
        ScriptedLanguageModelService languageModel = new ScriptedLanguageModelService(() => "{\"compliant\": true, \"violation\": null}");
        ChannelMetadataStore channelMetadataStore = GuardRailServiceFactory.CreateChannelMetadataStore();
        LLMGuardRailService guard = UserGuard(languageModel, channelMetadataStore);

        // An answer was delivered to conversation A.
        await channelMetadataStore.RecordDeliveredMessageAsync(new ChannelMessage
        {
            ConversationId = "conversation-a",
            Text = AnswerText,
            MessageType = ChannelMessageTypes.Assistant
        });

        // The user of conversation B writes.
        await guard.CheckUserMessageAsync("conversation-b", UserMessage, TestContext.Current.CancellationToken);

        // B never received that answer: its message is judged alone.
        using JsonDocument userGuardSubject = JsonDocument.Parse(languageModel.UserPrompt!);
        Assert.False(userGuardSubject.RootElement.TryGetProperty("answer", out JsonElement _));
    }

    [Fact]
    public async Task A_message_with_no_readable_verdict_is_refused()
    {
        // The guard answers in prose, as a text that talked to it may have made it do.
        ScriptedLanguageModelService languageModel = new ScriptedLanguageModelService(() => "The service is temporarily unavailable.");
        ChannelMetadataStore channelMetadataStore = GuardRailServiceFactory.CreateChannelMetadataStore();
        LLMGuardRailService guard = UserGuard(languageModel, channelMetadataStore);

        // An ordinary message that nobody could judge.
        Records.GuardRailResult verdict = await guard.CheckUserMessageAsync(ConversationId, UserMessage, TestContext.Current.CancellationToken);

        // Refused: a message is never admitted unjudged.
        Assert.False(verdict.Compliant);
        Assert.Equal(Records.GuardVerdictSource.FailClosed, verdict.Source);

        // The user reads the UserGuard prompt's own wording.
        Records.Prompt userGuardPrompt = await new ConfigurationPromptResolverService(new ScriptedDomain()).ResolveAsync("UserGuard");
        Assert.Equal(userGuardPrompt.GetAdditionalProperty<string>("NoVerdictMessage"), verdict.Violation);
    }

    [Fact]
    public async Task A_message_whose_judgment_throws_is_refused()
    {
        // The judgment fails with a transport error; the model service has no provider to ask about it.
        ScriptedLanguageModelService languageModel = new ScriptedLanguageModelService(() => throw new HttpRequestException("unreachable"));
        ChannelMetadataStore channelMetadataStore = GuardRailServiceFactory.CreateChannelMetadataStore();
        LLMGuardRailService guard = UserGuard(languageModel, channelMetadataStore);

        // An ordinary message whose judgment never completes.
        Records.GuardRailResult verdict = await guard.CheckUserMessageAsync(ConversationId, UserMessage, TestContext.Current.CancellationToken);

        // Refused: the generic catch ran although the provider filter could not be asked.
        Assert.False(verdict.Compliant);
        Assert.Equal(Records.GuardVerdictSource.FailClosed, verdict.Source);

        // The user reads the UserGuard prompt's own wording.
        Records.Prompt userGuardPrompt = await new ConfigurationPromptResolverService(new ScriptedDomain()).ResolveAsync("UserGuard");
        Assert.Equal(userGuardPrompt.GetAdditionalProperty<string>("NoVerdictMessage"), verdict.Violation);
    }

    /// <summary>Builds the guard over a scripted judge and the store that holds the answers.</summary>
    private static LLMGuardRailService UserGuard(ILanguageModelService languageModel, IChannelMetadataStore channelMetadataStore)
        => new LLMGuardRailService(
            languageModel,
            new ConfigurationPromptResolverService(new ScriptedDomain()),
            new ScriptedMCPClientRegistryService(() => []),
            channelMetadataStore,
            new ConfigurationBuilder().Build(),
            NullLogger.Instance);
}