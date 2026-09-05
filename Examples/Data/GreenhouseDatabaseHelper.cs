using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Examples.Data;

/// <summary>
/// The single system of record of The Greenhouse &amp; Nursery, shared by every agent of this
/// plugin: customers, catalog and stock, orders, the Green Care Plan and its clauses, invoices
/// and their detail lines. One shop, one database — an invoice line points at the very order
/// that produced it and the customer code a customer gives the accounts desk is the same code
/// the greenhouse ledger and the care plan are keyed by.
/// </summary>
/// <remarks>
/// It is standalone and independent from Morgana's own per-conversation persistence: stock,
/// orders and ledgers survive restarts AND cross conversationIds, because a shop is a system of
/// record shared by whoever talks to it, not a per-conversation scratchpad.
/// </remarks>
internal static class GreenhouseDatabaseHelper
{
    private static readonly object InitLock = new();

    // The path that was actually deployed, not a bare bool: StoragePath is an environment
    // variable and a process that changes it (the PromptHarness gives every run its own
    // throwaway directory) must deploy the seed again rather than skip it and then query a
    // database that was never put there.
    private static string? _deployedPath;

    private static string StorageDirectory
    {
        get
        {
            string? storageDirectory = Environment.GetEnvironmentVariable("Morgana__ConversationPersistence__StoragePath");
            return string.IsNullOrWhiteSpace(storageDirectory)
                ? AppContext.BaseDirectory
                : storageDirectory;
        }
    }

    private static string DbPath => Path.Combine(StorageDirectory, "Examples.db");

    private static string ConnectionString => $"Data Source={DbPath}";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Deploys the embedded seed database to <see cref="DbPath"/> the very first time any tool of
    /// this plugin is constructed against that physical location, then rebases its calendar onto
    /// the month the shop is actually being opened in. Every activation after that (any
    /// conversation, any process restart, any container recreation that keeps the volume) finds
    /// the file already there and does nothing further: from that point on the data only changes
    /// through live orders, confirmations and cancellations.
    /// </summary>
    internal static void Ensure()
    {
        string databasePath = DbPath;
        if (_deployedPath == databasePath)
            return;

        lock (InitLock)
        {
            if (_deployedPath == databasePath)
                return;

            // File.Exists is the ENTIRE "have we seeded yet" check — there is no separate flag
            // or marker row anywhere. That is deliberate: the moment Examples.db exists on disk
            // at this path, whatever it contains (seed data, or years of live orders on top of
            // it) is the truth and we must never overwrite it just because the process restarted.
            if (!File.Exists(databasePath))
            {
                Directory.CreateDirectory(StorageDirectory);

                // Seeded under a private temporary name and only then moved into place, so a
                // half-written or half-rebased file can never become the shop's system of record:
                // the move is the single instant at which the database starts existing and the
                // loser of a race between two processes deletes its own copy instead of
                // overwriting the winner's.
                string stagingPath = Path.Combine(StorageDirectory, $"Examples.{Guid.NewGuid():N}.tmp");
                try
                {
                    using (Stream seedStream = typeof(GreenhouseDatabaseHelper).Assembly.GetManifestResourceStream("Examples.Data.Examples.db")
                        ?? throw new InvalidOperationException("Embedded seed database 'Examples.Data.Examples.db' not found."))
                    using (FileStream fileStream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write))
                    {
                        seedStream.CopyTo(fileStream);
                    }

                    RebaseCalendar(stagingPath);

                    File.Move(stagingPath, databasePath, overwrite: false);
                }
                catch (IOException) when (File.Exists(databasePath))
                {
                    // Another process got there first: its database is the real one.
                }
                finally
                {
                    if (File.Exists(stagingPath))
                        File.Delete(stagingPath);
                }
            }

            _deployedPath = databasePath;
        }
    }

    /// <summary>
    /// Shifts every stored date forward by the whole months elapsed since the seed was authored,
    /// so the shop always opens on a plausible present: an invoice still due, a care plan with
    /// months left to run, orders placed within the last few weeks.
    /// </summary>
    /// <remarks>
    /// This runs exactly once, on the staged copy, before it becomes the database — never again,
    /// and never on live data. The shift is in whole months and preserves the day of the month,
    /// so the internal story of the seed (this invoice bills that order, this plan started when
    /// that first invoice was issued) survives it intact.
    /// </remarks>
    private static void RebaseCalendar(string stagingPath)
    {
        using SqliteConnection connection = new SqliteConnection($"Data Source={stagingPath}");
        connection.Open();

        DateTime anchorMonth;
        using (SqliteCommand readAnchor = connection.CreateCommand())
        {
            readAnchor.CommandText = "SELECT AnchorMonth FROM SeedInfo WHERE Id = 1";
            anchorMonth = DateTime.Parse((string)readAnchor.ExecuteScalar()!, CultureInfo.InvariantCulture);
        }

        DateTime currentMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        int months = ((currentMonth.Year - anchorMonth.Year) * 12) + currentMonth.Month - anchorMonth.Month;
        if (months <= 0)
            return;

        ShiftDates(connection, "Customers", "CustomerCode", ["JoinedAt"], months);
        ShiftDates(connection, "Orders", "OrderId", ["CreatedAt", "ConfirmedAt", "CancelledAt"], months);
        ShiftDates(connection, "CarePlans", "ContractId", ["StartDate", "EndDate"], months);
        ShiftDates(connection, "PlanVisits", "VisitId", ["VisitDate"], months);
        ShiftDates(connection, "Invoices", "InvoiceId", ["PeriodStart", "PeriodEnd", "IssueDate", "DueDate", "PaidDate"], months);

        using SqliteCommand writeAnchor = connection.CreateCommand();
        writeAnchor.CommandText = "UPDATE SeedInfo SET AnchorMonth = $anchorMonth WHERE Id = 1";
        writeAnchor.Parameters.AddWithValue("$anchorMonth", currentMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writeAnchor.ExecuteNonQuery();
    }

    private static void ShiftDates(SqliteConnection connection, string table, string keyColumn, string[] dateColumns, int months)
    {
        List<(string Key, string?[] Values)> rows = [];

        using (SqliteCommand read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT {keyColumn}, {string.Join(", ", dateColumns)} FROM {table}";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                string?[] values = new string?[dateColumns.Length];
                for (int i = 0; i < dateColumns.Length; i++)
                    values[i] = reader.IsDBNull(i + 1) ? null : reader.GetString(i + 1);

                rows.Add((reader.GetString(0), values));
            }
        }

        foreach ((string key, string?[] values) in rows)
        {
            using SqliteCommand write = connection.CreateCommand();
            write.CommandText = $"UPDATE {table} SET {string.Join(", ", dateColumns.Select(column => $"{column} = ${column}"))} WHERE {keyColumn} = $key";
            for (int i = 0; i < dateColumns.Length; i++)
                write.Parameters.AddWithValue($"${dateColumns[i]}", (object?)Shift(values[i], months) ?? DBNull.Value);

            write.Parameters.AddWithValue("$key", key);
            write.ExecuteNonQuery();
        }
    }

    // A stored date is either a plain calendar day (invoices, plans) or a full round-trip
    // timestamp (order lifecycle): the shifted value is written back in whichever of the two
    // shapes it arrived in, so the file stays readable exactly as the seed wrote it.
    private static string? Shift(string? value, int months)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        DateTime parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddMonths(months);
        return value.Length == 10
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : parsed.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Opens (and returns) a connection to <see cref="DbPath"/> with a busy timeout applied.
    /// </summary>
    /// <remarks>
    /// Examples.db is a SHARED, live database: different conversations WILL try to write to it at
    /// the same instant (that is the whole point of this example). SQLite serializes writers with a
    /// single write lock and without a busy timeout the loser of that race throws "database is
    /// locked" immediately. PRAGMA busy_timeout instead makes it WAIT for the holder to commit and
    /// then proceed — the transactional writes in ConfirmOrder/CancelOrder rely on this so that two
    /// concurrent commits queue up rather than one of them blowing up in the caller's face.
    /// </remarks>
    internal static async Task<SqliteConnection> OpenConnectionAsync()
    {
        SqliteConnection connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync();

        return connection;
    }

    private const double StandardTaxRate = 0.22;
    private const int InvoiceDueDays = 15;

    /// <summary>
    /// The shop's ONE backoffice write path onto <c>Invoices</c>/<c>InvoiceLines</c>. Whoever
    /// dispositively charges a customer — the greenhouse ledger confirming an order, the Green
    /// Care Plan desk enrolling a new plan — calls this instead of writing those two tables
    /// itself, so every agent's writes land in the identical shape: a line joins the customer's
    /// invoice already open (Pending) for the calendar month of <paramref name="chargeDate"/>, or
    /// opens a fresh one if none is. This is deliberately in the shared helper, not owned by
    /// either tool: consistency of the books is a backoffice concern, not a domain one.
    /// </summary>
    /// <param name="connection">An open connection, sharing <paramref name="transaction"/> with the caller's own writes.</param>
    /// <param name="transaction">The caller's transaction: the charge commits or rolls back together with whatever earned it.</param>
    /// <param name="customerCode">Customer code to bill. Not validated against Customers — see the "not a gate" remark on FindCustomerNameAsync in BillingTool/InventoryTool/ContractTool.</param>
    /// <param name="description">Line description as it will read on the invoice (e.g. a product name, or a plan's fee label).</param>
    /// <param name="sku">Product SKU the line is for, or null when the charge isn't a catalog item (e.g. a plan fee).</param>
    /// <param name="orderId">Order the line was produced by, or null when there isn't one (e.g. a plan fee).</param>
    /// <param name="unitPrice">Price per unit.</param>
    /// <param name="quantity">Quantity being charged.</param>
    /// <param name="chargeDate">Date the charge is issued on; also selects which open invoice (if any) it joins.</param>
    /// <returns>The invoiceId the line was written to.</returns>
    internal static async Task<string> BillCustomerAsync(SqliteConnection connection, SqliteTransaction transaction,
        string customerCode, string description, string? sku, string? orderId, double unitPrice, int quantity, DateTime chargeDate)
    {
        decimal lineAmount = Math.Round((decimal)unitPrice * quantity, 2);
        string today = chargeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string? invoiceId;
        await using (SqliteCommand findOpen = connection.CreateCommand())
        {
            findOpen.Transaction = transaction;
            findOpen.CommandText = """
                SELECT InvoiceId FROM Invoices
                WHERE CustomerCode = $customerCode COLLATE NOCASE AND Status = 'Pending'
                  AND strftime('%Y-%m', IssueDate) = strftime('%Y-%m', $today)
                ORDER BY IssueDate DESC LIMIT 1
                """;
            findOpen.Parameters.AddWithValue("$customerCode", customerCode);
            findOpen.Parameters.AddWithValue("$today", today);
            invoiceId = (string?)await findOpen.ExecuteScalarAsync();
        }

        int lineNumber;
        if (invoiceId == null)
        {
            invoiceId = $"INV-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
            string dueDate = chargeDate.AddDays(InvoiceDueDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
            lineNumber = 1;
        }
        else
        {
            await using SqliteCommand maxLine = connection.CreateCommand();
            maxLine.Transaction = transaction;
            maxLine.CommandText = "SELECT COALESCE(MAX(LineNumber), 0) FROM InvoiceLines WHERE InvoiceId = $invoiceId";
            maxLine.Parameters.AddWithValue("$invoiceId", invoiceId);
            lineNumber = Convert.ToInt32(await maxLine.ExecuteScalarAsync()) + 1;
        }

        await using (SqliteCommand insertLine = connection.CreateCommand())
        {
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

        // Subtotal/Tax/Total are always the sum of the lines that actually exist, recomputed here
        // rather than incremented, so they can never drift from what InvoiceLines really holds.
        await using (SqliteCommand recompute = connection.CreateCommand())
        {
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

        return invoiceId;
    }
}
