using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI.Abstractions;
using Morgana.AI.Providers;
using Morgana.AI.Services;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting what a presentation tool tells the model about the text of its turn. A landing
/// declaration reaches the model on the landing turn alone.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: no host, no model. A scripted chat client stands where the model would, behind
/// the real function-invoking loop that the tools run in.</para>
///
/// <para>Every literal a model sees is spelled out here rather than read from <c>Constants</c>, as the
/// wire-contract groups do: the point is to notice the contract changing.</para>
/// </remarks>
public sealed class TurnEndingTests
{
    /// <summary>The fact a presentation tool appends while the turn has written no text.</summary>
    private const string NothingReachedFact = " Nothing has reached the user in this turn yet.";

    /// <summary>The marker that opens the landing declaration.</summary>
    private const string LandingMarker = "[THE USER HAS JUST CHOSEN YOU]";

    [Theory]
    [InlineData("SetQuickReplies")]
    [InlineData("SetRichCard")]
    [InlineData("SetTurnEnding")]
    public async Task Presentation_tool_called_before_any_text_states_that_nothing_reached_the_user(string toolName)
    {
        // The model calls the tool first and writes its words in the response after it.
        ScriptedChatClient model = new ScriptedChatClient(Call(toolName), Text("Here you are."));
        ToolRig rig = await ToolRig.CreateAsync(model);

        await rig.Agent.RunAsync([new ChatMessage(ChatRole.User, "Show my invoices")], rig.Session, cancellationToken: TestContext.Current.CancellationToken);

        // The result is the last thing the model reads before writing, so the fact closes it.
        Assert.EndsWith(NothingReachedFact, ResultOf(model, "call-1"), StringComparison.Ordinal);
    }

    [Theory]
    // The text and the call arrive in one message of the model.
    [InlineData("SetQuickReplies", true)]
    [InlineData("SetRichCard", true)]
    [InlineData("SetTurnEnding", true)]
    // The text comes with an earlier call and the tool under test is called in the round after it.
    [InlineData("SetQuickReplies", false)]
    [InlineData("SetRichCard", false)]
    [InlineData("SetTurnEnding", false)]
    public async Task Presentation_tool_called_after_text_states_nothing(string toolName, bool sameMessage)
    {
        // Shape one carries text and call together; shape two writes text beside a lookup and calls the tool next.
        ScriptedChatClient model = sameMessage
            ? new ScriptedChatClient(TextAndCall("Let me set that up.", ToolCall(toolName)), Text("Here you are."))
            : new ScriptedChatClient(
                TextAndCall("Let me look.", new FunctionCallContent("call-0", "GetContextVariable", new Dictionary<string, object?> { ["variableName"] = "customerCode" })),
                Call(toolName),
                Text("Here you are."));
        ToolRig rig = await ToolRig.CreateAsync(model);

        await rig.Agent.RunAsync([new ChatMessage(ChatRole.User, "Show my invoices")], rig.Session, cancellationToken: TestContext.Current.CancellationToken);

        // The turn has already written its words, so the tool has no debt to announce.
        Assert.DoesNotContain("Nothing has reached the user", ResultOf(model, "call-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Words_of_an_earlier_turn_do_not_count_as_written_in_this_one()
    {
        // The model calls the tool first and writes its words after it.
        ScriptedChatClient model = new ScriptedChatClient(Call("SetQuickReplies"), Text("Here you are."));
        ToolRig rig = await ToolRig.CreateAsync(model);

        // The history holds an answer the user read in an earlier turn; the new phrase starts this one.
        await rig.Agent.RunAsync(
            [
                new ChatMessage(ChatRole.User, "Hello"),
                new ChatMessage(ChatRole.Assistant, "Hello, how can I help?"),
                new ChatMessage(ChatRole.User, "Show my invoices")
            ],
            rig.Session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Only text after the new phrase counts, so the turn is still owed its words.
        Assert.EndsWith(NothingReachedFact, ResultOf(model, "call-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presentation_tool_called_outside_a_tool_loop_claims_nothing()
    {
        // No model is involved: the tool is called directly, so no loop knows what the turn has written.
        ToolRig rig = await ToolRig.CreateAsync(new ScriptedChatClient());

        object? result = await rig.Tool.SetQuickReplies([new Morgana.Contracts.QuickReply("yes", "Yes", "Yes, please", false)]);

        // Without a loop there is nothing to state a fact about.
        Assert.DoesNotContain("Nothing has reached the user", result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Landing_declaration_reaches_the_model_on_the_landing_turn_alone()
    {
        // The real composer over the embedded morgana.json: the landing declaration is authored there.
        ConfigurationPromptComposerService composer = new ConfigurationPromptComposerService(
            new ConfigurationPromptResolverService(new ScriptedDomain()));
        MorganaAIContextProvider provider = new MorganaAIContextProvider(NullLogger.Instance, promptComposerService: composer);
        ScriptedChatClient model = new ScriptedChatClient(Text("Welcome."), Text("Here you are."));
        ChatClientAgent agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            AIContextProviders = [provider],
            ChatOptions = new ChatOptions { Instructions = "You keep the accounts book." }
        });
        AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // The landing turn: the session holds the key that the agent sets when the user pressed Morgana's button.
        await provider.SetVariableAsync(session, "landing", true);
        await agent.RunAsync("What can you do with my invoices?", session, cancellationToken: TestContext.Current.CancellationToken);

        // The agent drops the key at the end of the turn, which the next turn then runs without.
        provider.DropVariable(session, "landing");
        await agent.RunAsync("Show my invoices", session, cancellationToken: TestContext.Current.CancellationToken);

        // The per-run instructions land in ChatOptions.Instructions of the request, not in a system message.
        Assert.Contains(LandingMarker, model.Requests[0].Options!.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(LandingMarker, model.Requests[1].Options!.Instructions, StringComparison.Ordinal);
    }

    /// <summary>A model answer made of text alone.</summary>
    private static ChatResponse Text(string text) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>A model answer made of one call to the tool, with arguments that the tool accepts.</summary>
    private static ChatResponse Call(string toolName) => new ChatResponse(new ChatMessage(ChatRole.Assistant, [ToolCall(toolName)]));

    /// <summary>A model answer that writes some text and calls a tool in the same message.</summary>
    private static ChatResponse TextAndCall(string text, FunctionCallContent call)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextContent(text), call]));

    /// <summary>The call of a presentation tool, identified as <c>call-1</c> with valid arguments.</summary>
    private static FunctionCallContent ToolCall(string toolName)
    {
        // Each tool takes its own parameter: one button, a minimal card as JSON text or nothing at all.
        Dictionary<string, object?>? arguments = toolName switch
        {
            "SetQuickReplies" => new Dictionary<string, object?>
            {
                ["quickReplies"] = JsonSerializer.SerializeToElement(new[] { new { id = "yes", label = "Yes", value = "Yes, please", termination = false } })
            },
            "SetRichCard" => new Dictionary<string, object?>
            {
                ["richCard"] = """{"title":"Account","subtitle":null,"components":[{"type":"divider"}]}"""
            },
            _ => null
        };

        return new FunctionCallContent("call-1", toolName, arguments);
    }

    /// <summary>The text of the tool result that the model read in the request after the call.</summary>
    private static string ResultOf(ScriptedChatClient model, string callId)
        => model.Requests
            .SelectMany(request => request.Messages)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Single(functionResult => functionResult.CallId == callId)
            .Result?.ToString() ?? string.Empty;

    /// <summary>A plain tool and an agent whose model sits behind the real tool loop, sharing one session.</summary>
    private sealed class ToolRig
    {
        /// <summary>The tool that the loop exposes to the model.</summary>
        public required MorganaTool Tool { get; init; }

        /// <summary>The agent whose model is the scripted client behind the loop.</summary>
        public required ChatClientAgent Agent { get; init; }

        /// <summary>The session that the tool reads and writes its context in.</summary>
        public required AgentSession Session { get; init; }

        /// <summary>Wires the tool, the loop and the session around the scripted model.</summary>
        public static async Task<ToolRig> CreateAsync(ScriptedChatClient model)
        {
            // The loop is the real one: the tools are only told what the turn wrote because it runs them.
            IChatClient loop = new ChatClientBuilder(model).UseFunctionInvocation().Build();
            MorganaAIContextProvider provider = new MorganaAIContextProvider(NullLogger.Instance);

            // The session belongs to an agent that carries no tools: it only holds the tool's context variables.
            AgentSession session = await new ChatClientAgent(new ScriptedChatClient()).CreateSessionAsync();
            MorganaTool tool = new MorganaTool(
                NullLogger.Instance,
                () => new MorganaTool.ToolContext(provider, session, "conversation-1"));

            // The base tools offered to the model are the tool's own methods.
            ChatClientAgent agent = new ChatClientAgent(loop, new ChatClientAgentOptions
            {
                AIContextProviders = [provider],
                ChatOptions = new ChatOptions
                {
                    Tools =
                    [
                        AIFunctionFactory.Create(tool.GetContextVariable, "GetContextVariable"),
                        AIFunctionFactory.Create(tool.SetQuickReplies, "SetQuickReplies"),
                        AIFunctionFactory.Create(tool.SetRichCard, "SetRichCard"),
                        AIFunctionFactory.Create(tool.SetTurnEnding, "SetTurnEnding")
                    ]
                }
            });

            return new ToolRig { Tool = tool, Agent = agent, Session = session };
        }
    }
}
