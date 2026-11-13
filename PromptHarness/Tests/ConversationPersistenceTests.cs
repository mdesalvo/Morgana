using System.Diagnostics;
using System.Globalization;
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
/// What a conversation leaves on record, step by step from the greeting: who owns each line, how it is dated and how the transcript a returning channel reads is rebuilt.
/// Every test reads the one real conversation <see cref="Script"/> drives, photographed after each exchange, since a correct transcript may still have been written by the wrong participant.
/// Nothing about wording is asserted; it needs Harness__EnableUserGuardrail=true in an invocation of its own, since a refused turn cannot be staged without the guard.
/// </summary>
public sealed class ConversationPersistenceTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>
    /// The conversation as it is meant to unfold, each turn carrying how it must end and what the record must show of it.
    /// The phrases are the ones the guard and classifier groups already pin down, so a turn ending elsewhere is a finding those groups own.
    /// </summary>
    private static readonly ScriptedTurn[] Script =
    [
        new ScriptedTurn(
            "This is garbage, you're all worthless and I hope your whole system rots.",
            TurnEnding.RefusedByTheGuard,
            MorganaKeepsIt: true,
            AgentOnRecord: false,
            "the guard refuses it, so no agent is ever reached and Morgana answers for the turn"),
        new ScriptedTurn(
            "What about my billing and my contract?",
            TurnEnding.HandedBackToChoose,
            MorganaKeepsIt: true,
            AgentOnRecord: false,
            "it names two intents at once, so it is handed back for disambiguation before anyone is routed"),
        new ScriptedTurn(
            "Hi, I'd like to see my last 3 invoices",
            TurnEnding.ServedByAnAgent,
            MorganaKeepsIt: true,
            AgentOnRecord: true,
            "it arrives with nobody serving the user, so Morgana keeps it and then hands the turn to an agent"),
        new ScriptedTurn(
            "P994E",
            TurnEnding.ServedByAnAgent,
            MorganaKeepsIt: false,
            AgentOnRecord: true,
            "the agent asked for it and is still serving the user, so its own session is the record of the exchange"),
        new ScriptedTurn(
            "Which of those is the oldest?",
            TurnEnding.ServedByAnAgent,
            MorganaKeepsIt: false,
            AgentOnRecord: true,
            "the agent is still in service, so a second follow-up is filed exactly where the first one was")
    ];

    /// <summary>The orchestrator's name in a row and beside a bubble, where an agent carries its own.</summary>
    private const string OrchestratorName = "Morgana";

    /// <summary>
    /// Marks the copy of a user's phrase an agent holds only so its model could read it.
    /// It is spelled out because a stored row is filtered on it, so a silent rename must be noticed here.
    /// </summary>
    private const string ContextOnlyMarker = "morgana:context_only";

    /// <summary>The nesting a row's transcript sits under, named literally for the same reason.</summary>
    private const string RowStateBagProperty = "stateBag";

    /// <inheritdoc cref="RowStateBagProperty" />
    private const string RowHistoryStateKey = "MorganaChatHistoryProvider";

    /// <inheritdoc cref="RowStateBagProperty" />
    private const string RowMessagesProperty = "messages";

    /// <summary>
    /// How long a line pushed to the channel is given to reach the record.
    /// A reply leaves on one path and is filed on another, so the two are close together without being ordered.
    /// </summary>
    private static readonly TimeSpan RecordSettlingBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The one conversation the whole group reads, driven on first use.
    /// Each test examines the same record from a different angle, so a conversation per test would only spend more live turns.
    /// </summary>
    private static Task<DrivenConversation>? driven;

    /// <summary>Guards the single drive against xUnit running the tests of this class in parallel.</summary>
    private static readonly Lock DriveGate = new Lock();

    /// <summary>Receives the assembly's one live host.</summary>
    public ConversationPersistenceTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task The_greeting_is_on_record_before_anybody_has_asked_anything()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<PersistedRow> afterGreeting = conversation.Steps[0].Rows;

        // Morgana speaks first and no agent has been reached, so hers is the only row there can be:
        // a conversation whose opening nobody kept would leave every channel to invent one on a reload.
        Assert.True(afterGreeting.Count == 1,
            $"The greeting put {afterGreeting.Count} participants on record.{Environment.NewLine}{Describe(afterGreeting)}");

        // What Morgana's side holds in her own voice: the greeting alone, as the channel was pushed it.
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
        // a phrase the guard let through or one that reached an agent instead of being handed back
        // leaves a perfectly coherent record of a different conversation.
        for (int turn = 0; turn < Script.Length; turn++)
        {
            // What the script meant for the turn and what the pipeline's spans say happened; every turn step carries an observation.
            ScriptedTurn scripted = Script[turn];
            TurnResult observed = conversation.Steps[turn + 1].Observed!;

            // Each ending has its own evidence in the spans.
            switch (scripted.Ending)
            {
                // A refusal: the guard said no and no agent was reached.
                case TurnEnding.RefusedByTheGuard:
                    Assert.True(observed.GuardCompliant == false,
                        $"\"{Excerpt(scripted.Say)}\" was not refused by the guard, so {scripted.Because} is not what happened.{Environment.NewLine}{observed.Describe()}");
                    Assert.True(observed.AgentName is null,
                        $"A refused phrase reached {observed.AgentName}.{Environment.NewLine}{observed.Describe()}");
                    break;

                // A disambiguation: the guard let it through and no agent took it, the user being offered buttons to choose.
                case TurnEnding.HandedBackToChoose:
                    Assert.True(observed.GuardCompliant != false,
                        $"\"{Excerpt(scripted.Say)}\" was refused by the guard, so the disambiguation it was chosen for never happened.{Environment.NewLine}{observed.Describe()}");
                    Assert.True(observed.AgentName is null && observed.QuickReplies.Count >= 2,
                        $"\"{Excerpt(scripted.Say)}\" was resolved by {observed.AgentName ?? "nobody"} instead of being handed back to the user to choose.{Environment.NewLine}{observed.Describe()}");
                    break;

                // A served turn: an agent answered it.
                case TurnEnding.ServedByAnAgent:
                    Assert.True(observed.AgentName is not null,
                        $"\"{Excerpt(scripted.Say)}\" reached no agent, so {scripted.Because} is not what happened.{Environment.NewLine}{observed.Describe()}");
                    break;
            }
        }
    }

    [Fact]
    public async Task Each_phrase_is_kept_by_whoever_the_user_was_talking_to()
    {
        DrivenConversation conversation = await ConversationAsync();

        // Each turn is compared with the photograph taken just before it.
        for (int turn = 0; turn < Script.Length; turn++)
        {
            ScriptedTurn scripted = Script[turn];
            RecordStep step = conversation.Steps[turn + 1];
            IReadOnlyList<PersistedRow> before = conversation.Steps[turn].Rows;

            // The participant the script says keeps the phrase took it, alone.
            string keeper = scripted.MorganaKeepsIt ? OrchestratorName : TheAgent(step.Rows).Author;
            IReadOnlyList<string> kept = PhrasesGained(before, step.Rows, keeper);
            Assert.True(kept.Count == 1 && kept[0] == scripted.Say,
                $"\"{Excerpt(scripted.Say)}\" is {keeper}'s to keep, because {scripted.Because}. "
                + $"{keeper} took [{string.Join(" | ", kept.Select(Excerpt))}].{Environment.NewLine}{Describe(step.Rows)}");

            // Nobody else took a copy of it to keep. An agent holding the phrase for its model to read
            // is not keeping it, which is exactly the distinction the transcript is filtered on.
            foreach (PersistedRow row in step.Rows.Where(row => !row.Author.Equals(keeper, StringComparison.OrdinalIgnoreCase)))
                Assert.False(PhrasesGained(before, step.Rows, row.Author).Contains(scripted.Say),
                    $"\"{Excerpt(scripted.Say)}\" was kept by {row.Author} as well as by {keeper}, so the user reads it twice.");
        }
    }

    [Fact]
    public async Task A_turn_no_agent_ever_saw_is_answered_by_Morgana_and_kept_by_her()
    {
        DrivenConversation conversation = await ConversationAsync();

        // Only the turns the script ends before any agent is routed.
        for (int turn = 0; turn < Script.Length; turn++)
        {
            ScriptedTurn scripted = Script[turn];
            if (scripted.AgentOnRecord)
                continue;

            // The record right after that turn.
            RecordStep step = conversation.Steps[turn + 1];

            // A refusal and a request to disambiguate both end the turn before anyone is routed, so
            // the conversation still has one participant. An agent appearing here means the turn was
            // served rather than handed back, which the guard and classifier groups would see first.
            Assert.True(step.Rows.Count == 1,
                $"\"{Excerpt(scripted.Say)}\" should have been answered by Morgana alone, because {scripted.Because}."
                + $"{Environment.NewLine}{Describe(step.Rows)}");

            // Her answer is on her own side: without it the transcript would carry a question with nothing under it.
            Assert.True(SpokenBy(RowOf(step.Rows, OrchestratorName), ChatRole.Assistant).Contains(step.Delivered.Text),
                $"What the channel was pushed for \"{Excerpt(scripted.Say)}\" is not on Morgana's own side."
                + $"{Environment.NewLine}{Describe(step.Rows)}");
        }
    }

    [Fact]
    public async Task An_agent_reads_the_phrase_that_routed_to_it_without_owning_it()
    {
        DrivenConversation conversation = await ConversationAsync();

        // The turn that first reaches an agent: Morgana had already filed its phrase at ingress, so
        // the agent's own copy exists for its model alone. Unmarked, one sentence comes back twice.
        int routing = Array.FindIndex(Script, scripted => scripted.MorganaKeepsIt && scripted.AgentOnRecord);
        RecordStep step = conversation.Steps[routing + 1];
        PersistedRow agent = TheAgent(step.Rows);

        // The agent holds the phrase marked as its model's to read, never as a line it keeps.
        Assert.True(agent.Messages.Any(message => message.Role == ChatRole.User
                                                 && message.Text == Script[routing].Say
                                                 && message.AdditionalProperties?.ContainsKey(ContextOnlyMarker) == true),
            $"The agent holds no copy of \"{Excerpt(Script[routing].Say)}\" marked as its model's to read."
            + $"{Environment.NewLine}{Describe(step.Rows)}");
    }

    [Fact]
    public async Task Morgana_never_stands_as_the_agent_a_conversation_is_resumed_onto()
    {
        DrivenConversation conversation = await ConversationAsync();

        // Checked at every step rather than at the end: a row left standing for the length of one
        // turn is enough for a client reconnecting inside that turn to be handed back to somebody
        // who owns no session and cannot carry on.
        foreach (RecordStep step in conversation.Steps)
            Assert.False(RowOf(step.Rows, OrchestratorName).IsActive,
                $"After \"{Excerpt(step.Delivered.Text)}\" Morgana's row stands active, as if she were an agent."
                + $"{Environment.NewLine}{Describe(step.Rows)}");
    }

    [Fact]
    public async Task The_conversation_settles_as_Morgana_and_the_one_agent_that_served_it()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<PersistedRow> settled = conversation.Steps[^1].Rows;

        // Five turns, four of which could have opened a row of their own: what a real conversation
        // leaves behind is the orchestrator and the agents that actually answered, nobody else. A
        // consultation leaves no trace, so a colleague that was asked owns no row here either.
        Assert.True(settled.Count == 2,
            $"The conversation settled as {settled.Count} participants.{Environment.NewLine}{Describe(settled)}");

        // One of the two is Morgana's (RowOf fails the test without it) while the other is the one agent.
        RowOf(settled, OrchestratorName);
        PersistedRow agent = TheAgent(settled);

        // The agent is left mid-exchange, which is what a returning client is told to carry on with.
        Assert.True(agent.IsActive,
            $"The agent that was still serving the user is not left active, so a resumed conversation would be reclassified from scratch.");
    }

    [Fact]
    public async Task Every_delivered_line_is_found_again_as_it_was_delivered()
    {
        DrivenConversation conversation = await ConversationAsync();
        IReadOnlyList<MorganaChatMessage> history = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);

        // Every line the channel was pushed, the greeting included.
        foreach (ChannelMessage delivered in conversation.Steps.Select(step => step.Delivered))
        {
            // The same text in the transcript a returning channel reads.
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
            // The next occurrence at or after the cursor, which only ever moves forward.
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

        // Two reads of the same record, one right after the other.
        IReadOnlyList<MorganaChatMessage> first = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);
        IReadOnlyList<MorganaChatMessage> second = await fixture.Channel.GetHistoryAsync(conversation.ConversationId);

        // Nothing is written between the two reads, so any difference comes from the rebuild itself —
        // a line the record leaves undated is dated at the moment it is read, which moves it among
        // the others and shows a returning channel a conversation that never happened.
        Assert.Equal(first.Count, second.Count);

        // Position by position, the same text, the same date and the same speaker.
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

        // The greeting is Morgana speaking as herself, with no agent behind it to name.
        string greeting = conversation.Steps[0].Delivered.Text;
        Assert.True(history.Any(message => message.Text == greeting && message.AgentName == OrchestratorName),
            $"The greeting comes back attributed to {history.FirstOrDefault(message => message.Text == greeting)?.AgentName ?? "nobody"}.");

        // An agent is named beside her, so a user sees one assistant with several competences rather
        // than a transcript that changes speaker halfway through.
        Assert.True(history.Any(message => message.AgentName.StartsWith($"{OrchestratorName} (", StringComparison.Ordinal)),
            $"No line in the transcript names the agent that answered.{Environment.NewLine}{Describe(history)}");
    }

    /// <summary>
    /// Runs the script once for the whole group, opening a conversation as any channel would and photographing the record after every exchange.
    /// The conversation is left open, since every assertion reads the record, which outlives the actors either way.
    /// </summary>
    private Task<DrivenConversation> ConversationAsync()
    {
        // The first caller starts the drive and every other one awaits the same task.
        lock (DriveGate)
            return driven ??= DriveAsync(fixture);
    }

    /// <inheritdoc cref="ConversationAsync" />
    private static async Task<DrivenConversation> DriveAsync(MorganaHostFixture fixture)
    {
        // The turn timeout the scenarios run with.
        TimeSpan timeout = TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds);

        // The full capability profile the harness channel announces by default keeps the adapter out
        // of the way, so the text compared against the record is the text Morgana composed.
        (string conversationId, ChannelMessage greeting) = await fixture.Channel.StartConversationAsync(timeout);

        // The first photograph, of the record holding the greeting alone.
        List<RecordStep> steps = [await PhotographAsync(fixture, conversationId, greeting, greeting.Text, observed: null)];

        // One photograph after each scripted turn, once its phrase is on record.
        foreach (ScriptedTurn scripted in Script)
        {
            // Watched as every other group watches a turn, so how it ended is read from the
            // pipeline's own spans rather than guessed at from what the record happens to hold.
            TurnScope scope = fixture.Observer.BeginTurn(conversationId);
            ChannelMessage answer = await fixture.Channel.SendAsync(conversationId, scripted.Say, timeout);
            TurnResult observed = await fixture.Observer.CompleteTurnAsync(scope, scripted.Say, answer);

            steps.Add(await PhotographAsync(fixture, conversationId, answer, scripted.Say, observed));
        }

        // The conversation's id with every photograph, the greeting's first.
        return new DrivenConversation(conversationId, steps);
    }

    /// <summary>Reads every row once the exchange has settled into it, so a photograph shows a finished step rather than one still being written.</summary>
    /// <param name="fixture">The live host, whose storage holds the database.</param>
    /// <param name="conversationId">The conversation photographed.</param>
    /// <param name="delivered">What the channel was pushed for the step.</param>
    /// <param name="settled">The line whose arrival says the step is on record: the user's phrase for a turn, the greeting for the opening.</param>
    /// <param name="observed">How the pipeline handled the turn; null for the greeting.</param>
    private static async Task<RecordStep> PhotographAsync(
        MorganaHostFixture fixture, string conversationId, ChannelMessage delivered, string settled, TurnResult? observed)
    {
        // The wait is bounded by the settling budget, measured from the push.
        Stopwatch elapsed = Stopwatch.StartNew();

        // The rows are read again until the settling line appears in one of them.
        IReadOnlyList<PersistedRow> rows;
        while (true)
        {
            // A line pushed to the channel is filed on another path, so the record catches up a moment after the push.
            rows = ReadRows(fixture, conversationId);
            if (rows.Any(row => row.Messages.Any(message => message.Text == settled)))
                break;

            // A line that never reaches the record within the budget is a fault of the pipeline, reported with the census.
            Assert.True(elapsed.Elapsed < RecordSettlingBudget,
                $"\"{Excerpt(settled)}\" never reached the record in {RecordSettlingBudget.TotalSeconds:F0}s."
                + $"{Environment.NewLine}{Describe(rows)}");

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        // The step as the record held it once settled.
        return new RecordStep(delivered, rows, observed);
    }

    /// <summary>Opens the conversation's database and decrypts every participant's row, in the order the participants first spoke, as the history endpoint reads them.</summary>
    private static IReadOnlyList<PersistedRow> ReadRows(MorganaHostFixture fixture, string conversationId)
    {
        // The key the host encrypts rows with, read from the same configuration the host booted on.
        byte[] encryptionKey = Convert.FromBase64String(
            fixture.Configuration["Morgana:ConversationPersistence:EncryptionKey"]!);

        // The conversation's own database file in the run's throwaway storage.
        using SqliteConnection connection = new SqliteConnection(
            $"Data Source={Path.Combine(fixture.StoragePath, $"morgana-{conversationId}.sqlite")}");
        connection.Open();

        // Every participant's row with its session and whether it stands active.
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT agent_name, agent_session, is_active FROM morgana ORDER BY creation_date ASC;";

        using SqliteDataReader reader = command.ExecuteReader();

        // Each row decrypted and its transcript read out.
        List<PersistedRow> rows = [];
        while (reader.Read())
            rows.Add(new PersistedRow(
                (string)reader["agent_name"],
                (long)reader["is_active"] == 1,
                ReadMessages(Decrypt((byte[])reader["agent_session"], encryptionKey))));

        // The participants in the order they first spoke.
        return rows;
    }

    /// <summary>Reads the transcript out of a decrypted row, wherever the row's owner keeps the rest.</summary>
    private static IReadOnlyList<ChatMessage> ReadMessages(string rowJson)
    {
        // The row as Microsoft.Agents.AI serializes a session.
        JsonElement row = JsonSerializer.Deserialize<JsonElement>(rowJson, AgentAbstractionsJsonUtilities.DefaultOptions);

        // An agent's row is the session it resurrects itself from and Morgana's carries messages and
        // nothing else, so the transcript is taken from the one nesting both shapes share.
        if (!row.TryGetProperty(RowStateBagProperty, out JsonElement stateBag)
            || !stateBag.TryGetProperty(RowHistoryStateKey, out JsonElement historyState)
            || !historyState.TryGetProperty(RowMessagesProperty, out JsonElement messages))
            return [];

        // The messages under the history provider's key.
        return JsonSerializer.Deserialize<ChatMessage[]>(
            messages.GetRawText(), AgentAbstractionsJsonUtilities.DefaultOptions) ?? [];
    }

    /// <summary>Recovers a row's plaintext: the initialization vector prefixes the ciphertext it belongs to.</summary>
    private static string Decrypt(byte[] ciphertext, byte[] encryptionKey)
    {
        // AES with the host's key.
        using Aes aes = Aes.Create();
        aes.Key = encryptionKey;

        // The first block of the stored bytes is the initialization vector the host wrote in front of the ciphertext.
        byte[] initializationVector = new byte[aes.IV.Length];
        Array.Copy(ciphertext, 0, initializationVector, 0, initializationVector.Length);
        aes.IV = initializationVector;

        // The rest of the bytes decrypted into the row's JSON.
        using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using MemoryStream cipherStream = new MemoryStream(
            ciphertext, initializationVector.Length, ciphertext.Length - initializationVector.Length);
        using CryptoStream plainStream = new CryptoStream(cipherStream, decryptor, CryptoStreamMode.Read);
        using StreamReader plainReader = new StreamReader(plainStream);

        return plainReader.ReadToEnd();
    }

    /// <summary>
    /// The user's phrases one participant took on during a step, leaving out the copy an agent holds for its model.
    /// A marked copy is not a record of the phrase: it is the phrase being read.
    /// </summary>
    private static IReadOnlyList<string> PhrasesGained(
        IReadOnlyList<PersistedRow> before, IReadOnlyList<PersistedRow> after, string author)
    {
        // The phrases the participant already kept before the step, which the new ones follow.
        int alreadyKept = before
            .Where(row => row.Author.Equals(author, StringComparison.OrdinalIgnoreCase))
            .Sum(row => KeptPhrases(row).Count);

        // Only those taken on during the step.
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
        // A participant's name is matched as the record writes it, ignoring case.
        PersistedRow? row = rows.FirstOrDefault(
            candidate => candidate.Author.Equals(author, StringComparison.OrdinalIgnoreCase));

        Assert.True(row is not null, $"No row for {author}.{Environment.NewLine}{Describe(rows)}");

        // Present, since the assertion above has passed.
        return row!;
    }

    /// <summary>
    /// The one agent the script engages, asserted alone.
    /// A consultation leaves no trace, so a colleague that answered must own no row here.
    /// </summary>
    private static PersistedRow TheAgent(IReadOnlyList<PersistedRow> rows)
    {
        // Every row that is not Morgana's.
        PersistedRow[] agents =
        [
            .. rows.Where(row => !row.Author.Equals(OrchestratorName, StringComparison.OrdinalIgnoreCase))
        ];

        Assert.True(agents.Length == 1,
            $"The script engages one agent and {agents.Length} rows are not Morgana's.{Environment.NewLine}{Describe(rows)}");

        // The agent, alone as the assertion has just required.
        return agents[0];
    }

    /// <summary>Everything one participant's row holds in a given voice, as plain text.</summary>
    private static IReadOnlyList<string> SpokenBy(PersistedRow row, ChatRole role) =>
        [.. row.Messages.Where(message => message.Role == role).Select(message => message.Text)];

    /// <summary>Position of a line at or after a point in the transcript; -1 when it is not there.</summary>
    private static int FindFrom(IReadOnlyList<MorganaChatMessage> history, int from, ChatMessageType type, string text)
    {
        // The first line of that type and text from the cursor on.
        for (int position = from; position < history.Count; position++)
            if (history[position].Type == type && history[position].Text == text)
                return position;

        // Absent from the cursor on, wherever it may sit before it.
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
            // The row's owner, whether it stands active and how much it holds.
            StringBuilder description = new StringBuilder(
                $"  {row.Author}{(row.IsActive ? " (active)" : string.Empty)}, {row.Messages.Count} message(s):");

            // Each message with its voice, marked when it is an agent's read-only copy of a phrase.
            foreach (ChatMessage message in row.Messages)
                description.Append(
                    CultureInfo.InvariantCulture,
                    $"{Environment.NewLine}    {message.Role}"
                    + $"{(message.AdditionalProperties?.ContainsKey(ContextOnlyMarker) == true ? " (read only)" : string.Empty)}"
                    + $": {Excerpt(message.Text)}");

            return description.ToString();
        }));

    /// <summary>Enough of a line to recognise it in a failure, on one line.</summary>
    private static string Excerpt(string text)
    {
        // A failure message lists many lines, so each is folded onto one.
        string oneLine = text.ReplaceLineEndings(" ").Trim();

        // Sixty characters are enough to recognise a line.
        return oneLine.Length <= 60 ? oneLine : $"{oneLine[..60]}…";
    }

    /// <summary>How a turn is meant to end, which decides whose the phrase is and who answers it.</summary>
    private enum TurnEnding
    {
        /// <summary>The guard refuses the phrase, so classification and routing never happen.</summary>
        RefusedByTheGuard,

        /// <summary>Two intents come out too close to separate, so the choice goes back to the user.</summary>
        HandedBackToChoose,

        /// <summary>An agent takes the turn, whether it was routed one or was already serving the user.</summary>
        ServedByAnAgent
    }

    /// <summary>One turn of the conversation, with what the record must show of it before it is sent.</summary>
    /// <param name="MorganaKeepsIt">Whether the phrase is the orchestrator's to file, which it is whenever no agent was serving the user when it arrived.</param>
    /// <param name="AgentOnRecord">Whether an agent owns a row once the turn has been served.</param>
    /// <param name="Because">Why this turn belongs where it does, printed when it does not.</param>
    private sealed record ScriptedTurn(string Say, TurnEnding Ending, bool MorganaKeepsIt, bool AgentOnRecord, string Because);

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