using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Checks the wiring itself before any scenario is believed: host up, handshake accepted, webhook delivered, observers wired and the gardening server answering.
/// A broken observer reads exactly like a prompt regression, which is why the suite starts here whenever it fails wholesale.
/// </summary>
public sealed class HarnessSmokeTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host.</summary>
    public HarnessSmokeTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Conversation_opens_and_Morgana_presents_itself()
    {
        // The start call, the webhook delivery and the welcome read off it, with no scenario logic on top:
        // the smallest proof that the rig is alive.
        (string conversationId, ChannelMessage presentation) =
            await fixture.Channel.StartConversationAsync(TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds));

        try
        {
            // A welcome with words and the intents' buttons, whatever its wording.
            Assert.False(string.IsNullOrWhiteSpace(presentation.Text), "The presentation carried no text.");
            Assert.NotEmpty(presentation.QuickReplies ?? []);
        }
        finally
        {
            // The conversation is ended whatever the verdict, so its actors do not outlive the test.
            await fixture.Channel.EndConversationAsync(conversationId);
        }
    }

    [Fact]
    public async Task Structural_observers_see_the_turn()
    {
        // A fresh conversation, given the turn timeout the scenarios run with.
        TimeSpan timeout = TimeSpan.FromSeconds(fixture.Options.TurnTimeoutSeconds);
        (string conversationId, ChannelMessage _) = await fixture.Channel.StartConversationAsync(timeout);

        try
        {
            // The same sequence ScenarioRunner uses for every scripted turn, proving the observers see a turn
            // independently of any scenario's own assertions.
            TurnScope scope = fixture.Observer.BeginTurn(conversationId);
            ChannelMessage message = await fixture.Channel.SendAsync(conversationId, "I'd like to see my invoices", timeout);
            TurnResult turn = await fixture.Observer.CompleteTurnAsync(scope, "I'd like to see my invoices", message);

            // The span observer: an agent handled the turn and its name reached the listener.
            Assert.False(string.IsNullOrWhiteSpace(turn.AgentName),
                $"No morgana.agent span was observed for the turn.\n{turn.Describe()}");

            // The log observer: a turn always produces host log lines. Asserting on the lines rather than on parsed
            // context accesses keeps this a test of the rig, since a skipped lookup is the context group's verdict.
            Assert.NotEmpty(turn.LogLines);

            // The model observer: every call of the turn carries what the model was handed and answered,
            // or a failing run would be left with nothing but its words to be diagnosed from.
            Assert.NotEmpty(turn.Calls);
            Assert.All(turn.Calls, call => Assert.False(string.IsNullOrEmpty(call.InputMessages), "A model call was observed without its input messages."));
            Assert.Contains(turn.Calls, call => !string.IsNullOrEmpty(call.Instructions));
        }
        finally
        {
            // The conversation is ended whatever the verdict, so its actors do not outlive the test.
            await fixture.Channel.EndConversationAsync(conversationId);
        }
    }

    [Fact]
    public void Every_scenario_file_parses()
    {
        // A parse and shape check that reaches no host, so a malformed file or a mismatched id costs nothing
        // here instead of failing a billed run later.
        string[] files = Directory.GetFiles(ScenarioLoader.Directory, "*.yaml");
        Assert.NotEmpty(files);

        // Each file must load under its own name and script at least one turn.
        foreach (string file in files)
        {
            ScenarioDefinition scenario = ScenarioLoader.Load(Path.GetFileNameWithoutExtension(file));

            Assert.Equal(Path.GetFileNameWithoutExtension(file), scenario.Id);
            Assert.NotEmpty(scenario.Turns);
        }
    }

    /// <summary>Checks that the gardening server offers its three tools and covers exactly the plants of the almanac with names of their own.</summary>
    [Fact]
    public async Task Gardening_server_offers_its_three_tools_and_the_covered_plants()
    {
        // The shipped domain is served only when the run replaces nothing.
        Assert.NotNull(fixture.GardeningServer);

        // The client speaks to the server the way an agent of the instance under test does.
        await using McpClient client = await OpenGardeningClientAsync();

        // The tools that the server offers, in a fixed order for the comparison.
        IList<McpClientTool> offeredTools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        string[] offeredToolNames = [.. offeredTools.Select(tool => tool.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["GetPlantPage", "GetSeasonalTasks", "ListPlants"], offeredToolNames);

        // The plants that the server covers.
        JsonElement plantListing = await CallGardeningToolAsync(client, "ListPlants");

        // The SKUs in a fixed order for the comparison.
        string[] listedSkus = [.. plantListing.GetProperty("plants").EnumerateArray().Select(plant => plant.GetProperty("sku").GetString() ?? string.Empty).Order(StringComparer.Ordinal)];

        // An almanac page whose SKU is misspelt silently takes its plant out of the agent's perimeter.
        string[] expectedSkus =
        [
            "RSE-100", "HYD-101", "CAM-102", "LIL-103", "ORC-200", "BOP-201", "HIB-202", "PAS-203", "ROS-301", "MNT-302", "BAS-303", "LAVP-305",
            "SEED-400", "DAF-401", "SUNF-402", "CROC-403", "TUL-500", "FERN-600", "MONS-601", "POTH-602", "SNKP-603", "ECHV-700",
            "CACT-701", "ALOE-702"
        ];
        Assert.Equal([.. expectedSkus.Order(StringComparer.Ordinal)], listedSkus);

        // An alias shared by two plants would send the customer's words to the wrong page.
        HashSet<string> seenAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement listedPlant in plantListing.GetProperty("plants").EnumerateArray())
        {
            foreach (JsonElement alias in listedPlant.GetProperty("alsoKnownAs").EnumerateArray())
            {
                // The alias that was seen before belongs to another plant.
                string aliasText = alias.GetString() ?? string.Empty;
                Assert.True(seenAliases.Add(aliasText), $"The alias '{aliasText}' appears in the alsoKnownAs of two plants.");
            }
        }
    }

    /// <summary>Checks that the customer's own words find their plant in the almanac.</summary>
    /// <param name="plant">The plant as the customer writes it.</param>
    /// <param name="expectedSku">The SKU of the plant that the words must lead to.</param>
    [Theory]
    [InlineData("my roses", "RSE-100")]
    [InlineData("moth orchid", "ORC-200")]
    [InlineData("sansevieria", "SNKP-603")]
    [InlineData("mint", "MNT-302")]
    [InlineData("rosemary", "ROS-301")]
    [InlineData("strelitzia", "BOP-201")]
    [InlineData("swiss cheese plant", "MONS-601")]
    [InlineData("cacti", "CACT-701")]
    [InlineData("lavender", "LAVP-305")]
    [InlineData("hyd-101", "HYD-101")]
    public async Task Gardening_server_finds_the_plant_from_the_customer_s_words(string plant, string expectedSku)
    {
        // The shipped domain is served only when the run replaces nothing.
        Assert.NotNull(fixture.GardeningServer);

        // The customer's words are the entrance to the almanac and "rosemary" guards against "rose" finding the wrong plant: the cases are spelled out.
        await using McpClient client = await OpenGardeningClientAsync();
        JsonElement plantPage = await CallGardeningToolAsync(client, "GetPlantPage", new Dictionary<string, object?> { ["plant"] = plant });

        // A page is the answer: no error and no candidates to choose from.
        Assert.False(plantPage.TryGetProperty("error", out _), $"GetPlantPage answered with an error for '{plant}'.");
        Assert.False(plantPage.TryGetProperty("candidates", out _), $"GetPlantPage answered with candidates for '{plant}'.");

        // The page is the one of the plant that the words name.
        Assert.Equal(expectedSku, plantPage.GetProperty("sku").GetString());
    }

    /// <summary>Checks that a plant outside the almanac is reported instead of being answered.</summary>
    /// <param name="plant">A plant that the almanac does not cover.</param>
    [Theory]
    [InlineData("Burundi silver baobab")]
    [InlineData("sage")]
    public async Task Gardening_server_has_no_page_for_a_plant_outside_the_almanac(string plant)
    {
        // The shipped domain is served only when the run replaces nothing.
        Assert.NotNull(fixture.GardeningServer);

        // Sage is sold dried and has no page so it is as unknown as a plant that nobody sells.
        await using McpClient client = await OpenGardeningClientAsync();
        JsonElement plantPage = await CallGardeningToolAsync(client, "GetPlantPage", new Dictionary<string, object?> { ["plant"] = plant });

        // The refusal names itself as an error.
        Assert.True(plantPage.TryGetProperty("error", out _), $"GetPlantPage did not report an error for '{plant}'.");
    }

    /// <summary>Checks that the gardening server answers in full for every plant that it lists and for every month of the year.</summary>
    [Fact]
    public async Task Gardening_server_answers_for_every_covered_plant_and_month()
    {
        // The shipped domain is served only when the run replaces nothing.
        Assert.NotNull(fixture.GardeningServer);

        // The client speaks to the server the way an agent of the instance under test does.
        await using McpClient client = await OpenGardeningClientAsync();

        // The plants that the server says it covers.
        JsonElement plantListing = await CallGardeningToolAsync(client, "ListPlants");
        string[] listedSkus = [.. plantListing.GetProperty("plants").EnumerateArray().Select(plant => plant.GetProperty("sku").GetString() ?? string.Empty)];

        // The photos live in Examples/Data/Images and are served from the repository's main branch.
        string[] photographedSkus = ["ALOE-702", "BOP-201", "CAM-102", "HYD-101", "LAVP-305", "MONS-601", "ORC-200", "RSE-100", "SUNF-402", "TUL-500"];

        // Every plant of the listing must have a page that diagnoses a wilting plant or a yellow leaf.
        foreach (string listedSku in listedSkus)
        {
            // The page is advice and never an error.
            JsonElement plantPage = await CallGardeningToolAsync(client, "GetPlantPage", new Dictionary<string, object?> { ["plant"] = listedSku });
            Assert.False(plantPage.TryGetProperty("error", out _), $"GetPlantPage answered with an error for {listedSku}.");

            // The symptoms of the plant's problems, which the customer's description is read against.
            JsonElement problems = plantPage.GetProperty("problems");
            string[] symptoms = [.. problems.EnumerateArray().Select(problem => problem.GetProperty("symptoms").GetString() ?? string.Empty)];

            // A plant with few problems leaves the gardener with nothing to say about the commonest complaints.
            Assert.True(problems.GetArrayLength() >= 5, $"GetPlantPage listed fewer than five problems for {listedSku}.");

            // A wilting plant must find its cause in the page.
            Assert.True(symptoms.Any(symptom => symptom.Contains("wilt", StringComparison.OrdinalIgnoreCase) || symptom.Contains("droop", StringComparison.OrdinalIgnoreCase)), $"No problem of {listedSku} describes a wilting or drooping plant.");

            // A yellow leaf must find its cause in the page.
            Assert.True(symptoms.Any(symptom => symptom.Contains("yellow", StringComparison.OrdinalIgnoreCase)), $"No problem of {listedSku} describes yellow leaves.");

            // A plant without a calendar leaves the gardener with nothing to say about the year.
            Assert.True(plantPage.GetProperty("calendar").GetArrayLength() > 0, $"GetPlantPage listed no calendar for {listedSku}.");

            // A photographed plant carries the address of its photo and every other plant carries none.
            if (photographedSkus.Contains(listedSku))
            {
                // The address is the one of the photo in the repository.
                Assert.Equal($"https://raw.githubusercontent.com/mdesalvo/Morgana/main/Examples/Data/Images/{listedSku}.jpg", plantPage.GetProperty("imageUrl").GetString());
            }
            else
            {
                // A plant without a photo has no imageUrl at all.
                Assert.False(plantPage.TryGetProperty("imageUrl", out _), $"GetPlantPage carried an imageUrl for {listedSku}, which has no photo.");
            }
        }

        // The plants that the twelve months mention, to be compared with the listing.
        HashSet<string> skusWithTasks = new HashSet<string>(StringComparer.Ordinal);

        // Every month of the year must have something to do.
        for (int month = 1; month <= 12; month++)
        {
            // The tasks of the month are advice and never an error.
            JsonElement seasonalTasks = await CallGardeningToolAsync(client, "GetSeasonalTasks", new Dictionary<string, object?> { ["month"] = month });
            Assert.False(seasonalTasks.TryGetProperty("error", out _), $"GetSeasonalTasks answered with an error for month {month}.");

            // A month without a task would leave the gardener with nothing to say about it.
            Assert.True(seasonalTasks.GetProperty("tasks").GetArrayLength() > 0, $"GetSeasonalTasks listed no task for month {month}.");

            // The plants that this month mentions join the ones seen so far.
            foreach (JsonElement seasonalTask in seasonalTasks.GetProperty("tasks").EnumerateArray())
                skusWithTasks.Add(seasonalTask.GetProperty("sku").GetString() ?? string.Empty);
        }

        // A plant that no month mentions has a calendar that never reaches the user.
        Assert.Equal([.. listedSkus.Order(StringComparer.Ordinal)], [.. skusWithTasks.Order(StringComparer.Ordinal)]);
    }

    /// <summary>Opens a client on the gardening server at the address that the shipped agent names.</summary>
    private static async Task<McpClient> OpenGardeningClientAsync()
    {
        // The server is reached over HTTP at the address that GardeningAgent names.
        HttpClientTransport transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(GardeningServerHost.Address),
            Name = "gardening"
        });

        // The handshake runs here so that an unreachable server fails the test at the door.
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>Calls one tool of the gardening server and parses the JSON text that it answers with.</summary>
    private static async Task<JsonElement> CallGardeningToolAsync(McpClient client, string toolName, Dictionary<string, object?>? toolArguments = null)
    {
        // The tool is asked for its advice with the arguments that the test gives.
        CallToolResult toolResult = await client.CallToolAsync(toolName, toolArguments);

        // The server answers with one text block holding the JSON of the advice.
        string answerText = toolResult.Content.OfType<TextContentBlock>().Single().Text;

        // The caller reads the advice after the parsed document is released, so it is handed a copy.
        using JsonDocument answerDocument = JsonDocument.Parse(answerText);
        return answerDocument.RootElement.Clone();
    }
}