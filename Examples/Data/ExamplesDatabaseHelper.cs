using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Examples.Data;

/// <summary>The single system of record of The Greenhouse &amp; Nursery, shared by every agent of this plugin and by the GardeningMCP server: where it lives, how its seed is deployed and how it is opened.</summary>
/// <remarks>It is independent of Morgana's per-conversation persistence: the catalogue, the orders, the ledgers and the almanac survive restarts and cross conversations.</remarks>
internal static class ExamplesDatabaseHelper
{
    // Serialises the deployment of the seed so that two conversations starting together never both create the database.
    private static readonly object InitLock = new();

    // The path that was actually deployed, not a bare bool: a process that changes StoragePath (the PromptHarness gives every run its own
    // throwaway directory) must deploy the seed again rather than skip it and then query a database that was never put there.
    private static string? _deployedPath;

    // The directory that holds the database: the StoragePath that Morgana publishes or the plugin's own folder when none is set.
    private static string StorageDirectory
    {
        get
        {
            // StoragePath is read from the environment because the plugin has no access to Morgana's configuration.
            string? storageDirectory = Environment.GetEnvironmentVariable("Morgana__ConversationPersistence__StoragePath");

            // A published StoragePath wins and without one the database sits beside the plugin.
            return string.IsNullOrWhiteSpace(storageDirectory)
                ? AppContext.BaseDirectory
                : storageDirectory;
        }
    }

    // The file of the system of record inside the storage directory.
    private static string DbPath => Path.Combine(StorageDirectory, "Examples.sqlite");

    // How every tool of the plugin and the GardeningMCP server reach the one database file.
    private static string ConnectionString => $"Data Source={DbPath}";

    // The shape in which the tools hand JSON to the model: compact and camelCase.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Deploys the embedded seed database to <see cref="DbPath"/> the first time any tool of this plugin asks for it and rebases its calendar onto the current month.</summary>
    internal static void Ensure()
    {
        // The physical location that this activation is deployed against.
        string databasePath = DbPath;

        // Every later activation (any conversation, process restart or container recreation that keeps the volume) finds the database deployed and does nothing.
        if (_deployedPath == databasePath)
            return;

        // Two activations starting together are let through one at a time.
        lock (InitLock)
        {
            // Another activation may have deployed the database while this one waited for the lock.
            if (_deployedPath == databasePath)
                return;

            // File.Exists is the entire "have we seeded yet" check with no flag or marker row: whatever the file holds
            // (seed data or years of live orders on top of it) is the truth and is never overwritten because the process restarted.
            if (!File.Exists(databasePath))
            {
                // A fresh volume may not have the storage directory yet.
                Directory.CreateDirectory(StorageDirectory);

                // Seeded under a private temporary name and only then moved into place, so a half-written
                // or half-rebased file can never become the shop's system of record.
                string stagingPath = Path.Combine(StorageDirectory, $"Examples.{Guid.NewGuid():N}.tmp");
                try
                {
                    // The seed travels inside this assembly and a missing resource means a faulty build.
                    using (Stream seedStream = typeof(ExamplesDatabaseHelper).Assembly.GetManifestResourceStream("Examples.Data.Examples.sqlite")
                        ?? throw new InvalidOperationException("Embedded seed database 'Examples.Data.Examples.sqlite' not found."))
                    using (FileStream fileStream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write))
                    {
                        // The staging file starts as an exact copy of the seed.
                        seedStream.CopyTo(fileStream);
                    }

                    // The seed's dates are brought to the current month on the staging file and never on live data.
                    RebaseCalendar(stagingPath);

                    // The move is the single instant at which the database starts existing and a race between two processes
                    // is decided: the loser deletes its own copy instead of overwriting the winner's.
                    File.Move(stagingPath, databasePath, overwrite: false);
                }
                catch (IOException) when (File.Exists(databasePath))
                {
                    // Another process got there first: its database is the real one.
                }
                finally
                {
                    // A staging file that was not moved into place leaves nothing behind.
                    if (File.Exists(stagingPath))
                        File.Delete(stagingPath);
                }
            }

            // From now on the data changes only through live orders, confirmations and cancellations.
            _deployedPath = databasePath;
        }
    }

    /// <summary>Shifts every stored date of the staged seed forward by the whole months elapsed since the seed was authored.</summary>
    /// <remarks>It runs exactly once on the staged copy before it becomes the database and never on live data.</remarks>
    private static void RebaseCalendar(string stagingPath)
    {
        // The staged copy is opened directly since it is not yet the system of record.
        using SqliteConnection connection = new SqliteConnection($"Data Source={stagingPath}");
        connection.Open();

        // The seed records the month it was authored in, which is where the shift starts from.
        DateTime anchorMonth;
        using (SqliteCommand readAnchor = connection.CreateCommand())
        {
            // The single anchor row holds the first day of the month in which the seed was authored.
            readAnchor.CommandText = "SELECT AnchorMonth FROM SeedInfo WHERE Id = 1";
            anchorMonth = DateTime.Parse((string)readAnchor.ExecuteScalar()!, CultureInfo.InvariantCulture);
        }

        // The month in which the shop is being opened.
        DateTime currentMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

        // The shop always opens on a plausible present: an invoice still due, a care plan with months left to run and orders placed within the last few weeks.
        int months = ((currentMonth.Year - anchorMonth.Year) * 12) + currentMonth.Month - anchorMonth.Month;

        // A seed authored in the current month or later needs no shift.
        if (months <= 0)
            return;

        // The shift is in whole months and preserves the day of the month, so the internal story of the seed
        // (this invoice bills that order, this plan started when that first invoice was issued) survives it intact.
        ShiftDates(connection, "Orders", "OrderId", ["CreatedAt", "ConfirmedAt", "CancelledAt"], months);
        ShiftDates(connection, "CarePlans", "ContractId", ["StartDate", "EndDate"], months);
        ShiftDates(connection, "PlanVisits", "VisitId", ["VisitDate"], months);
        ShiftDates(connection, "Invoices", "InvoiceId", ["PeriodStart", "PeriodEnd", "IssueDate", "DueDate", "PaidDate"], months);

        // The anchor moves to the current month so the seed is never rebased twice.
        using SqliteCommand writeAnchor = connection.CreateCommand();
        writeAnchor.CommandText = "UPDATE SeedInfo SET AnchorMonth = $anchorMonth WHERE Id = 1";
        writeAnchor.Parameters.AddWithValue("$anchorMonth", currentMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writeAnchor.ExecuteNonQuery();
    }

    /// <summary>Shifts the date columns of every row of one table of the staged seed by the given months.</summary>
    private static void ShiftDates(SqliteConnection connection, string table, string keyColumn, string[] dateColumns, int months)
    {
        // The rows are read completely before any is rewritten, so the reader never overlaps the writes.
        List<(string Key, string?[] Values)> rows = [];

        // The key and the date columns of every row of the table.
        using (SqliteCommand read = connection.CreateCommand())
        {
            // Only the key and the date columns are read since nothing else of the row changes.
            read.CommandText = $"SELECT {keyColumn}, {string.Join(", ", dateColumns)} FROM {table}";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                // One slot per date column.
                string?[] values = new string?[dateColumns.Length];

                // A date that the row does not have stays missing.
                for (int i = 0; i < dateColumns.Length; i++)
                    values[i] = reader.IsDBNull(i + 1) ? null : reader.GetString(i + 1);

                // The row is kept with its key for the rewrite below.
                rows.Add((reader.GetString(0), values));
            }
        }

        // Each row is rewritten with all its dates shifted.
        foreach ((string key, string?[] values) in rows)
        {
            // One update sets every date column of the row.
            using SqliteCommand write = connection.CreateCommand();
            write.CommandText = $"UPDATE {table} SET {string.Join(", ", dateColumns.Select(column => $"{column} = ${column}"))} WHERE {keyColumn} = $key";

            // Each date is shifted in the shape that it arrived in.
            for (int i = 0; i < dateColumns.Length; i++)
                write.Parameters.AddWithValue($"${dateColumns[i]}", (object?)Shift(values[i], months) ?? DBNull.Value);

            // The row is found again by its key.
            write.Parameters.AddWithValue("$key", key);
            write.ExecuteNonQuery();
        }
    }

    /// <summary>Shifts one stored date by the given months and keeps the shape in which it arrived.</summary>
    private static string? Shift(string? value, int months)
    {
        // A column without a date stays without one.
        if (string.IsNullOrEmpty(value))
            return null;

        // The stored date moved forward by the whole months.
        DateTime parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddMonths(months);

        // A stored date is either a plain calendar day (invoices and plans) or a full round-trip timestamp (order lifecycle):
        // it is written back in whichever of the two shapes it arrived in, so the file stays readable exactly as the seed wrote it.
        return value.Length == 10
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : parsed.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>Opens a connection to <see cref="DbPath"/> with a busy timeout applied.</summary>
    /// <remarks>Examples.sqlite is a shared live database where different conversations write at the same instant.</remarks>
    internal static async Task<SqliteConnection> OpenConnectionAsync()
    {
        // Every call gets its own connection since the conversations that use it run concurrently.
        SqliteConnection connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        // SQLite serialises writers with a single lock and without a busy timeout the loser of a race fails at once with "database is locked".
        // With it the loser waits for the holder to commit, which is what lets two concurrent commits of ConfirmOrder and CancelOrder queue up.
        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync();

        // The caller owns the open connection and disposes it.
        return connection;
    }
}
