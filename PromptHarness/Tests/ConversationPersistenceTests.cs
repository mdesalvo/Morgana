using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// What a conversation leaves on record, followed step by step from the greeting onwards: who owns
/// each line, when it is dated and how the transcript a returning channel reads is rebuilt out of
/// the rows.
/// </summary>
/// <remarks>
/// <para>One real conversation is run and nothing about its wording is asserted. The oracle is the
/// conversation itself: what the channel was pushed and what the channel said are compared against
/// what the record gives back, so nothing here can fail because a model phrased an answer
/// differently. Every assertion is about ownership, dating and order.</para>
///
/// <para>The record is photographed after every single exchange rather than read once at the end,
/// because that is the only way to see which side each line landed on while it was landing: a
/// transcript that reads correctly can still have been written by the wrong participant and the
/// finished record no longer says who wrote what when. Each turn of <see cref="Script"/> declares
/// whose it is before it is sent, so a step is checked against what was expected of it rather than
/// against whatever it produced.</para>
///
/// <para>The script walks the four ways a turn can end so the record left behind is the one a real
/// conversation leaves: a phrase the guard refuses, a phrase too ambiguous to route, a phrase that
/// reaches a desk and two the desk was waiting for. Morgana answers the first two herself, which is
/// what puts her voice on record with no desk in the conversation at all.</para>
///
/// <para><strong>Runs under <c>Harness__EnableGuardrail=true</c>, alone.</strong> The guard is off
/// everywhere else and a refused turn cannot be staged without it. Nothing is mocked to avoid the
/// knob: a persistence group that wrote its own rows would assert against its own idea of what the
/// pipeline stores, which is the one thing it exists to check.</para>
/// </remarks>
public sealed class ConversationPersistenceTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>
    /// The conversation as it is meant to unfold, each turn carrying how it must end and what the
    /// record must show of it. The phrases are the ones the guard and classifier groups already pin
    /// down, so a turn ending elsewhere is a finding those groups own rather than a surprise here.
    /// </summary>
    private static readonly ScriptedTurn[] Script =
    [
        new ScriptedTurn(
            "This is garbage, you're all worthless and I hope your whole system rots.",
            TurnEnding.RefusedByTheGuard,
            MorganaKeepsIt: true,
            DeskOnRecord: false,
            "the guard refuses it, so no desk is ever reached and Morgana answers for the turn"),
        new ScriptedTurn(
            "What about my billing and my contract?",
            TurnEnding.HandedBackToChoose,
            MorganaKeepsIt: true,
            DeskOnRecord: false,
            "it names two intents at once, so it is handed back for disambiguation before anyone is routed"),
        new ScriptedTurn(
            "Hi, I'd like to see my last 3 invoices",
            TurnEnding.ServedByADesk,
            MorganaKeepsIt: true,
            DeskOnRecord: true,
            "it arrives with nobody serving the user, so Morgana keeps it and then hands the turn to a desk"),
        new ScriptedTurn(
            "P994E",
            TurnEnding.ServedByADesk,
            MorganaKeepsIt: false,
            DeskOnRecord: true,
            "the desk asked for it and is still serving the user, so its own session is the record of the exchange"),
        new ScriptedTurn(
            "Which of those is the oldest?",
            TurnEnding.ServedByADesk,
            MorganaKeepsIt: false,
            DeskOnRecord: true,
            "the desk is still in service, so a second follow-up is filed exactly where the first one was")
    ];

    /// <summary>The orchestrator's name in a row and beside a bubble, where a desk carries its own.</summary>
    private const string OrchestratorName = "Morgana";

    /// <summary>
    /// Marks the copy of a user's phrase a desk holds only so its model could read it. Spelled out
    /// rather than read from <c>Constants</c>: this literal is what a stored row is filtered on, so
    /// renaming it silently must be noticed here rather than asserted against itself.
    /// </summary>
    private const string ContextOnlyMarker = "morgana:context_only";

    /// <summary>The nesting a row's transcript sits under, named literally for the same reason.</summary>
    private const string RowStateBagProperty = "stateBag";

    /// <inheritdoc cref="RowStateBagProperty" />
    private const string RowHistoryStateKey = "MorganaChatHistoryProvider";

    /// <inheritdoc cref="RowStateBagProperty" />
    private const string RowMessagesProperty = "messages";

    /// <summary>
    /// How long a line pushed to the channel is given to reach the record. A reply leaves on one
    /// path and is filed on another, so the two are close together without being ordered.
    /// </summary>
    private static readonly TimeSpan RecordSettlingBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The one conversation the whole group reads, driven on first use. Each test below examines the
    /// same record from a different angle, so a conversation per test would buy nothing and spend a
    /// second set of live turns.
    /// </summary>
    private static Task<DrivenConversation>? driven;

    /// <summary>Guards the single drive against xUnit running the tests of this class in parallel.</summary>
    private static readonly Lock DriveGate = new Lock();

    public ConversationPersistenceTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task The_greeting_is_on_record_before_anybody_has_asked_anything()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<PersistedRow> afterGreeting = conversation.Steps[0].Rows;

        // Morgana speaks first and no desk has been reached, so hers is the only row there can be:
        // a conversation whose opening nobody kept is one every channel has to invent an opening for
        // on a reload, which is what every channel used to do.
        Assert.True(afterGreeting.Count == 1,
            $"The greeting put {afterGreeting.Count} participants on record.{Environment.NewLine}{Describe(afterGreeting)}");

        IReadOnlyList<string> spoken = SpokenBy(RowOf(afterGreeting, OrchestratorName), ChatRole.Assistant);

        Assert.True(spoken.Count == 1,
            $"Morgana's side opens with {spoken.Count} lines instead of the greeting alone.{Environment.NewLine}{Describe(afterGreeting)}");
        Assert.True(spoken[0] == conversation.Steps[0].Delivered.Text,
            "The greeting on record is not the greeting the channel was pushed.");
    }

    [Fact]
    public async Task Every_turn_ends_the_way_the_script_says_it_does()
    {
        DrivenConversation conversation = await ConversationAsync();

        // The record is only worth reading if the conversation that produced it is the one intended:
        // a phrase the guard let through, or one that reached a desk instead of being handed back,
        // leaves a perfectly coherent record of a different conversation.
        for (int turn = 0; turn < Script.Length; turn++)
        {
            ScriptedTurn scripted = Script[turn];
            TurnResult observed = conversation.Steps[turn + 1].Observed!;

            switch (scripted.Ending)
            {
                case TurnEnding.RefusedByTheGuard:
                    Assert.True(observed.GuardCompliant == false,
                        $"\"{Excerpt(scripted.Say)}\" was not refused by the guard, so {scripted.Because} is not what happened.{Environment.NewLine}{observed.Describe()}");
                    Assert.True(observed.AgentName is null,
                        $"A refused phrase reached {observed.AgentName}.{Environment.NewLine}{observed.Describe()}");
                    break;

                case TurnEnding.HandedBackToChoose:
                    Assert.True(observed.GuardCompliant != false,
                        $"\"{Excerpt(scripted.Say)}\" was refused by the guard, so the disambiguation it was chosen for never happened.{Environment.NewLine}{observed.Describe()}");
                    Assert.True(observed.AgentName is null && observed.QuickReplies.Count >= 2,
                        $"\"{Excerpt(scripted.Say)}\" was resolved by {observed.AgentName ?? "nobody"} instead of being handed back to the user to choose.{Environment.NewLine}{observed.Describe()}");
                    break;

                case TurnEnding.ServedByADesk:
                    Assert.True(observed.AgentName is not null,
                        $"\"{Excerpt(scripted.Say)}\" reached no desk, so {scripted.Because} is not what happened.{Environment.NewLine}{observed.Describe()}");
                    break;
            }
        }
    }

    [Fact]
    public async Task Each_phrase_is_kept_by_whoever_the_user_was_talking_to()
    {
        DrivenConversation conversation = await ConversationAsync();

        for (int turn = 0; turn < Script.Length; turn++)
        {
            ScriptedTurn scripted = Script[turn];
            RecordStep step = conversation.Steps[turn + 1];
            IReadOnlyList<PersistedRow> before = conversation.Steps[turn].Rows;

            string keeper = scripted.MorganaKeepsIt ? OrchestratorName : TheDesk(step.Rows).Author;
            IReadOnlyList<string> kept = PhrasesGained(before, step.Rows, keeper);

            Assert.True(kept.Count == 1 && kept[0] == scripted.Say,
                $"\"{Excerpt(scripted.Say)}\" is {keeper}'s to keep, because {scripted.Because}. "
                + $"{keeper} took [{string.Join(" | ", kept.Select(Excerpt))}].{Environment.NewLine}{Describe(step.Rows)}");

            // Nobody else took a copy of it to keep. A desk holding the phrase for its model to read
            // is not keeping it, which is exactly the distinction the transcript is filtered on.
            foreach (PersistedRow row in step.Rows.Where(row => !row.Author.Equals(keeper, StringComparison.OrdinalIgnoreCase)))
                Assert.False(PhrasesGained(before, step.Rows, row.Author).Contains(scripted.Say),
                    $"\"{Excerpt(scripted.Say)}\" was kept by {row.Author} as well as by {keeper}, so the user reads it twice.");
        }
    }

    [Fact]
    public async Task A_turn_no_desk_ever_saw_is_answered_by_Morgana_and_kept_by_her()
    {
        DrivenConversation conversation = await ConversationAsync();

        for (int turn = 0; turn < Script.Length; turn++)
        {
            ScriptedTurn scripted = Script[turn];
            if (scripted.DeskOnRecord)
                continue;

            RecordStep step = conversation.Steps[turn + 1];

            // A refusal and a request to disambiguate both end the turn before anyone is routed, so
            // the conversation still has one participant. A desk appearing here means the turn was
            // served rather than handed back, which the guard and classifier groups would see first.
            Assert.True(step.Rows.Count == 1,
                $"\"{Excerpt(scripted.Say)}\" should have been answered by Morgana alone, because {scripted.Because}."
                + $"{Environment.NewLine}{Describe(step.Rows)}");

            // Her answer is on her own side: without it the transcript carries a question with
            // nothing under it, which is the hole a channel used to fill by inventing a line.
            Assert.True(SpokenBy(RowOf(step.Rows, OrchestratorName), ChatRole.Assistant).Contains(step.Delivered.Text),
                $"What the channel was pushed for \"{Excerpt(scripted.Say)}\" is not on Morgana's own side."
                + $"{Environment.NewLine}{Describe(step.Rows)}");
        }
    }

    [Fact]
    public async Task A_desk_reads_the_phrase_that_routed_to_it_without_owning_it()
    {
        DrivenConversation conversation = await ConversationAsync();

        // The turn that first reaches a desk: Morgana had already filed its phrase at ingress, so
        // the desk's own copy exists for its model alone. Unmarked, one sentence comes back twice.
        int routing = Array.FindIndex(Script, scripted => scripted.MorganaKeepsIt && scripted.DeskOnRecord);
        RecordStep step = conversation.Steps[routing + 1];
        PersistedRow desk = TheDesk(step.Rows);

        Assert.True(desk.Messages.Any(message => message.Role == ChatRole.User
                                                 && message.Text == Script[routing].Say
                                                 && message.AdditionalProperties?.ContainsKey(ContextOnlyMarker) == true),
            $"The desk holds no copy of \"{Excerpt(Script[routing].Say)}\" marked as its model's to read."
            + $"{Environment.NewLine}{Describe(step.Rows)}");
    }

    [Fact]
    public async Task Morgana_never_stands_as_the_desk_a_conversation_is_resumed_onto()
    {
        DrivenConversation conversation = await ConversationAsync();

        // Checked at every step rather than at the end: a row left standing for the length of one
        // turn is enough for a client reconnecting inside that turn to be handed back to somebody
        // who owns no session and cannot carry on.
        foreach (RecordStep step in conversation.Steps)
            Assert.False(RowOf(step.Rows, OrchestratorName).IsActive,
                $"After \"{Excerpt(step.Delivered.Text)}\" Morgana's row stands active, as if she were a desk."
                + $"{Environment.NewLine}{Describe(step.Rows)}");
    }

    [Fact]
    public async Task The_conversation_settles_as_Morgana_and_the_one_desk_that_served_it()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<PersistedRow> settled = conversation.Steps[^1].Rows;

        // Five turns, four of which could have opened a row of their own: what a real conversation
        // leaves behind is the orchestrator and the desks that actually answered, nobody else. A
        // consultation leaves no trace, so a colleague that was asked owns no row here either.
        Assert.True(settled.Count == 2,
            $"The conversation settled as {settled.Count} participants.{Environment.NewLine}{Describe(settled)}");

        RowOf(settled, OrchestratorName);
        PersistedRow desk = TheDesk(settled);

        // The desk is left mid-exchange, which is what a returning client is told to carry on with.
        Assert.True(desk.IsActive,
            $"The desk that was still serving the user is not left active, so a resumed conversation would be reclassified from scratch.");
    }

    [Fact]
    public async Task Every_delivered_line_is_found_again_as_it_was_delivered()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<MorganaChatMessage> history = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);

        foreach (ChannelMessage delivered in conversation.Steps.Select(step => step.Delivered))
        {
            MorganaChatMessage[] matches = [.. history.Where(message => message.Text == delivered.Text)];

            // A channel that missed a push catches up from the history and tells a reply it already
            // has from one it never saw by its date alone. A line dated twice is shown twice.
            Assert.True(matches.Length == 1,
                $"A line the channel was pushed appears {matches.Length} times in the history: \"{Excerpt(delivered.Text)}\".");
            Assert.True(matches[0].Timestamp == delivered.Timestamp,
                $"\"{Excerpt(delivered.Text)}\" was pushed dated {delivered.Timestamp:O} and is on record dated {matches[0].Timestamp:O}.");
        }
    }

    [Fact]
    public async Task The_user_reads_the_conversation_back_in_the_order_it_happened()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<MorganaChatMessage> history = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);

        // The greeting, then each phrase with the answer it drew: the sequence the user lived
        // through, which is the only order a transcript may come back in.
        List<(ChatMessageType Type, string Text)> lived =
        [
            (ChatMessageType.Assistant, conversation.Steps[0].Delivered.Text)
        ];
        for (int turn = 0; turn < Script.Length; turn++)
        {
            lived.Add((ChatMessageType.User, Script[turn].Say));
            lived.Add((ChatMessageType.Assistant, conversation.Steps[turn + 1].Delivered.Text));
        }

        // Read against the transcript in one forward pass: a line found earlier than the one before
        // it is never matched, so an order that only holds by luck fails here rather than reading as
        // a missing line. A line Morgana added of her own accord may sit between two of these.
        int cursor = 0;
        foreach ((ChatMessageType type, string text) in lived)
        {
            int position = FindFrom(history, cursor, type, text);

            Assert.True(position >= 0,
                $"The transcript does not carry \"{Excerpt(text)}\" as {type}, or carries it out of order.{Environment.NewLine}{Describe(history)}");
            cursor = position + 1;
        }
    }

    [Fact]
    public async Task A_phrase_the_user_typed_once_is_read_back_once()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<MorganaChatMessage> history = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);

        foreach (ScriptedTurn scripted in Script)
        {
            // A routed phrase is genuinely on record twice, in two rows for two different readers.
            // A transcript showing both shows the user saying the same thing twice in a row.
            int occurrences = history.Count(message => message.Type == ChatMessageType.User && message.Text == scripted.Say);

            Assert.True(occurrences == 1,
                $"\"{Excerpt(scripted.Say)}\" was typed once and comes back {occurrences} times.{Environment.NewLine}{Describe(history)}");
        }
    }

    [Fact]
    public async Task The_transcript_is_rebuilt_the_same_way_every_time_it_is_read()
    {
        DrivenConversation conversation = await ConversationAsync();

        IReadOnlyList<MorganaChatMessage> first = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);
        IReadOnlyList<MorganaChatMessage> second = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);

        // Nothing is written between the two reads, so any difference comes from the rebuild itself —
        // a line the record leaves undated is dated at the moment it is read, which moves it among
        // the others and shows a returning channel a conversation that never happened.
        Assert.Equal(first.Count, second.Count);
        for (int position = 0; position < first.Count; position++)
        {
            Assert.True(first[position].Text == second[position].Text,
                $"Position {position} reads \"{Excerpt(first[position].Text)}\" once and \"{Excerpt(second[position].Text)}\" the next time.");
            Assert.True(first[position].Timestamp == second[position].Timestamp,
                $"\"{Excerpt(first[position].Text)}\" is dated {first[position].Timestamp:O} once and {second[position].Timestamp:O} the next time.");
            Assert.True(first[position].AgentName == second[position].AgentName,
                $"\"{Excerpt(first[position].Text)}\" is attributed to {first[position].AgentName} once and to {second[position].AgentName} the next time.");
        }
    }

    [Fact]
    public async Task The_transcript_says_who_spoke_each_line()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<MorganaChatMessage> history = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);

        // The greeting is Morgana speaking as herself, with no desk behind it to name.
        string greeting = conversation.Steps[0].Delivered.Text;
        Assert.True(history.Any(message => message.Text == greeting && message.AgentName == OrchestratorName),
            $"The greeting comes back attributed to {history.FirstOrDefault(message => message.Text == greeting)?.AgentName ?? "nobody"}.");

        // A desk is named beside her, so a user sees one assistant with several competences rather
        // than a transcript that changes speaker halfway through.
        Assert.True(history.Any(message => message.AgentName.StartsWith($"{OrchestratorName} (", StringComparison.Ordinal)),
            $"No line in the transcript names the desk that answered.{Environment.NewLine}{Describe(history)}");
    }

    /// <summary>
    /// Runs the script once for the whole group, opening a conversation as any channel would and
    /// photographing the record after every exchange. The conversation is deliberately left open:
    /// every assertion reads the record, which outlives the actors either way.
    /// </summary>
    private Task<DrivenConversation> ConversationAsync()
    {
        lock (DriveGate)
            return driven ??= DriveAsync(fixture);
    }

    /// <inheritdoc cref="ConversationAsync" />
    private static async Task<DrivenConversation> DriveAsync(MorganaHostFixture fixture)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds);

        // The full capability profile the harness channel announces by default keeps the adapter out
        // of the way, so the text compared against the record is the text Morgana composed.
        (string conversationId, ChannelMessage greeting) = await fixture.Channel.StartConversationAsync(timeout);

        List<RecordStep> steps = [await PhotographAsync(fixture, conversationId, greeting, greeting.Text, observed: null)];

        foreach (ScriptedTurn scripted in Script)
        {
            // Watched as every other group watches a turn, so how it ended is read from the
            // pipeline's own spans rather than guessed at from what the record happens to hold.
            TurnScope scope = fixture.Observer.BeginTurn(conversationId);
            ChannelMessage answer = await fixture.Channel.SendAsync(conversationId, scripted.Say, timeout);
            TurnResult observed = await fixture.Observer.CompleteTurnAsync(scope, scripted.Say, answer);

            steps.Add(await PhotographAsync(fixture, conversationId, answer, scripted.Say, observed));
        }

        return new DrivenConversation(conversationId, steps);
    }

    /// <summary>
    /// Reads every row once the exchange has settled into it, so a photograph shows a finished step
    /// rather than one still being written.
    /// </summary>
    /// <param name="settled">
    /// The line whose arrival says the step is on record: the user's phrase for a turn, the greeting
    /// for the opening. A line pushed to the channel is filed on a different path, so the record is
    /// reached shortly after the push rather than before it.
    /// </param>
    private static async Task<RecordStep> PhotographAsync(
        MorganaHostFixture fixture, string conversationId, ChannelMessage delivered, string settled, TurnResult? observed)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        IReadOnlyList<PersistedRow> rows;
        while (true)
        {
            rows = ReadRows(fixture, conversationId);
            if (rows.Any(row => row.Messages.Any(message => message.Text == settled)))
                break;

            Assert.True(elapsed.Elapsed < RecordSettlingBudget,
                $"\"{Excerpt(settled)}\" never reached the record in {RecordSettlingBudget.TotalSeconds:F0}s."
                + $"{Environment.NewLine}{Describe(rows)}");

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return new RecordStep(delivered, rows, observed);
    }

    /// <summary>
    /// Opens the conversation's database and decrypts every participant's row, in the order the
    /// participants first spoke — the order the history endpoint itself reads them in.
    /// </summary>
    private static IReadOnlyList<PersistedRow> ReadRows(MorganaHostFixture fixture, string conversationId)
    {
        byte[] encryptionKey = Convert.FromBase64String(
            fixture.Configuration["Morgana:ConversationPersistence:EncryptionKey"]!);

        using SqliteConnection connection = new SqliteConnection(
            $"Data Source={Path.Combine(fixture.StoragePath, $"morgana-{conversationId}.db")}");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT agent_name, agent_session, is_active FROM morgana ORDER BY creation_date ASC;";

        using SqliteDataReader reader = command.ExecuteReader();

        List<PersistedRow> rows = [];
        while (reader.Read())
            rows.Add(new PersistedRow(
                (string)reader["agent_name"],
                (long)reader["is_active"] == 1,
                ReadMessages(Decrypt((byte[])reader["agent_session"], encryptionKey))));

        return rows;
    }

    /// <summary>Reads the transcript out of a decrypted row, wherever the row's owner keeps the rest.</summary>
    private static IReadOnlyList<ChatMessage> ReadMessages(string rowJson)
    {
        JsonElement row = JsonSerializer.Deserialize<JsonElement>(rowJson, AgentAbstractionsJsonUtilities.DefaultOptions);

        // A desk's row is the session it resurrects itself from and Morgana's carries messages and
        // nothing else, so the transcript is taken from the one nesting both shapes share.
        if (!row.TryGetProperty(RowStateBagProperty, out JsonElement stateBag)
            || !stateBag.TryGetProperty(RowHistoryStateKey, out JsonElement historyState)
            || !historyState.TryGetProperty(RowMessagesProperty, out JsonElement messages))
            return [];

        return JsonSerializer.Deserialize<ChatMessage[]>(
            messages.GetRawText(), AgentAbstractionsJsonUtilities.DefaultOptions) ?? [];
    }

    /// <summary>Recovers a row's plaintext: the initialization vector prefixes the ciphertext it belongs to.</summary>
    private static string Decrypt(byte[] ciphertext, byte[] encryptionKey)
    {
        using Aes aes = Aes.Create();
        aes.Key = encryptionKey;

        byte[] initializationVector = new byte[aes.IV.Length];
        Array.Copy(ciphertext, 0, initializationVector, 0, initializationVector.Length);
        aes.IV = initializationVector;

        using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using MemoryStream cipherStream = new MemoryStream(
            ciphertext, initializationVector.Length, ciphertext.Length - initializationVector.Length);
        using CryptoStream plainStream = new CryptoStream(cipherStream, decryptor, CryptoStreamMode.Read);
        using StreamReader plainReader = new StreamReader(plainStream);

        return plainReader.ReadToEnd();
    }

    /// <summary>
    /// The user's phrases one participant took on during a step, leaving out the copy a desk holds
    /// for its model: a marked copy is not a record of the phrase, it is the phrase being read.
    /// </summary>
    private static IReadOnlyList<string> PhrasesGained(
        IReadOnlyList<PersistedRow> before, IReadOnlyList<PersistedRow> after, string author)
    {
        int alreadyKept = before
            .Where(row => row.Author.Equals(author, StringComparison.OrdinalIgnoreCase))
            .Sum(row => KeptPhrases(row).Count);

        return [.. after.Where(row => row.Author.Equals(author, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(row => KeptPhrases(row).Skip(alreadyKept))];
    }

    /// <inheritdoc cref="PhrasesGained" />
    private static IReadOnlyList<string> KeptPhrases(PersistedRow row) =>
        [.. row.Messages.Where(message => message.Role == ChatRole.User
                                          && message.AdditionalProperties?.ContainsKey(ContextOnlyMarker) != true)
                        .Select(message => message.Text)];

    /// <summary>The row of one named participant, failing with the whole census when there is none.</summary>
    private static PersistedRow RowOf(IReadOnlyList<PersistedRow> rows, string author)
    {
        PersistedRow? row = rows.FirstOrDefault(
            candidate => candidate.Author.Equals(author, StringComparison.OrdinalIgnoreCase));

        Assert.True(row is not null, $"No row for {author}.{Environment.NewLine}{Describe(rows)}");

        return row!;
    }

    /// <summary>
    /// The one desk the script engages. Asserting it is alone is part of the point: a consultation
    /// leaves no trace, so a colleague that answered must own no row here.
    /// </summary>
    private static PersistedRow TheDesk(IReadOnlyList<PersistedRow> rows)
    {
        PersistedRow[] desks =
        [
            .. rows.Where(row => !row.Author.Equals(OrchestratorName, StringComparison.OrdinalIgnoreCase))
        ];

        Assert.True(desks.Length == 1,
            $"The script engages one desk and {desks.Length} rows are not Morgana's.{Environment.NewLine}{Describe(rows)}");

        return desks[0];
    }

    /// <summary>Everything one participant's row holds in a given voice, as plain text.</summary>
    private static IReadOnlyList<string> SpokenBy(PersistedRow row, ChatRole role) =>
        [.. row.Messages.Where(message => message.Role == role).Select(message => message.Text)];

    /// <summary>Position of a line at or after a point in the transcript, or -1 when it is not there.</summary>
    private static int FindFrom(IReadOnlyList<MorganaChatMessage> history, int from, ChatMessageType type, string text)
    {
        for (int position = from; position < history.Count; position++)
            if (history[position].Type == type && history[position].Text == text)
                return position;

        return -1;
    }

    /// <summary>The transcript as a failure should print it: who spoke, when and the opening of what was said.</summary>
    private static string Describe(IReadOnlyList<MorganaChatMessage> history) =>
        string.Join(Environment.NewLine, history.Select(
            (message, position) => $"  [{position}] {message.Timestamp:HH:mm:ss.fff} {message.AgentName}: {Excerpt(message.Text)}"));

    /// <summary>The rows as a failure should print them: who owns one, whether it stands active and what it holds.</summary>
    private static string Describe(IReadOnlyList<PersistedRow> rows) =>
        string.Join(Environment.NewLine, rows.Select(row =>
        {
            StringBuilder description = new StringBuilder(
                $"  {row.Author}{(row.IsActive ? " (active)" : string.Empty)}, {row.Messages.Count} message(s):");

            foreach (ChatMessage message in row.Messages)
                description.Append(
                    $"{Environment.NewLine}    {message.Role}"
                    + $"{(message.AdditionalProperties?.ContainsKey(ContextOnlyMarker) == true ? " (read only)" : string.Empty)}"
                    + $": {Excerpt(message.Text)}");

            return description.ToString();
        }));

    /// <summary>Enough of a line to recognise it in a failure, on one line.</summary>
    private static string Excerpt(string text)
    {
        string oneLine = text.ReplaceLineEndings(" ").Trim();

        return oneLine.Length <= 60 ? oneLine : $"{oneLine[..60]}…";
    }

    /// <summary>How a turn is meant to end, which decides whose the phrase is and who answers it.</summary>
    private enum TurnEnding
    {
        /// <summary>The guard refuses the phrase, so classification and routing never happen.</summary>
        RefusedByTheGuard,

        /// <summary>Two intents come out too close to separate, so the choice goes back to the user.</summary>
        HandedBackToChoose,

        /// <summary>A desk takes the turn, whether it was routed one or was already serving the user.</summary>
        ServedByADesk
    }

    /// <summary>One turn of the conversation, with what the record must show of it before it is sent.</summary>
    /// <param name="MorganaKeepsIt">Whether the phrase is the orchestrator's to file, which it is whenever no desk was serving the user when it arrived.</param>
    /// <param name="DeskOnRecord">Whether a desk owns a row once the turn has been served.</param>
    /// <param name="Because">Why this turn belongs where it does, printed when it does not.</param>
    private sealed record ScriptedTurn(string Say, TurnEnding Ending, bool MorganaKeepsIt, bool DeskOnRecord, string Because);

    /// <summary>The conversation the group reads, as a photograph of the record after every exchange.</summary>
    /// <param name="Steps">The greeting first, then one entry per turn of the script.</param>
    private sealed record DrivenConversation(string ConversationId, IReadOnlyList<RecordStep> Steps);

    /// <summary>One exchange: what the channel was pushed and what the whole record held once it had settled.</summary>
    /// <param name="Observed">How the pipeline handled the turn; absent for the greeting, which answers no turn.</param>
    private sealed record RecordStep(ChannelMessage Delivered, IReadOnlyList<PersistedRow> Rows, TurnResult? Observed);

    /// <summary>One participant's row as the database holds it, decrypted.</summary>
    /// <param name="IsActive">Whether the conversation is standing mid-exchange with this participant.</param>
    private sealed record PersistedRow(string Author, bool IsActive, IReadOnlyList<ChatMessage> Messages);
}
