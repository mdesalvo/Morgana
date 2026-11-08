using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Examples.Data;

/// <summary>The reads of the gardener's almanac, the four tables grafted onto the catalogue by SKU.</summary>
internal static class GardeningDatabaseHelper
{
    /// <summary>The words that carry no name of a plant and are dropped from the customer's words and from every name.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "my", "the", "a", "an", "our", "your", "some", "plant" };

    /// <summary>Splits a text on every character that is neither a letter nor a digit.</summary>
    private static readonly Regex WordSeparator = new Regex("[^\\p{L}\\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A plant of the catalogue that has a care sheet together with the other names that people call it by.</summary>
    internal sealed record CoveredPlant(string Sku, string Name, string Category, IReadOnlyList<string> AlsoKnownAs);

    /// <summary>The whole page of the almanac for one plant: its names, its care, its problems and its calendar.</summary>
    internal sealed record PlantPage(string Sku, string Name, string Category, IReadOnlyList<string> AlsoKnownAs, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl, PlantCare Care, IReadOnlyList<PlantProblem> Problems, IReadOnlyList<MonthlyTask> Calendar);

    /// <summary>How one plant is grown.</summary>
    internal sealed record PlantCare(string Light, string Water, string Soil, string Temperature, string Feeding, string Pruning);

    /// <summary>One trouble of a plant with the remedy that the almanac gives: a pest or disease as well as a trouble that comes from care.</summary>
    internal sealed record PlantProblem(string Name, string Symptoms, string Remedy);

    /// <summary>One task that a plant needs in a month of the year.</summary>
    internal sealed record MonthlyTask(int Month, string MonthName, string Task);

    /// <summary>One task that a plant needs in a month of the year together with the plant that needs it.</summary>
    internal sealed record SeasonalTask(string Sku, string Name, string Task);

    /// <summary>Reads the plants of the catalogue that have a care sheet in the almanac with their other names.</summary>
    internal static async Task<List<CoveredPlant>> ReadCoveredPlantsAsync(SqliteConnection connection)
    {
        // A plant is covered by the almanac when it has a care sheet; the plants sold dried have none.
        // The outer join keeps a plant that has no alias at all.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Sku, p.Name, p.Category, a.Alias
            FROM Products p JOIN PlantCareSheets c ON c.Sku = p.Sku LEFT JOIN PlantAliases a ON a.Sku = p.Sku
            ORDER BY p.Sku, a.Alias
            """;

        // One row per alias: the first row of a plant opens its entry and the following rows fill its alias list.
        List<CoveredPlant> coveredPlants = [];
        Dictionary<string, List<string>> aliasesBySku = new(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // The SKU tells which plant this row belongs to.
            string sku = reader.GetString(0);

            // The first row of a plant opens its entry.
            if (!aliasesBySku.TryGetValue(sku, out List<string>? aliases))
            {
                // The plant starts with no alias and is filed under its SKU for the rows that follow.
                aliases = [];
                aliasesBySku[sku] = aliases;

                // The plant joins the list in the SKU order of the query.
                coveredPlants.Add(new CoveredPlant(sku, reader.GetString(1), reader.GetString(2), aliases));
            }

            // The outer join gives a plant without aliases a single row with no alias.
            if (!reader.IsDBNull(3))
                aliases.Add(reader.GetString(3));
        }

        // The plants that the almanac covers in SKU order.
        return coveredPlants;
    }

    /// <summary>Finds the covered plants that the customer's words name: a SKU alone or every plant whose name or alias holds all the words.</summary>
    internal static List<CoveredPlant> FindPlantsNamed(IReadOnlyList<CoveredPlant> coveredPlants, string plant)
    {
        // A SKU names its plant exactly whatever its casing.
        string trimmedPlant = plant.Trim();
        CoveredPlant? plantWithSku = coveredPlants.FirstOrDefault(coveredPlant => string.Equals(coveredPlant.Sku, trimmedPlant, StringComparison.OrdinalIgnoreCase));
        if (plantWithSku is not null)
            return [plantWithSku];

        // A request with no word left after the normalisation names nothing.
        HashSet<string> requestWords = NormaliseWords(trimmedPlant);
        if (requestWords.Count == 0)
            return [];

        // A plant matches when its name or one of its aliases holds every word of the request.
        List<CoveredPlant> matchingPlants = [];
        foreach (CoveredPlant coveredPlant in coveredPlants)
        {
            // The names that people call this plant by: the one in the catalogue and the aliases.
            List<string> namesOfPlant = [coveredPlant.Name, .. coveredPlant.AlsoKnownAs];
            if (namesOfPlant.Any(nameOfPlant => requestWords.IsSubsetOf(NormaliseWords(nameOfPlant))))
                matchingPlants.Add(coveredPlant);
        }

        // The matching plants keep the SKU order of the covered plants.
        return matchingPlants;
    }

    /// <summary>Reduces a text to its meaningful words in singular form.</summary>
    private static HashSet<string> NormaliseWords(string text)
    {
        // The same rule on the request and on every name makes "roses" find "Red Rose Bush" and keeps "rose" from finding "Rosemary Topiary".
        HashSet<string> words = new(StringComparer.Ordinal);
        foreach (string word in WordSeparator.Split(text.ToLowerInvariant()))
        {
            // A plural is brought back to its singular so that "orchids" and "orchid" are the same word.
            string singularWord = word;
            if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
                singularWord = word[..^3] + "y";
            else if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
                singularWord = word[..^1];

            // Single characters and the words that carry no name are dropped.
            if (singularWord.Length < 2 || StopWords.Contains(singularWord))
                continue;

            // The word that helps to tell one plant from another.
            words.Add(singularWord);
        }

        // The words of the text.
        return words;
    }

    /// <summary>Reads the whole page of one plant: its care sheet and its problems and its calendar.</summary>
    internal static async Task<PlantPage> ReadPlantPageAsync(SqliteConnection connection, CoveredPlant coveredPlant)
    {
        // The care sheet and the photo of the plant, both filled by the one read below.
        PlantCare plantCare;
        string? imageUrl;
        await using (SqliteCommand careCommand = connection.CreateCommand())
        {
            // The care sheet comes with the photo that the catalogue holds for the same plant.
            careCommand.CommandText = "SELECT c.Light, c.Water, c.Soil, c.Temperature, c.Feeding, c.Pruning, p.ImageUrl FROM PlantCareSheets c JOIN Products p ON p.Sku = c.Sku WHERE c.Sku = $sku";
            careCommand.Parameters.AddWithValue("$sku", coveredPlant.Sku);

            // Every covered plant has a care sheet so a missing row means that the seed is broken.
            await using SqliteDataReader careReader = await careCommand.ExecuteReaderAsync();
            if (!await careReader.ReadAsync())
                throw new InvalidOperationException($"The plant '{coveredPlant.Sku}' is covered by the almanac but has no care sheet.");

            // The six fields of the care as the almanac writes them.
            plantCare = new PlantCare(
                careReader.GetString(0), careReader.GetString(1), careReader.GetString(2),
                careReader.GetString(3), careReader.GetString(4), careReader.GetString(5));

            // The photo of the plant: only some plants of the catalogue have one.
            imageUrl = careReader.IsDBNull(6) ? null : careReader.GetString(6);
        }

        // The problems of the plant in the order that the almanac writes them.
        List<PlantProblem> problems = [];
        await using (SqliteCommand problemsCommand = connection.CreateCommand())
        {
            // The problems are filed under the plant's SKU and numbered in the almanac's order.
            problemsCommand.CommandText = "SELECT Name, Symptoms, Remedy FROM PlantProblems WHERE Sku = $sku ORDER BY Position";
            problemsCommand.Parameters.AddWithValue("$sku", coveredPlant.Sku);

            // Each row is one trouble with its symptoms and remedy.
            await using SqliteDataReader problemsReader = await problemsCommand.ExecuteReaderAsync();
            while (await problemsReader.ReadAsync())
                problems.Add(new PlantProblem(problemsReader.GetString(0), problemsReader.GetString(1), problemsReader.GetString(2)));
        }

        // The calendar of the plant from January to December.
        List<MonthlyTask> calendar = [];
        await using (SqliteCommand calendarCommand = connection.CreateCommand())
        {
            // The plant holds at most one task per month, so the months come out in calendar order.
            calendarCommand.CommandText = "SELECT Month, Task FROM PlantSeasonalTasks WHERE Sku = $sku ORDER BY Month";
            calendarCommand.Parameters.AddWithValue("$sku", coveredPlant.Sku);

            // Each row is one task with the name of its month so that the model never has to translate the number.
            await using SqliteDataReader calendarReader = await calendarCommand.ExecuteReaderAsync();
            while (await calendarReader.ReadAsync())
            {
                // The month as the almanac numbers it, from 1 for January.
                int month = calendarReader.GetInt32(0);

                // The task travels with the name of its month.
                calendar.Add(new MonthlyTask(month, CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month), calendarReader.GetString(1)));
            }
        }

        // The page that the customer's question is answered from.
        return new PlantPage(coveredPlant.Sku, coveredPlant.Name, coveredPlant.Category, coveredPlant.AlsoKnownAs, imageUrl, plantCare, problems, calendar);
    }

    /// <summary>Reads the tasks that the plants of the almanac need in one month of the year.</summary>
    internal static async Task<List<SeasonalTask>> ReadSeasonalTasksAsync(SqliteConnection connection, int month)
    {
        // Only the plants that the almanac covers are listed, in SKU order.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Sku, p.Name, t.Task
            FROM PlantSeasonalTasks t JOIN Products p ON p.Sku = t.Sku JOIN PlantCareSheets c ON c.Sku = p.Sku
            WHERE t.Month = $month
            ORDER BY p.Sku
            """;
        command.Parameters.AddWithValue("$month", month);

        // Each row is a task together with the plant that needs it.
        List<SeasonalTask> seasonalTasks = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            seasonalTasks.Add(new SeasonalTask(reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        // The tasks of the month.
        return seasonalTasks;
    }
}
