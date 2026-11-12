using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Examples.Data;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using static Examples.Data.GardeningDatabaseHelper;

namespace GardeningMCP;

/// <summary>The gardener's almanac served over MCP, answering only for the plants of the nursery's catalogue that the almanac covers.</summary>
[McpServerToolType]
internal sealed class GardeningTools
{
    /// <summary>Lists the plants that the almanac covers.</summary>
    [McpServerTool(Name = "ListPlants", ReadOnly = true, OpenWorld = false)]
    [Description("Lists the plants of The Greenhouse & Nursery that the almanac covers as structured JSON: totalPlants and an array of plants with sku, name, category and alsoKnownAs, the other names that people call the plant by. The almanac holds advice on these plants and on no other.")]
    public async Task<string> ListPlants()
    {
        // The catalogue is the plugin's live system of record so it is read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // The plants of the catalogue that have advice in the almanac.
        List<CoveredPlant> coveredPlants = await ReadCoveredPlantsAsync(connection);

        // The listing the model chooses a plant from.
        return JsonSerializer.Serialize(new { totalPlants = coveredPlants.Count, plants = coveredPlants }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Returns the almanac's page for one plant found from the name that the customer uses for it.</summary>
    [McpServerTool(Name = "GetPlantPage", ReadOnly = true, OpenWorld = false)]
    [Description("Returns the almanac's page for one plant, found from the name that the customer uses for it, as structured JSON: sku, name, category, alsoKnownAs, imageUrl (a photo of the plant, present only when the almanac holds one), care (light, water, soil, temperature, feeding and pruning), problems (each with name, symptoms and remedy, covering the troubles that come from care as well as pests and diseases) and calendar (each with month, monthName and task). A name that fits several plants returns them as candidates instead; a name that fits none is reported together with the list of the almanac's plants.")]
    public async Task<string> GetPlantPage(
        [Description("The plant in the customer's own words: a common name, a botanical name or a plural all work (e.g. 'my roses', 'moth orchid', 'sansevieria'); a sku works too.")] string plant)
    {
        // The catalogue is the plugin's live system of record so it is read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // The plants that the almanac covers, which the words of the customer are matched against.
        List<CoveredPlant> coveredPlants = await ReadCoveredPlantsAsync(connection);

        // The plants that the customer's words name.
        List<CoveredPlant> matchingPlants = FindPlantsNamed(coveredPlants, plant);

        // No plant fits: the model gets the plants of the almanac to choose from instead of a bare refusal.
        if (matchingPlants.Count == 0)
            return JsonSerializer.Serialize(new { error = "The almanac has no page for this plant", requestedPlant = plant, plants = coveredPlants }, ExamplesDatabaseHelper.JsonOptions);

        // Several plants fit: the customer is to say which one is meant.
        if (matchingPlants.Count > 1)
            return JsonSerializer.Serialize(new { requestedPlant = plant, candidates = matchingPlants }, ExamplesDatabaseHelper.JsonOptions);

        // One plant fits: its whole page answers the question.
        return JsonSerializer.Serialize(await ReadPlantPageAsync(connection, matchingPlants[0]), ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Returns what the plants of the almanac need in one month of the year.</summary>
    [McpServerTool(Name = "GetSeasonalTasks", ReadOnly = true, OpenWorld = false)]
    [Description("Returns what the plants of the almanac need in one month of the year as structured JSON: month, monthName and an array of tasks, each with the sku and name of its plant and the task. The calendar is written for a temperate climate of the northern hemisphere.")]
    public async Task<string> GetSeasonalTasks(
        [Description("The month as a number from 1 (January) to 12 (December). Omitted, the current month is used.")] int? month = null)
    {
        // The server's own clock stands for the present when the month is not given.
        int monthOfYear = month ?? DateTime.Today.Month;

        // A month outside the calendar has no tasks to report.
        if (monthOfYear < 1 || monthOfYear > 12)
            return JsonSerializer.Serialize(new { error = "The month must be a number from 1 to 12", requestedMonth = month }, ExamplesDatabaseHelper.JsonOptions);

        // The calendar is read from the plugin's live system of record.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // The tasks of the month for the plants that the almanac covers.
        List<SeasonalTask> seasonalTasks = await ReadSeasonalTasksAsync(connection, monthOfYear);

        // The month with its name so the model never has to translate the number.
        return JsonSerializer.Serialize(new
        {
            month = monthOfYear,
            monthName = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(monthOfYear),
            tasks = seasonalTasks
        }, ExamplesDatabaseHelper.JsonOptions);
    }
}