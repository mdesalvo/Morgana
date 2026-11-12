using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Examples.Data;

/// <summary>
/// The single system of record of The Greenhouse &amp; Nursery, shared by every agent of this plugin and by the GardeningMCP server, with the write they share.
/// It is independent of Morgana's per-conversation persistence: the catalogue, the orders, the ledgers and the almanac survive restarts and cross conversations.
/// </summary>
internal static class ExamplesDatabaseHelper
{
    /// <summary>Serialises the deployment of the seed so that two conversations starting together never both create the database.</summary>
    private static readonly object InitLock = new();

    /// <summary>
    /// The path that was actually deployed, null until the first deployment.
    /// A process that changes StoragePath, as the PromptHarness does for every run, deploys the seed again rather than query a database never put there.
    /// </summary>
    private static string? deployedPath;

    /// <summary>The directory that holds the database: the StoragePath that Morgana publishes or the plugin's own folder when none is set.</summary>
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

    /// <summary>The file of the system of record inside the storage directory.</summary>
    private static string DbPath => Path.Combine(StorageDirectory, "Examples.sqlite");

    /// <summary>How every tool of the plugin and the GardeningMCP server reach the one database file.</summary>
    private static string ConnectionString => $"Data Source={DbPath}";

    /// <summary>The shape in which the tools hand JSON to the model: compact and camelCase.</summary>
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
        if (deployedPath == databasePath)
            return;

        // Two activations starting together are let through one at a time.
        lock (InitLock)
        {
            // Another activation may have deployed the database while this one waited for the lock.
            if (deployedPath == databasePath)
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
            deployedPath = databasePath;
        }
    }

    /// <summary>
    /// Shifts every stored date of the staged seed forward by the whole months elapsed since the seed was authored.
    /// It runs exactly once on the staged copy before it becomes the database and never on live data.
    /// </summary>
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
        int elapsedMonths = ((currentMonth.Year - anchorMonth.Year) * 12) + currentMonth.Month - anchorMonth.Month;

        // A seed authored in the current month or later needs no shift.
        if (elapsedMonths <= 0)
            return;

        // The shift is in whole months and preserves the day of the month, so the internal story of the seed
        // (this invoice bills that order, this plan started when that first invoice was issued) survives it intact.
        ShiftDates(connection, "Orders", "OrderId", ["CreatedAt", "ConfirmedAt", "CancelledAt"], elapsedMonths);
        ShiftDates(connection, "CarePlans", "ContractId", ["StartDate", "EndDate"], elapsedMonths);
        ShiftDates(connection, "PlanVisits", "VisitId", ["VisitDate"], elapsedMonths);
        ShiftDates(connection, "Invoices", "InvoiceId", ["PeriodStart", "PeriodEnd", "IssueDate", "DueDate", "PaidDate"], elapsedMonths);

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
        using (SqliteCommand readCommand = connection.CreateCommand())
        {
            // Only the key and the date columns are read since nothing else of the row changes.
            readCommand.CommandText = $"SELECT {keyColumn}, {string.Join(", ", dateColumns)} FROM {table}";
            using SqliteDataReader reader = readCommand.ExecuteReader();
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
            using SqliteCommand writeCommand = connection.CreateCommand();
            writeCommand.CommandText = $"UPDATE {table} SET {string.Join(", ", dateColumns.Select(column => $"{column} = ${column}"))} WHERE {keyColumn} = $key";

            // Each date is shifted in the shape that it arrived in.
            for (int i = 0; i < dateColumns.Length; i++)
                writeCommand.Parameters.AddWithValue($"${dateColumns[i]}", (object?)Shift(values[i], months) ?? DBNull.Value);

            // The row is found again by its key.
            writeCommand.Parameters.AddWithValue("$key", key);
            writeCommand.ExecuteNonQuery();
        }
    }

    /// <summary>Shifts one stored date by the given months and keeps the shape in which it arrived.</summary>
    private static string? Shift(string? value, int months)
    {
        // A column without a date stays without one.
        if (string.IsNullOrEmpty(value))
            return null;

        // The stored date moved forward by the whole months.
        DateTime shiftedDate = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddMonths(months);

        // A stored date is either a plain calendar day (invoices and plans) or a full round-trip timestamp (order lifecycle):
        // it is written back in whichever of the two shapes it arrived in, so the file stays readable exactly as the seed wrote it.
        return value.Length == 10
            ? shiftedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : shiftedDate.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Opens a connection to <see cref="DbPath"/> with a busy timeout applied.
    /// Examples.sqlite is a shared live database where different conversations write at the same instant.
    /// </summary>
    internal static async Task<SqliteConnection> OpenConnectionAsync()
    {
        // Every call gets its own connection since the conversations that use it run concurrently.
        SqliteConnection connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        // SQLite serialises writers with a single lock and without a busy timeout the loser of a race fails at once with "database is locked".
        // With it the loser waits for the holder to commit, which is what lets two concurrent commits of ConfirmOrder and CancelOrder queue up.
        await using SqliteCommand pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragmaCommand.ExecuteNonQueryAsync();

        // The caller owns the open connection and disposes it.
        return connection;
    }

    /// <summary>The tax rate that every invoice opened by the shop carries.</summary>
    private const double StandardTaxRate = 0.22;

    /// <summary>The days between the issue of an invoice and its due date.</summary>
    private const int InvoiceDueDays = 15;

    /// <summary>The shop's one backoffice write path onto <c>Invoices</c> and <c>InvoiceLines</c>, called by whoever dispositively charges a customer.</summary>
    /// <param name="connection">An open connection that shares <paramref name="transaction"/> with the caller's own writes.</param>
    /// <param name="transaction">The caller's transaction, so the charge commits or rolls back together with whatever earned it.</param>
    /// <param name="customerCode">Customer code to bill, never validated since the nursery serves anyone at the counter.</param>
    /// <param name="description">Line description as it will read on the invoice, such as a product name or a plan's fee label.</param>
    /// <param name="sku">Product SKU the line is for; null when the charge is not a catalog item.</param>
    /// <param name="orderId">Order the line was produced by; null when there is none.</param>
    /// <param name="unitPrice">Price per unit.</param>
    /// <param name="quantity">Quantity being charged.</param>
    /// <param name="chargeDate">Date the charge is issued on, which also selects the open invoice that it joins.</param>
    /// <returns>The invoiceId the line was written to.</returns>
    internal static async Task<string> BillCustomerAsync(SqliteConnection connection, SqliteTransaction transaction,
        string customerCode, string description, string? sku, string? orderId, double unitPrice, int quantity, DateTime chargeDate)
    {
        // The inventory agent confirming an order and the contract agent enrolling a plan write here and not to the tables
        // themselves: consistency of the books is a backoffice concern and not a domain one. The line is rounded to the cent.
        decimal lineAmount = Math.Round((decimal)unitPrice * quantity, 2);

        // The charge date as the invoices store it.
        string today = chargeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // A line joins the customer's invoice that is already open (Pending) for the calendar month of the charge date.
        string? invoiceId;
        await using (SqliteCommand findOpenInvoice = connection.CreateCommand())
        {
            // The most recent open invoice of the customer in that month is looked up inside the caller's transaction.
            findOpenInvoice.Transaction = transaction;
            findOpenInvoice.CommandText = """
                SELECT InvoiceId FROM Invoices
                WHERE CustomerCode = $customerCode COLLATE NOCASE AND Status = 'Pending'
                  AND strftime('%Y-%m', IssueDate) = strftime('%Y-%m', $today)
                ORDER BY IssueDate DESC LIMIT 1
                """;
            findOpenInvoice.Parameters.AddWithValue("$customerCode", customerCode);
            findOpenInvoice.Parameters.AddWithValue("$today", today);
            invoiceId = (string?)await findOpenInvoice.ExecuteScalarAsync();
        }

        // The position of the new line within its invoice.
        int lineNumber;

        // No invoice is open for the month so the charge opens a fresh one.
        if (invoiceId == null)
        {
            // A fresh invoice gets a short random identifier.
            invoiceId = $"INV-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

            // The invoice falls due after the standard number of days from the charge date.
            string dueDate = chargeDate.AddDays(InvoiceDueDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // The new invoice starts empty and Pending since its totals are computed below from its lines.
            await using SqliteCommand insertInvoice = connection.CreateCommand();
            insertInvoice.Transaction = transaction;
            insertInvoice.CommandText = """
                INSERT INTO Invoices (InvoiceId, CustomerCode, PeriodStart, PeriodEnd, IssueDate, DueDate, Subtotal, TaxRate, Tax, Total, Status)
                VALUES ($invoiceId, $customerCode, $today, $today, $today, $dueDate, 0, $taxRate, 0, 0, 'Pending')
                """;
            insertInvoice.Parameters.AddWithValue("$invoiceId", invoiceId);
            insertInvoice.Parameters.AddWithValue("$customerCode", customerCode);
            insertInvoice.Parameters.AddWithValue("$today", today);
            insertInvoice.Parameters.AddWithValue("$dueDate", dueDate);
            insertInvoice.Parameters.AddWithValue("$taxRate", StandardTaxRate);
            await insertInvoice.ExecuteNonQueryAsync();

            // The charge is the first line of the new invoice.
            lineNumber = 1;
        }
        // An invoice is already open, so the charge goes after the last line it holds.
        else
        {
            await using SqliteCommand maxLine = connection.CreateCommand();
            maxLine.Transaction = transaction;
            maxLine.CommandText = "SELECT COALESCE(MAX(LineNumber), 0) FROM InvoiceLines WHERE InvoiceId = $invoiceId";
            maxLine.Parameters.AddWithValue("$invoiceId", invoiceId);
            lineNumber = Convert.ToInt32(await maxLine.ExecuteScalarAsync(), CultureInfo.InvariantCulture) + 1;
        }

        // The charge itself becomes a line of the invoice with its amount rounded to the cent.
        await using (SqliteCommand insertLine = connection.CreateCommand())
        {
            // The line is written in the caller's transaction so it commits together with whatever earned it.
            insertLine.Transaction = transaction;
            insertLine.CommandText = """
                INSERT INTO InvoiceLines (InvoiceId, LineNumber, Description, Sku, OrderId, UnitPrice, Quantity, Unit, Amount)
                VALUES ($invoiceId, $lineNumber, $description, $sku, $orderId, $unitPrice, $quantity, 'unit', $amount)
                """;
            insertLine.Parameters.AddWithValue("$invoiceId", invoiceId);
            insertLine.Parameters.AddWithValue("$lineNumber", lineNumber);
            insertLine.Parameters.AddWithValue("$description", description);
            insertLine.Parameters.AddWithValue("$sku", (object?)sku ?? DBNull.Value);
            insertLine.Parameters.AddWithValue("$orderId", (object?)orderId ?? DBNull.Value);
            insertLine.Parameters.AddWithValue("$unitPrice", unitPrice);
            insertLine.Parameters.AddWithValue("$quantity", quantity);
            insertLine.Parameters.AddWithValue("$amount", (double)lineAmount);
            await insertLine.ExecuteNonQueryAsync();
        }

        // Subtotal, tax and total are always the sum of the lines that exist, so they never drift from what InvoiceLines holds.
        await using (SqliteCommand recompute = connection.CreateCommand())
        {
            // The totals are rewritten in the same transaction as the line that changed them.
            recompute.Transaction = transaction;
            recompute.CommandText = """
                UPDATE Invoices SET
                    Subtotal = (SELECT COALESCE(SUM(Amount), 0) FROM InvoiceLines WHERE InvoiceId = $invoiceId),
                    Tax = ROUND((SELECT COALESCE(SUM(Amount), 0) FROM InvoiceLines WHERE InvoiceId = $invoiceId) * TaxRate, 2),
                    Total = (SELECT COALESCE(SUM(Amount), 0) FROM InvoiceLines WHERE InvoiceId = $invoiceId)
                            + ROUND((SELECT COALESCE(SUM(Amount), 0) FROM InvoiceLines WHERE InvoiceId = $invoiceId) * TaxRate, 2)
                WHERE InvoiceId = $invoiceId
                """;
            recompute.Parameters.AddWithValue("$invoiceId", invoiceId);
            await recompute.ExecuteNonQueryAsync();
        }

        // The invoice that now holds the charge.
        return invoiceId;
    }
}