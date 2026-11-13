using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>The flows of Morgana that reach the model, told apart from the request alone.</summary>
public enum ModelCallKind
{
    /// <summary>The UserGuard judging a user's message.</summary>
    UserGuard,

    /// <summary>The Classifier choosing an intent.</summary>
    Classifier,

    /// <summary>The ToolGuard inspecting a tool result or an offer.</summary>
    ToolGuard,

    /// <summary>The PeerGuard judging a partner's question.</summary>
    PeerGuard,

    /// <summary>Morgana introducing herself.</summary>
    Presentation,

    /// <summary>A round of the inventory agent answering a user.</summary>
    InventoryAgent,

    /// <summary>A round of the billing agent answering a user.</summary>
    BillingAgent,

    /// <summary>A round of the inventory agent answering a colleague.</summary>
    ConsultedInventory,

    /// <summary>A request that matches no flow above and is answered with an error.</summary>
    Unrecognised
}

/// <summary>One request that the fake model received and what became of it.</summary>
/// <param name="Kind">The flow that the request belongs to.</param>
/// <param name="Round">Tool results already in the turn, which is zero for a call that is not an agent's.</param>
/// <param name="Streamed">Whether the caller asked for a streamed answer.</param>
/// <param name="Held">Whether the call was chosen to be held.</param>
/// <param name="StartedAt">When the request arrived.</param>
/// <param name="LastByteAt">When the last frame was flushed; <c>null</c> when none was.</param>
/// <param name="Completed">Whether the whole answer was sent.</param>
/// <param name="AbortedAt">When the caller dropped a held call; <c>null</c> when it never did.</param>
/// <param name="RequestJson">The raw request body, which is what the model was handed.</param>
/// <param name="ResumedAt">When a held streamed call sent its first frame after the pause; <c>null</c> when it never did.</param>
public sealed record ModelCallRecord(
    ModelCallKind Kind,
    int Round,
    bool Streamed,
    bool Held,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastByteAt,
    bool Completed,
    DateTimeOffset? AbortedAt,
    string RequestJson,
    DateTimeOffset? ResumedAt = null);

/// <summary>
/// A fake Ollama server that answers every flow of Morgana from a script and holds the calls that a test chooses, so a cancelled wait can be proven end to end.
/// A held call is never completed: only the caller dropping the connection ends it, which is the fact that the tests read.
/// </summary>
public sealed class CancellableModelHost : IAsyncDisposable
{
    /// <summary>The model name that the fake answers under, echoed in every frame.</summary>
    private const string ModelName = "beam";

    /// <summary>Tokens that the prompt of every completed answer reports, so every completed call costs the same dust.</summary>
    private const int PromptTokens = 100;

    /// <summary>Tokens that the generation of every completed answer reports.</summary>
    private const int GeneratedTokens = 20;

    /// <summary>Frames that a held streamed call sends before it pauses.</summary>
    private const int FramesBeforeHold = 3;

    /// <summary>Text that identifies the UserGuard prompt, found in no other prompt of morgana.json.</summary>
    private const string UserGuardMarker = "guard of her door to users";

    /// <summary>Text that identifies the Classifier prompt, found in no other prompt of morgana.json.</summary>
    private const string ClassifierMarker = "a request classifier";

    /// <summary>Text that identifies the ToolGuard prompt, found in no other prompt of morgana.json.</summary>
    private const string ToolGuardMarker = "guard of her tools";

    /// <summary>Text that identifies the PeerGuard prompt, found in no other prompt of morgana.json.</summary>
    private const string PeerGuardMarker = "guard of her door to partners";

    /// <summary>Text that identifies the Presentation prompt, found in no other prompt of morgana.json.</summary>
    private const string PresentationMarker = "Morgana introducing herself to the user";

    /// <summary>The label that opens the PeerConsultationDeclaration injection, which marks a colleague's question.</summary>
    private const string PeerConsultationLabel = "[A COLLEAGUE IS ASKING YOU]";

    /// <summary>A tool that only the inventory agent offers.</summary>
    private const string InventoryOnlyTool = "CreatePurchaseOrder";

    /// <summary>A tool that only the billing agent offers, which is how it reaches the inventory agent.</summary>
    private const string BillingOnlyTool = "consult_inventory";

    /// <summary>The tool that the inventory agent calls in its first round.</summary>
    private const string CatalogueTool = "GetProductCatalog";

    /// <summary>The question that the billing agent puts to the inventory agent.</summary>
    private const string ConsultationQuestion = "Which plants are in stock?";

    /// <summary>The verdict that every guard answers with.</summary>
    private const string CompliantVerdict = "{\"compliant\":true,\"violation\":null}";

    /// <summary>The application serving the fake model.</summary>
    private readonly WebApplication application;

    /// <summary>Guards the lists below, which the host's request threads and the test thread share.</summary>
    private readonly object gate = new object();

    /// <summary>Every request received so far, in arrival order.</summary>
    private readonly List<ModelCallRecord> calls = [];

    /// <summary>How long a held streamed call pauses before it sends its next line.</summary>
    private readonly TimeSpan heldStreamResumesAfter;

    /// <summary>The calls that tests asked to hold and that have not arrived yet.</summary>
    private readonly List<(ModelCallKind Kind, int Round)> pendingHolds = [];

    /// <summary>How long a held streamed call pauses before its next line, which tests read to bound their wait.</summary>
    public TimeSpan HeldStreamResumesAfter => heldStreamResumesAfter;

    /// <summary>Where the fake model answers, known once it listens.</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>Holds the application that serves the fake model and the pause of a held streamed call.</summary>
    private CancellableModelHost(WebApplication application, TimeSpan heldStreamResumesAfter)
    {
        this.application = application;
        this.heldStreamResumesAfter = heldStreamResumesAfter;
    }

    /// <summary>A snapshot of every request received so far.</summary>
    public IReadOnlyList<ModelCallRecord> Calls
    {
        get
        {
            // The request threads append while a test reads, so the snapshot is taken under the lock.
            lock (gate)
                return calls.ToArray();
        }
    }

    /// <summary>Starts the fake model on a free loopback port and returns once it listens, so the instance under test finds it at boot.</summary>
    public static async Task<CancellableModelHost> StartAsync(TimeSpan heldStreamResumesAfter)
    {
        // A bare web application, configured by none of the suite's files.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Nothing here is evidence: the log would only crowd the host output the suite captures.
        builder.Logging.ClearProviders();

        // Port zero lets the OS pick a free port, read back once Kestrel has bound it.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication application = builder.Build();

        // The routes need the host that records the calls, so the host exists before the application starts.
        CancellableModelHost host = new CancellableModelHost(application, heldStreamResumesAfter);

        // The one route Ollama's chat client calls.
        application.MapPost("/api/chat", (HttpContext context) => host.AnswerChatAsync(context));

        // Any other path is a call that no flow of Morgana is expected to make.
        application.MapFallback((HttpContext context) => host.RefuseUnrecognisedAsync(context));

        // Listening before the instance under test boots, since its startup checks may already ask the model.
        await application.StartAsync();

        // The address Kestrel actually bound, which the tiers of the instance under test are pointed at.
        host.Address = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First()
            .TrimEnd('/');

        // The running fake, which the fixture stops at the end of the run.
        return host;
    }

    /// <summary>Holds the next call of that kind and round, once: a streamed one receives frames up to a point then pauses and resumes with a line past the turn budget until its caller drops it.</summary>
    public void HoldNext(ModelCallKind kind, int round = 0)
    {
        // The call has not arrived yet, so the wish waits in a list that the request threads consume.
        lock (gate)
            pendingHolds.Add((kind, round));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // A held call that nobody dropped would keep the shutdown waiting, so the wait is bounded.
        using CancellationTokenSource shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await application.StopAsync(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // The port is released by the disposal that follows whether or not the stop finished in time.
        }

        // Releases the port for the next invocation of the suite.
        await application.DisposeAsync();
    }

    /// <summary>Answers one chat request from the script, holding it instead when a test asked for that.</summary>
    private async Task AnswerChatAsync(HttpContext context)
    {
        // The arrival time is what the cancellation bounds are measured from.
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        // The body is kept whole because a test reads what the model was handed.
        using StreamReader reader = new StreamReader(context.Request.Body);
        string requestJson = await reader.ReadToEndAsync(context.RequestAborted);
        JsonNode? request = JsonNode.Parse(requestJson);

        // Ollama streams unless the caller says otherwise.
        bool streamed = request?["stream"]?.GetValue<bool>() ?? true;

        // The flow and the round decide both the script and whether a test asked to hold the call.
        ModelCallKind kind = Classify(request);
        int round = CountToolResultsInTurn(request);
        bool held = TakeHold(kind, round);

        // The call is on the record before it is answered, so a test sees a held call while it is held.
        int index = Record(new ModelCallRecord(kind, round, streamed, held, startedAt, null, false, null, requestJson));

        // A request that matches no flow is a fault of the instance under test and never receives a scripted answer.
        if (kind == ModelCallKind.Unrecognised)
        {
            // The error answer is what makes the fault visible to the instance as well as to the record.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        // The script says what the model answers in this flow and round.
        List<JsonObject> frames = ScriptedFrames(kind, round, request);

        // The caller asked for the answer in the form it reads it in.
        if (streamed)
            await StreamAsync(context, index, frames, held);
        else
            await AnswerWholeAsync(context, index, frames, held);
    }

    /// <summary>Records a path that no flow of Morgana is expected to call and answers it with 404.</summary>
    private async Task RefuseUnrecognisedAsync(HttpContext context)
    {
        // The body is kept whole so the fault can be read from the record.
        using StreamReader reader = new StreamReader(context.Request.Body);
        string requestJson = await reader.ReadToEndAsync(context.RequestAborted);

        // The call is on the record so that a test can fail on it; the caller is told the path does not exist.
        Record(new ModelCallRecord(ModelCallKind.Unrecognised, 0, false, false, DateTimeOffset.UtcNow, null, false, null, requestJson));
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>Sends the frames of a streamed answer one object per line; a held call pauses part of the way, then resumes with unending lines.</summary>
    private async Task StreamAsync(HttpContext context, int index, List<JsonObject> frames, bool held)
    {
        // Ollama streams one JSON object per line.
        context.Response.ContentType = "application/x-ndjson";

        // A held call sends the first frames and then pauses: a stream that is slower than the turn budget is the case under test.
        int framesToSend = !held ? frames.Count : frames.Count >= FramesBeforeHold ? FramesBeforeHold : frames.Count - 1;
        for (int frameIndex = 0; frameIndex < framesToSend; frameIndex++)
        {
            // Each frame is flushed on its own so that the caller sees it before the next one exists.
            await WriteLineAsync(context, FrameObject(frames[frameIndex], done: false));
            Update(index, record => record with { LastByteAt = DateTimeOffset.UtcNow });
        }

        // A held call never reaches the closing frame.
        if (held)
        {
            await ResumeUntilDroppedAsync(context, index);
            return;
        }

        // The closing frame carries the token counts that make every completed call cost the same.
        await WriteLineAsync(context, FrameObject(new JsonObject { ["role"] = "assistant", ["content"] = "" }, done: true));
        Update(index, record => record with { LastByteAt = DateTimeOffset.UtcNow, Completed = true });
    }

    /// <summary>Sends the frames of a non-streamed answer as one object; a held call is sent nothing.</summary>
    private async Task AnswerWholeAsync(HttpContext context, int index, List<JsonObject> frames, bool held)
    {
        // A held call sends nothing at all: the caller waits for the whole response.
        if (held)
        {
            await WaitUntilDroppedAsync(context, index);
            return;
        }

        // The frames of a streamed answer fold into the one message that a non-streamed answer carries.
        JsonObject message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = string.Concat(frames.Select(frame => frame["content"]?.GetValue<string>()))
        };

        // A scripted tool call travels in the one message as well.
        JsonArray? toolCalls = frames.Select(frame => frame["tool_calls"]).OfType<JsonArray>().FirstOrDefault();
        if (toolCalls is not null)
            message["tool_calls"] = JsonNode.Parse(toolCalls.ToJsonString());

        // The whole answer is one object with the token counts that a completed call reports.
        context.Response.ContentType = "application/json";
        await WriteLineAsync(context, FrameObject(message, done: true));
        Update(index, record => record with { LastByteAt = DateTimeOffset.UtcNow, Completed = true });
    }

    /// <summary>Pauses a held streamed call then sends a line every second until the caller drops the connection or a minute passes.</summary>
    private async Task ResumeUntilDroppedAsync(HttpContext context, int index)
    {
        try
        {
            // The pause outlasts the turn budget, as a real model slower than the budget would.
            await Task.Delay(heldStreamResumesAfter, context.RequestAborted);

            // The first line after the pause is what a reader that ignores the stop would hand to the caller.
            Update(index, record => record with { ResumedAt = DateTimeOffset.UtcNow });
            DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTimeOffset.UtcNow < giveUpAt)
            {
                // The line carries text that no completed answer holds, so a test can tell it from the answer.
                await WriteLineAsync(context, FrameObject(new JsonObject { ["role"] = "assistant", ["content"] = " more" }, done: false));
                Update(index, record => record with { LastByteAt = DateTimeOffset.UtcNow });

                // One line a second keeps the connection busy so that its closing is noticed at the next write.
                await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The caller dropped the connection, which is how Morgana cancels the model call.
            Update(index, record => record with { AbortedAt = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>Waits for the caller to drop the connection and notes when it did.</summary>
    private async Task WaitUntilDroppedAsync(HttpContext context, int index)
    {
        try
        {
            // A held call has no timeout of its own: the fact under test is that the caller gives up.
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The caller dropped the connection, which is how Morgana cancels the model call.
            Update(index, record => record with { AbortedAt = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>Tells which flow of Morgana sent the request.</summary>
    private static ModelCallKind Classify(JsonNode? request)
    {
        // The names of the tools offered tell the agents apart; the guards and the classifier offer none.
        List<string> toolNames = (request?["tools"] as JsonArray ?? [])
            .Select(tool => tool?["function"]?["name"]?.GetValue<string>() ?? string.Empty)
            .ToList();

        // The conversation as the model is handed it.
        List<JsonNode?> messages = (request?["messages"] as JsonArray ?? []).ToList();

        // Only the inventory agent offers the tool that places an order.
        bool offersInventoryTool = toolNames.Contains(InventoryOnlyTool, StringComparer.Ordinal);

        // A colleague's question is the one user message that opens with the consultation label.
        bool carriesConsultationLabel = messages.Any(message =>
            message?["role"]?.GetValue<string>() == "user"
            && (message["content"]?.GetValue<string>() ?? string.Empty).Contains(PeerConsultationLabel, StringComparison.Ordinal));

        // The inventory agent asked by a colleague, which answers facts only.
        if (offersInventoryTool && carriesConsultationLabel)
            return ModelCallKind.ConsultedInventory;

        // The inventory agent asked by a user.
        if (offersInventoryTool)
            return ModelCallKind.InventoryAgent;

        // Only the billing agent holds the tool that reaches the inventory agent.
        if (toolNames.Contains(BillingOnlyTool, StringComparer.Ordinal))
            return ModelCallKind.BillingAgent;

        // The remaining flows are told apart by the prompt each one is handed as its system message.
        string systemText = string.Concat(messages
            .Where(message => message?["role"]?.GetValue<string>() == "system")
            .Select(message => message?["content"]?.GetValue<string>()));

        // The guard of the user's door.
        if (systemText.Contains(UserGuardMarker, StringComparison.Ordinal))
            return ModelCallKind.UserGuard;

        // The intent classifier.
        if (systemText.Contains(ClassifierMarker, StringComparison.Ordinal))
            return ModelCallKind.Classifier;

        // The guard of the tools.
        if (systemText.Contains(ToolGuardMarker, StringComparison.Ordinal))
            return ModelCallKind.ToolGuard;

        // The guard of the partners' door.
        if (systemText.Contains(PeerGuardMarker, StringComparison.Ordinal))
            return ModelCallKind.PeerGuard;

        // Morgana's introduction.
        if (systemText.Contains(PresentationMarker, StringComparison.Ordinal))
            return ModelCallKind.Presentation;

        // A request that no prompt of Morgana explains.
        return ModelCallKind.Unrecognised;
    }

    /// <summary>Counts the tool results that follow the last user message, which is the round an agent is in.</summary>
    private static int CountToolResultsInTurn(JsonNode? request)
    {
        // The conversation as the agent hands it to the model, its own tool results included.
        List<JsonNode?> messages = (request?["messages"] as JsonArray ?? []).ToList();

        // Earlier turns also hold tool results, so only the messages after the last user message belong to this round.
        int lastUserIndex = messages.FindLastIndex(message => message?["role"]?.GetValue<string>() == "user");

        // The round is how many tool results the turn already holds: zero on its first call.
        return messages.Skip(lastUserIndex + 1).Count(message => message?["role"]?.GetValue<string>() == "tool");
    }

    /// <summary>Consumes the pending hold that matches the call, if a test asked for one.</summary>
    private bool TakeHold(ModelCallKind kind, int round)
    {
        // The request threads and the test thread both touch the list of wishes.
        lock (gate)
            return pendingHolds.Remove((kind, round));
    }

    /// <summary>Appends a call to the record and returns its position.</summary>
    private int Record(ModelCallRecord call)
    {
        // The position stays valid because calls are only ever appended.
        lock (gate)
        {
            calls.Add(call);
            return calls.Count - 1;
        }
    }

    /// <summary>Replaces a call of the record with its updated copy.</summary>
    private void Update(int index, Func<ModelCallRecord, ModelCallRecord> change)
    {
        // The change is applied under the lock so that two updates of one call never overwrite each other.
        lock (gate)
            calls[index] = change(calls[index]);
    }

    /// <summary>The message objects the script answers a call with, one per streamed frame.</summary>
    private static List<JsonObject> ScriptedFrames(ModelCallKind kind, int round, JsonNode? request)
    {
        // Each flow has its own script; an unrecognised one has none.
        switch (kind)
        {
            // The guards admit everything: the cancelled flows are the subject, not the verdicts.
            case ModelCallKind.UserGuard:
            case ModelCallKind.ToolGuard:
            case ModelCallKind.PeerGuard:
                return [TextFrame(CompliantVerdict)];

            // The classifier routes an invoice request to billing and every other request to inventory.
            case ModelCallKind.Classifier:
                string intent = LastUserText(request).Contains("invoice", StringComparison.OrdinalIgnoreCase) ? "billing" : "inventory";
                return [TextFrame($"{{\"intents\":[{{\"intent\":\"{intent}\",\"confidence\":0.95}}]}}")];

            // The presentation holds both texts that the presenter deserializes.
            case ModelCallKind.Presentation:
                return [TextFrame("{\"message\":\"Welcome to the cauldron.\",\"reintroduction\":\"Here is what the cauldron holds.\"}")];

            // The inventory agent reads the catalogue first and answers from it in the next round.
            case ModelCallKind.InventoryAgent:
                return round == 0
                    ? [ToolCallFrame(CatalogueTool, new JsonObject())]
                    : TextFrames("Here ", "is ", "our ", "catalogue.");

            // The billing agent asks the inventory agent first and relays its answer in the next round.
            case ModelCallKind.BillingAgent:
                return round == 0
                    ? [ToolCallFrame(BillingOnlyTool, ConsultationArguments(request))]
                    : TextFrames("Here ", "is ", "what ", "I ", "found.");

            // The inventory agent answers a colleague in one sentence of facts.
            case ModelCallKind.ConsultedInventory:
                return [TextFrame("Plenty in stock.")];

            // An unrecognised call never reaches the script.
            default:
                return [];
        }
    }

    /// <summary>The arguments that fill every string parameter of the consultation tool, as its schema in the request declares them, with the question.</summary>
    private static JsonObject ConsultationArguments(JsonNode? request)
    {
        // The schema of the consultation tool, as the agent offered it in this very request.
        JsonNode? consultationTool = (request?["tools"] as JsonArray ?? [])
            .FirstOrDefault(tool => tool?["function"]?["name"]?.GetValue<string>() == BillingOnlyTool);

        // The arguments start empty and take one entry per parameter of the schema.
        JsonObject arguments = new JsonObject();

        // Every string parameter that the schema declares carries the question; no other kind is declared.
        if (consultationTool?["function"]?["parameters"]?["properties"] is JsonObject properties)
            foreach (KeyValuePair<string, JsonNode?> property in properties)
                if (property.Value?["type"]?.GetValue<string>() == "string")
                    arguments[property.Key] = ConsultationQuestion;

        // The arguments the billing agent's tool call carries, whatever its parameter is named.
        return arguments;
    }

    /// <summary>The text of the last user message, which is what the classifier is asked about.</summary>
    private static string LastUserText(JsonNode? request)
    {
        // The classifier receives the user's phrase as its last user message.
        JsonNode? lastUser = (request?["messages"] as JsonArray ?? [])
            .LastOrDefault(message => message?["role"]?.GetValue<string>() == "user");

        // Empty when the request carries no user message, which routes the request to inventory.
        return lastUser?["content"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>One frame carrying a piece of text.</summary>
    private static JsonObject TextFrame(string text) => new JsonObject { ["role"] = "assistant", ["content"] = text };

    /// <summary>One frame per piece of text, which is how a streamed answer arrives.</summary>
    private static List<JsonObject> TextFrames(params string[] pieces) => pieces.Select(TextFrame).ToList();

    /// <summary>One frame carrying a call of a tool.</summary>
    private static JsonObject ToolCallFrame(string toolName, JsonObject arguments) => new JsonObject
    {
        ["role"] = "assistant",
        ["content"] = "",
        ["tool_calls"] = new JsonArray(new JsonObject
        {
            ["id"] = $"call_{Guid.NewGuid():N}",
            ["function"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments }
        })
    };

    /// <summary>Wraps a message in the object Ollama sends, closing the answer with the token counts when it is the last.</summary>
    private static JsonObject FrameObject(JsonObject message, bool done)
    {
        // The fields that every object of an Ollama answer carries.
        JsonObject frame = new JsonObject
        {
            ["model"] = ModelName,
            ["created_at"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["message"] = JsonNode.Parse(message.ToJsonString()),
            ["done"] = done
        };

        // Only the last object of an answer reports how it ended and what it cost.
        if (done)
        {
            frame["done_reason"] = "stop";
            frame["prompt_eval_count"] = PromptTokens;
            frame["eval_count"] = GeneratedTokens;
        }

        // The object as Ollama's client reads it.
        return frame;
    }

    /// <summary>Writes one object as a line and flushes it, so the caller reads it before the next is written.</summary>
    private static async Task WriteLineAsync(HttpContext context, JsonObject frame)
    {
        // One object per line is the framing that Ollama's client splits the stream on.
        await context.Response.WriteAsync(frame.ToJsonString() + "\n", context.RequestAborted);

        // Without the flush the line would wait in a buffer and a held call would look silent before it is.
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}