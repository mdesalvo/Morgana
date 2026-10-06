using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting what an agent's model reads of its own history: the current episode only and
/// the tool results of earlier turns marked as such, while the stored history stays whole.
/// </summary>
/// <remarks>
/// Deterministic and free: a real agent runs over a client that answers nothing and only records the
/// messages it was handed, which is exactly the view.
/// </remarks>
public sealed class HistoryViewTests
{
    [Fact]
    public async Task Returning_user_opens_a_new_episode()
    {
        ViewUnderTest agent = await ViewUnderTest.CreateAsync();
        agent.File(new ChatMessage(ChatRole.User, "Show my invoices"));
        ChatMessage farewell = new ChatMessage(ChatRole.Assistant, "Farewell!");
        farewell.AdditionalProperties = new AdditionalPropertiesDictionary { ["morgana:episode_end"] = true };
        agent.File(new ChatMessage(ChatRole.User, "We're done, thanks"));
        agent.File(farewell);

        IReadOnlyList<ChatMessage> view = await agent.RunTurnAsync("Show my payment history");

        // The farewell and everything before it stay on the record and out of the model's sight.
        Assert.Equal(["Show my payment history"], view.Select(message => message.Text));
        Assert.Contains(agent.Stored, message => message.Text == "Farewell!");
    }

    [Fact]
    public async Task Tool_results_of_earlier_turns_arrive_marked_and_stay_untouched_on_record()
    {
        ViewUnderTest agent = await ViewUnderTest.CreateAsync();
        agent.File(new ChatMessage(ChatRole.User, "How many ferns are in stock?"));
        agent.File(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "CheckStockLevel")]));
        agent.File(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "FERN-001: 12 in stock")]));
        agent.File(new ChatMessage(ChatRole.Assistant, "Twelve ferns are waiting for you."));

        IReadOnlyList<ChatMessage> view = await agent.RunTurnAsync("And now?");

        string earlierResult = view.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Equal("[EARLIER TURN]\nFERN-001: 12 in stock", earlierResult);
        Assert.Equal("FERN-001: 12 in stock", agent.StoredResult());
    }

    [Fact]
    public async Task Without_a_template_earlier_results_are_handed_back_as_returned()
    {
        ViewUnderTest agent = await ViewUnderTest.CreateAsync(markEarlierResults: false);
        agent.File(new ChatMessage(ChatRole.User, "How many ferns are in stock?"));
        agent.File(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "CheckStockLevel")]));
        agent.File(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "FERN-001: 12 in stock")]));

        IReadOnlyList<ChatMessage> view = await agent.RunTurnAsync("And now?");

        Assert.Equal("FERN-001: 12 in stock",
            view.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single().Result!.ToString());
    }

    /// <summary>
    /// An agent over Morgana's history provider whose model only records what it is handed.
    /// </summary>
    private sealed class ViewUnderTest
    {
        /// <summary>The provider under test.</summary>
        private MorganaChatHistoryProvider history = null!;

        /// <summary>The agent running over it.</summary>
        private ChatClientAgent agent = null!;

        /// <summary>The agent's session, holding the stored history.</summary>
        private AgentSession session = null!;

        /// <summary>The model, recording each request.</summary>
        private readonly RecordingChatClient model = new RecordingChatClient();

        /// <summary>The messages on record.</summary>
        public IReadOnlyList<ChatMessage> Stored => history.GetMessages(session);

        /// <summary>
        /// Builds the agent, marking earlier results with a short template or leaving them unmarked.
        /// </summary>
        public static async Task<ViewUnderTest> CreateAsync(bool markEarlierResults = true)
        {
            ViewUnderTest view = new ViewUnderTest();
            view.history = new MorganaChatHistoryProvider("inventory", null, NullLogger.Instance,
                promptComposerService: new EarlierResultComposer(markEarlierResults ? "[EARLIER TURN]\n((result))" : null));
            view.agent = new ChatClientAgent(view.model, new ChatClientAgentOptions { ChatHistoryProvider = view.history });
            view.session = await view.agent.CreateSessionAsync();
            return view;
        }

        /// <summary>Puts a message on record, as an earlier turn would have.</summary>
        public void File(ChatMessage message) => history.AppendMessage(session, message);

        /// <summary>The stored result of the earlier tool call, as the tool returned it.</summary>
        public string StoredResult()
            => history.GetMessages(session).SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!;

        /// <summary>
        /// Files the user's message and runs a turn as MorganaAgent does, answering what the model was handed.
        /// </summary>
        public async Task<IReadOnlyList<ChatMessage>> RunTurnAsync(string userText)
        {
            File(new ChatMessage(ChatRole.User, userText));
            await agent.RunAsync(session);
            return model.LastRequest;
        }
    }

    /// <summary>
    /// A composer holding only the earlier-result template, as morgana.json would declare it.
    /// </summary>
    private sealed class EarlierResultComposer(string? template) : IPromptComposerService
    {
        public Task<string?> ComposeEarlierToolResultAsync(string result)
            => Task.FromResult(template?.Replace("((result))", result));

        public Task<string> ComposeAgentInstructionsAsync(Records.Prompt domainPrompt, bool peerCapable = false) => throw new NotSupportedException();
        public Task<string> ComposeToolDescriptionAsync(Records.ToolDefinition toolDefinition) => throw new NotSupportedException();
        public Task<string> ComposePeerDescriptionAsync(A2A.AgentCard peerCard) => throw new NotSupportedException();
        public Task<string?> ComposeColleaguesDeclarationAsync(IReadOnlyDictionary<string, string> colleagues) => throw new NotSupportedException();
        public Task<string> ComposeConsultationRequestAsync(string? callerIntent, string question) => throw new NotSupportedException();
        public Task<string?> ComposeTurnClosureRequestAsync() => throw new NotSupportedException();
        public Task<string> ComposeReplyNotAcceptedAsync(string reason) => throw new NotSupportedException();
        public Task<string> ComposeToolResultAsync(string name, IReadOnlyDictionary<string, string>? values = null) => throw new NotSupportedException();
        public Task<string?> ComposeWorkflowResultAsync(string workflow, string? step, string result) => throw new NotSupportedException();
    }

    /// <summary>
    /// Stands where the model would, answering an empty text and keeping the messages of the last request.
    /// </summary>
    private sealed class RecordingChatClient : IChatClient
    {
        /// <summary>What the last request carried: the view that the agent's model would read.</summary>
        public IReadOnlyList<ChatMessage> LastRequest { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastRequest = [.. messages];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "noted")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No streaming is scripted in this group");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
