using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Examples.Data;

/// <summary>The write that the shop's agents share on the system of record, so the books agree whichever agent closes a sale.</summary>
internal static class GreenhouseDatabaseHelper
{
    // The tax rate that every invoice opened by the shop carries.
    private const double StandardTaxRate = 0.22;

    // The days between the issue of an invoice and its due date.
    private const int InvoiceDueDays = 15;

    /// <summary>The shop's one backoffice write path onto <c>Invoices</c> and <c>InvoiceLines</c>, called by whoever dispositively charges a customer.</summary>
    /// <param name="connection">An open connection that shares <paramref name="transaction"/> with the caller's own writes.</param>
    /// <param name="transaction">The caller's transaction, so the charge commits or rolls back together with whatever earned it.</param>
    /// <param name="customerCode">Customer code to bill, never validated since the nursery takes anyone at the counter.</param>
    /// <param name="description">Line description as it will read on the invoice, such as a product name or a plan's fee label.</param>
    /// <param name="sku">Product SKU the line is for, or null when the charge is not a catalog item.</param>
    /// <param name="orderId">Order the line was produced by, or null when there is none.</param>
    /// <param name="unitPrice">Price per unit.</param>
    /// <param name="quantity">Quantity being charged.</param>
    /// <param name="chargeDate">Date the charge is issued on, which also selects the open invoice that it joins.</param>
    /// <returns>The invoiceId the line was written to.</returns>
    internal static async Task<string> BillCustomerAsync(SqliteConnection connection, SqliteTransaction transaction,
        string customerCode, string description, string? sku, string? orderId, double unitPrice, int quantity, DateTime chargeDate)
    {
        // The greenhouse ledger confirming an order and the Green Care Plan agent enrolling a plan write here and not to the tables
        // themselves: consistency of the books is a backoffice concern and not a domain one.
        decimal lineAmount = Math.Round((decimal)unitPrice * quantity, 2);
        string today = chargeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // A line joins the customer's invoice that is already open (Pending) for the calendar month of the charge date.
        string? invoiceId;
        await using (SqliteCommand findOpen = connection.CreateCommand())
        {
            // The most recent open invoice of the customer in that month is looked up inside the caller's transaction.
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
        else
        {
            // The charge goes after the last line that the open invoice already holds.
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

        // Subtotal/Tax/Total are always the sum of the lines that actually exist, recomputed here
        // rather than incremented, so they can never drift from what InvoiceLines really holds.
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
