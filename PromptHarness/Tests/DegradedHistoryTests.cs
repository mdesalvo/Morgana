using Morgana.Contracts;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// What a channel too poor for rich answers reads back when it returns to a conversation: every answer in the
/// words it was delivered in, never the rich original it could not show.
/// </summary>
/// <remarks>
/// <para>One real turn under Rune's profile, then the history read over REST as a returning channel reads it.
/// The oracle is the delivery itself: the answer the channel was pushed is compared with the answer the
/// history gives back, so nothing here depends on how the model phrased it. No judge, no threshold, no knob.</para>
///
/// <para>The record keeps every answer as its author wrote it, card and buttons included, whatever channel it
/// was delivered to. A history handing that record to a poor channel as it stands loses the card and the
/// buttons without a trace, since the channel draws text alone: an answer then speaks of figures nobody sees.</para>
/// </remarks>
public sealed class DegradedHistoryTests
{
    /// <summary>
    /// The phrase <c>channeladapter-degrades-invoice-card</c> already pins down: BillingAgent answers it with a card
    /// and buttons, neither of which the profile can show, so its delivery is always degraded.
    /// </summary>
    private const string InvoiceRequest = "Hi, my customer code is P994E — show me my last 3 invoices";

    /// <summary>
    /// Where the words of a degraded delivery are kept. Spelled out rather than read from the host: a table
    /// renamed silently must be noticed here rather than asserted against itself.
    /// </summary>
    private const string DegradedMessageTable = "degraded_message";

    /// <summary>
    /// The row BillingAgent keeps its session in, which holds the rich original of every answer it gives. Spelled
    /// out for the same reason as <see cref="DegradedMessageTable"/>.
    /// </summary>
    private const string BillingRow = "billing";

    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Reads and rewrites the record directly, which no endpoint does.</summary>
    private readonly ChannelApiClient api;

    /// <summary>
    /// The one conversation the whole group reads, driven on first use. Each test examines it from a different
    /// angle, so a conversation per test would spend a second set of live turns and buy nothing.
    /// </summary>
    private static Task<DegradedConversation>? driven;

    /// <summary>Guards the single drive against xUnit running the tests of this class in parallel.</summary>
    private static readonly Lock DriveGate = new Lock();

    public DegradedHistoryTests(MorganaHostFixture fixture)
    {
        this.fixture = fixture;
        api = new ChannelApiClient(fixture);
    }

    [Fact]
    public async Task Answer_reads_back_in_the_words_it_was_delivered_in()
    {
        DegradedConversation conversation = await ConversationAsync();

        // An answer that fitted the profile as written was never degraded and would prove nothing below
        Assert.True(conversation.KeptDegradedMessages >= 1,
            $"The answer was delivered without being degraded, so nothing was kept to read back: {conversation.Delivered.Text}");

        MorganaChatMessage answer = AnswerIn(conversation.History, conversation.Delivered);

        // Word for word: degrading the record again, by a model or by a rule, would never land on the same text
        Assert.True(answer.Text == conversation.Delivered.Text,
            $"The history gives back other words than the delivery.{Environment.NewLine}Delivered: {conversation.Delivered.Text}{Environment.NewLine}History: {answer.Text}");
        Assert.True(answer.RichCard is null, $"The history hands a card to a channel that cannot draw one: {answer.RichCard?.Title}");
        Assert.True(answer.QuickReplies is not { Count: > 0 }, "The history hands buttons to a channel that cannot draw any.");
    }

    [Fact]
    public async Task Degraded_message_is_filed_under_the_row_of_its_rich_original()
    {
        DegradedConversation conversation = await ConversationAsync();

        // The rich original lives in the answering agent's row: the degraded words name that same row, so either is
        // found from the other by reading the record, never by guessing from a date alone
        Assert.True(conversation.KeptUnderRow == BillingRow,
            $"The degraded answer is filed under '{conversation.KeptUnderRow}' instead of the row of the agent that wrote it, '{BillingRow}'.");
        Assert.Equal(1L, await api.QueryRecordAsync(conversation.ConversationId, $"SELECT COUNT(*) FROM morgana WHERE agent_name = '{BillingRow}';"));
    }

    [Fact]
    public async Task Nothing_in_the_history_exceeds_what_the_channel_shows()
    {
        DegradedConversation conversation = await ConversationAsync();

        // The greeting included: every line a returning channel draws must be one it can draw
        foreach (MorganaChatMessage message in conversation.History.Where(message => message.Type != ChatMessageType.User))
            AssertFitsTheProfile(message);
    }

    [Fact]
    public async Task Answer_whose_delivered_words_were_not_kept_is_degraded_by_rule()
    {
        DegradedConversation conversation = await ConversationAsync();

        // The record as an answer delivered before its words could be kept leaves it, which the other tests of
        // this class no longer read: they hold the history as it was read before this line
        await api.QueryRecordAsync(conversation.ConversationId, $"DELETE FROM {DegradedMessageTable};");
        IReadOnlyList<MorganaChatMessage> history = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);

        AssertFitsTheProfile(AnswerIn(history, conversation.Delivered));
    }

    /// <summary>Fails unless the message is one the degraded profile can draw exactly as it is.</summary>
    private static void AssertFitsTheProfile(MorganaChatMessage message)
    {
        int maxMessageLength = HarnessChannel.DegradedCapabilities.MaxMessageLength!.Value;

        Assert.True(message.RichCard is null, $"A card reaches a channel that cannot draw one: {message.RichCard?.Title}");
        Assert.True(message.QuickReplies is not { Count: > 0 }, $"Buttons reach a channel that cannot draw any: {message.Text}");
        Assert.True(!string.IsNullOrWhiteSpace(message.Text), "An answer reaches the channel with nothing to show.");
        Assert.True(message.Text.Length <= maxMessageLength,
            $"An answer of {message.Text.Length} characters reaches a channel showing {maxMessageLength}: {message.Text}");
        Assert.True(!ExpectationChecker.ContainsMarkdownSyntax(message.Text),
            $"An answer reaches a channel without markdown still carrying its syntax: {message.Text}");
    }

    /// <summary>The history's line for the delivered answer, found by the instant both are dated with.</summary>
    private static MorganaChatMessage AnswerIn(IReadOnlyList<MorganaChatMessage> history, ChannelMessage delivered)
    {
        MorganaChatMessage? answer = history.SingleOrDefault(message => message.Timestamp == delivered.Timestamp);
        Assert.True(answer is not null,
            $"No line of the history is dated {delivered.Timestamp:O} like the delivered answer: {string.Join(", ", history.Select(message => message.Timestamp.ToString("O")))}");
        return answer!;
    }

    /// <summary>
    /// Runs the turn once for the whole group and reads the history straight after it. The conversation is
    /// deliberately left open: every assertion reads the record, which outlives the actors either way.
    /// </summary>
    private Task<DegradedConversation> ConversationAsync()
    {
        lock (DriveGate)
            return driven ??= DriveAsync(fixture, api);
    }

    /// <inheritdoc cref="ConversationAsync" />
    private static async Task<DegradedConversation> DriveAsync(MorganaHostFixture fixture, ChannelApiClient api)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds);

        // Announced under its own channel name: the presentation is cached per name and the harness's own is rich
        (string conversationId, _) = await fixture.Channel.StartConversationAsync(
            timeout, HarnessChannel.DegradedCapabilities, HarnessChannel.DegradedChannelName);
        ChannelMessage delivered = await fixture.Channel.SendAsync(conversationId, InvoiceRequest, timeout);

        IReadOnlyList<MorganaChatMessage> history = await fixture.Channel.GetHistoryAsync(conversationId);
        long keptDegradedMessages = (long)(await api.QueryRecordAsync(conversationId, $"SELECT COUNT(*) FROM {DegradedMessageTable};"))!;
        string? keptUnderRow = await api.QueryRecordAsync(conversationId, $"SELECT agent_name FROM {DegradedMessageTable};") as string;

        return new DegradedConversation(conversationId, delivered, history, keptDegradedMessages, keptUnderRow);
    }

    /// <summary>The conversation as the group reads it: the answer as it was pushed and the history as it was first read.</summary>
    private sealed record DegradedConversation(
        string ConversationId,
        ChannelMessage Delivered,
        IReadOnlyList<MorganaChatMessage> History,
        long KeptDegradedMessages,
        string? KeptUnderRow);
}
