using System.Text.Json;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Services;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting the signature service that stands between the model and every tool, for a careless model: a value
/// written in another form of its parameter's elementary type is read as meant with no round of repair, while every
/// other break of a schema is refused naming the argument at fault, so that the model repairs the call at once.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: a scripted chat client stands where the model would, making the slips that a cheap tier
/// makes. Every literal that the model reads is spelled out here, so that a change of wording is noticed.</para>
/// </remarks>
public sealed class CallSignatureTests
{
    /// <summary>The service under test, which holds no state between calls.</summary>
    private static readonly SchemaCallSignatureService CallSignatureService = new SchemaCallSignatureService();

    /// <summary>A tool taking one value of each elementary type, every one of them required.</summary>
    private static readonly AIFunction Water = AIFunctionFactory.Create((int liters, bool urgent, double ratio) => "watered", "Water");

    /// <summary>A tool whose only parameter lists the words that it takes, as a closed set does.</summary>
    private static readonly AIFunction Sow = AIFunctionFactory.Create(
        (Season season) => "sown", "Sow", serializerOptions: new JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(), Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } });

    /// <summary>The seasons that Sow takes.</summary>
    private enum Season
    {
        /// <summary>The first sowing.</summary>
        Spring,

        /// <summary>The second sowing.</summary>
        Autumn
    }

    // =========================================================================
    // THE SERVICE: VALUES READ AS MEANT, FAULTS NAMED
    // =========================================================================

    [Theory]
    [InlineData("""{"liters":"2","urgent":true,"ratio":1.5}""", "liters", "2")]
    [InlineData("""{"liters":"2.0","urgent":true,"ratio":1.5}""", "liters", "2")]
    [InlineData("""{"liters":2.0,"urgent":true,"ratio":1.5}""", "liters", "2")]
    [InlineData("""{"liters":2,"urgent":"true","ratio":1.5}""", "urgent", "true")]
    [InlineData("""{"liters":2,"urgent":" False ","ratio":1.5}""", "urgent", "false")]
    [InlineData("""{"liters":2,"urgent":true,"ratio":"2.5"}""", "ratio", "2.5")]
    public void CallSignature_Value_in_another_form_of_its_elementary_type_is_read_as_that_type(string arguments, string parameter, string readValue)
    {
        FunctionInvocationContext context = ContextOf(Water, arguments);

        // The value is rewritten in the form that the parameter takes and reported as rewritten.
        Assert.Equal([parameter], CallSignatureService.NormalizeArguments(context));
        Assert.Equal(readValue, ((JsonElement)context.Arguments[parameter]!).GetRawText());

        // The rewritten call breaks nothing, so it reaches the tool.
        Assert.Null(CallSignatureService.Fault(context));
    }

    [Theory]
    [InlineData("""{"liters":"2.5","urgent":true,"ratio":1.5}""", "the call passes 'liters' as string while 'Water' takes it as integer")]
    [InlineData("""{"liters":2.5,"urgent":true,"ratio":1.5}""", "the call passes 'liters' as number while 'Water' takes it as integer")]
    [InlineData("""{"liters":2,"urgent":"yes","ratio":1.5}""", "the call passes 'urgent' as string while 'Water' takes it as boolean")]
    [InlineData("""{"liters":2,"urgent":true,"ratio":"plenty"}""", "the call passes 'ratio' as string while 'Water' takes it as number")]
    [InlineData("""{"liters":2,"urgent":1,"ratio":1.5}""", "the call passes 'urgent' as number while 'Water' takes it as boolean")]
    [InlineData("""{"liters":2,"ratio":1.5}""", "the call leaves out 'urgent', which 'Water' requires")]
    public void CallSignature_Value_with_no_lossless_reading_is_refused_naming_the_argument(string arguments, string fault)
    {
        FunctionInvocationContext context = ContextOf(Water, arguments);

        // Nothing is guessed: a fraction is never cut, a word is never taken for a boolean, a number never for a word.
        Assert.Empty(CallSignatureService.NormalizeArguments(context));

        // The fault names the argument and what is wrong with it.
        Assert.Equal(fault, CallSignatureService.Fault(context));
    }

    [Fact]
    public void CallSignature_Value_outside_those_that_its_parameter_lists_is_refused_naming_the_values_it_takes()
    {
        FunctionInvocationContext context = ContextOf(Sow, """{"season":"winter"}""");

        // A word outside the closed set that the parameter lists is refused naming the words that it takes.
        Assert.Equal("the call passes 'season' as 'winter' while 'Sow' takes one of: spring, autumn", CallSignatureService.Fault(context));
    }

    // =========================================================================
    // A CARELESS MODEL IN A WHOLE TURN
    // =========================================================================

    [Fact]
    public async Task CallSignature_Careless_Reply_carrying_its_values_in_another_form_closes_the_turn_at_once()
    {
        await using TurnClosureTests.AgentTurnUnderTest turn = await TurnClosureTests.AgentTurnUnderTest.CreateAsync(streaming: false);
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"The plan has seven clauses.","userIsLeaving":"False"}""")));

        List<object> received = await turn.AskAsync(new Records.AgentRequest("conv-turn", "Which clauses are there?", null));

        // The boolean written as a word is the boolean itself, so the first call closes the turn with no round of repair.
        Assert.Equal("The plan has seven clauses.", received.OfType<Records.AgentResponse>().Single().Response);
        Assert.Single(turn.Model.Requests);
    }

    [Fact]
    public async Task CallSignature_Careless_Reply_breaking_its_schema_is_refused_naming_the_argument_at_fault()
    {
        await using TurnClosureTests.AgentTurnUnderTest turn = await TurnClosureTests.AgentTurnUnderTest.CreateAsync(streaming: false);
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"The plan has seven clauses.","userIsLeaving":"no"}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"The plan has seven clauses."}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"The plan has seven clauses.","userIsLeaving":false}""")));

        List<object> received = await turn.AskAsync(new Records.AgentRequest("conv-turn", "Which clauses are there?", null));

        // Text that is no boolean is refused with the argument, the type passed and the type that Reply takes.
        Assert.Equal("[CALL NOT ACCEPTED] the call passes 'userIsLeaving' as string while 'Reply' takes it as boolean", FirstLine(LastResultText(turn.Model.Requests[1])));

        // A required argument left out is refused naming it.
        Assert.Equal("[CALL NOT ACCEPTED] the call leaves out 'userIsLeaving', which 'Reply' requires", FirstLine(LastResultText(turn.Model.Requests[2])));

        // The repaired call closes the turn with the model's own declaration.
        Assert.Equal("The plan has seven clauses.", received.OfType<Records.AgentResponse>().Single().Response);
        Assert.Equal(3, turn.Model.Requests.Count);
    }

    [Fact]
    public async Task CallSignature_Careless_domain_call_breaking_its_schema_never_reaches_the_tool()
    {
        await using TurnClosureTests.AgentTurnUnderTest turn = await TurnClosureTests.AgentTurnUnderTest.CreateAsync(streaming: false);
        turn.Model.Enqueue(Answer(Call("Stock", """{"item":12}""")));
        turn.Model.Enqueue(Answer(Call("Stock", """{"item":"tulip"}""")));
        turn.Model.Enqueue(Answer(Call("Reply", """{"text":"There are 12 tulips.","userIsLeaving":false}""")));

        await turn.AskAsync(new Records.AgentRequest("conv-turn", "How many tulips?", null));

        // A number meant for a text parameter has no lossless reading as that text, so it stops before the tool.
        Assert.Equal("[CALL NOT ACCEPTED] the call passes 'item' as number while 'Stock' takes it as string", FirstLine(LastResultText(turn.Model.Requests[1])));

        // The tool runs once, on the repaired call.
        Assert.Equal(["Stock:tulip"], WorkflowTests.InventoryTools.Calls);
        Assert.Equal(3, turn.Model.Requests.Count);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>The call of a tool about to run, with its arguments as a model sends them.</summary>
    private static FunctionInvocationContext ContextOf(AIFunction function, string arguments)
        => new FunctionInvocationContext { Function = function, Arguments = Arguments(arguments) };

    /// <summary>Arguments as a model sends them: one JSON value per parameter.</summary>
    private static AIFunctionArguments Arguments(string json)
        => new AIFunctionArguments(JsonDocument.Parse(json).RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

    /// <summary>One model answer calling a tool.</summary>
    private static ChatResponse Call(string name, string json)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, Arguments(json))]));

    /// <summary>One model answer made of the given calls.</summary>
    private static ChatResponse Answer(params ChatResponse[] calls)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. calls.SelectMany(call => call.Messages[0].Contents)]));

    /// <summary>The text of the last tool result that one request handed the model: the answer to the call just made.</summary>
    private static string LastResultText(ModelRequest request)
        => request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => result.Result is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString()! : result.Result?.ToString() ?? string.Empty)
            .Last();

    /// <summary>The first line of a refusal, which carries the fact; the rest is the template's own sentence.</summary>
    private static string FirstLine(string text) => text.Split('\n')[0];
}
