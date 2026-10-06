using System.Globalization;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;

namespace Examples.Tools;

/// <summary>
/// The accounts agent of The Greenhouse &amp; Nursery: the invoices issued to a customer for plants
/// bought from the catalog and for the Green Care Plan and the payments received against them.
/// Reads the same shared database the greenhouse ledger writes (see <see cref="GreenhouseDatabaseHelper"/>),
/// which is what lets a detail line point at the very order that produced it. It only ever
/// reads: nothing here charges, credits or settles anything.
/// </summary>
[ProvidesToolForIntent("billing")]
public class BillingTool : MorganaTool
{
    public BillingTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
        GreenhouseDatabaseHelper.Ensure();
    }

    // =========================================================================
    // ROWS AND LOOKUPS
    // =========================================================================

    private record Invoice(
        string InvoiceId,
        string CustomerCode,
        DateTime PeriodStart,
        DateTime PeriodEnd,
        DateTime IssueDate,
        DateTime DueDate,
        decimal Subtotal,
        double TaxRate,
        decimal Tax,
        decimal Total,
        string Status,
        DateTime? PaidDate,
        string? PaymentType,
        string? PaymentLastFour);

    private record InvoiceLine(
        int LineNumber,
        string Description,
        string? Sku,
        string? OrderId,
        decimal UnitPrice,
        int Quantity,
        string Unit,
        decimal Amount);

    private const string InvoiceColumns =
        "InvoiceId, CustomerCode, PeriodStart, PeriodEnd, IssueDate, DueDate, Subtotal, TaxRate, Tax, Total, Status, PaidDate, PaymentType, PaymentLastFour";

    private static Invoice ReadInvoice(SqliteDataReader reader) => new Invoice(
        reader.GetString(0),
        reader.GetString(1),
        ReadDate(reader, 2)!.Value,
        ReadDate(reader, 3)!.Value,
        ReadDate(reader, 4)!.Value,
        ReadDate(reader, 5)!.Value,
        (decimal)reader.GetDouble(6),
        reader.GetDouble(7),
        (decimal)reader.GetDouble(8),
        (decimal)reader.GetDouble(9),
        reader.GetString(10),
        ReadDate(reader, 11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));

    private static DateTime? ReadDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Resolves the name behind a customer code, when the shop happens to know it.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a gate. A code the books have never seen is served exactly like one they
    /// have and simply comes back empty: the nursery takes anyone at the counter and an accounts
    /// agent that refuses to look before it has recognised you is a worse demo and a worse shop.
    /// The name is a courtesy on the answer, never a permission to answer.
    /// </remarks>
    private static async Task<string?> FindCustomerNameAsync(SqliteConnection connection, string customerCode)
    {
        // COLLATE NOCASE, like everywhere else in this plugin: the code travels through a chat
        // transcript, typed from memory and 'p994e' is the same customer as 'P994E'.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM Customers WHERE CustomerCode = $customerCode COLLATE NOCASE";
        command.Parameters.AddWithValue("$customerCode", customerCode);

        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<List<InvoiceLine>> GetInvoiceLinesAsync(SqliteConnection connection, string invoiceId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT LineNumber, Description, Sku, OrderId, UnitPrice, Quantity, Unit, Amount FROM InvoiceLines WHERE InvoiceId = $invoiceId ORDER BY LineNumber";
        command.Parameters.AddWithValue("$invoiceId", invoiceId);

        List<InvoiceLine> lines = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(new InvoiceLine(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (decimal)reader.GetDouble(4),
                reader.GetInt32(5),
                reader.GetString(6),
                (decimal)reader.GetDouble(7)));
        }

        return lines;
    }

    // The period is stored as the two months it spans, never as prose, so that a monthly invoice
    // and a quarterly one read naturally from the same two columns.
    private static string PeriodLabel(DateTime periodStart, DateTime periodEnd) =>
        periodStart.Year == periodEnd.Year && periodStart.Month == periodEnd.Month
            ? periodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
            : $"{periodStart.ToString("MMMM", CultureInfo.InvariantCulture)} - {periodEnd.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}";

    private static string StatusIcon(string status) => status switch
    {
        "Paid" => "✅",
        "Pending" => "⏳",
        "Overdue" => "⚠️",
        "Cancelled" => "❌",
        _ => "📋"
    };

    private static string StatusDescription(string status) => status switch
    {
        "Paid" => "Paid",
        "Pending" => "Pending Payment",
        "Overdue" => "Overdue",
        "Cancelled" => "Cancelled",
        _ => status
    };

    private static PaymentMethodInfo? PaymentMethod(string? paymentType, string? lastFourDigits)
    {
        if (paymentType == null || lastFourDigits == null)
            return null;

        return new PaymentMethodInfo(
            paymentType,
            lastFourDigits,
            paymentType switch
            {
                "CreditCard" => $"Credit Card ending in {lastFourDigits}",
                "BankTransfer" => $"Bank Transfer from account ending in {lastFourDigits}",
                "DirectDebit" => $"Direct Debit from account ending in {lastFourDigits}",
                _ => $"{paymentType} ({lastFourDigits})"
            });
    }

    private const string NothingUnderThisCode =
        "The accounts book holds no record under this customer code. Nothing on these pages identifies the right "
        + "one: the code names no account here, or it is mistyped.";

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Retrieves the most recent invoices issued to a customer.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <param name="count">Number of recent invoices to retrieve (1-10)</param>
    /// <returns>The invoice summaries, or a note when the books hold none</returns>
    public async Task<InvoicesResult> GetInvoices(string customerCode, int count)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        count = Math.Clamp(count, 1, 10);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {InvoiceColumns} FROM Invoices WHERE CustomerCode = $customerCode COLLATE NOCASE ORDER BY IssueDate DESC LIMIT $count";
        command.Parameters.AddWithValue("$customerCode", customerCode);
        command.Parameters.AddWithValue("$count", count);

        List<Invoice> invoices = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                invoices.Add(ReadInvoice(reader));
        }

        if (invoices.Count == 0)
        {
            return new InvoicesResult(customerCode, customerName, 0, [], NothingUnderThisCode);
        }

        return new InvoicesResult(
            customerCode,
            customerName,
            invoices.Count,
            [.. invoices.Select(invoice => new InvoiceSummary(
                invoice.InvoiceId,
                PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
                invoice.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                invoice.DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                invoice.Total,
                invoice.Status,
                StatusIcon(invoice.Status),
                invoice.PaidDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                invoice.Status == "Pending"
                    ? Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days)
                    : null))]);
    }

    /// <summary>
    /// Retrieves detailed information about a specific invoice.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <param name="invoiceId">Specific invoice identifier (e.g., "INV-0512")</param>
    /// <returns>The complete invoice details, or the error naming the invoices that exist</returns>
    public async Task<InvoiceDetailsResult> GetInvoiceDetails(string customerCode, string invoiceId)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        // Scoped to the customer, not merely looked up by id: one customer's invoice is never
        // readable by quoting its number in another customer's conversation and an invoice that
        // belongs to someone else is reported exactly as one that does not exist.
        Invoice? invoice = null;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {InvoiceColumns} FROM Invoices WHERE InvoiceId = $invoiceId COLLATE NOCASE AND CustomerCode = $customerCode COLLATE NOCASE";
            command.Parameters.AddWithValue("$invoiceId", invoiceId);
            command.Parameters.AddWithValue("$customerCode", customerCode);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                invoice = ReadInvoice(reader);
        }

        if (invoice == null)
        {
            await using SqliteCommand available = connection.CreateCommand();
            available.CommandText = "SELECT InvoiceId FROM Invoices WHERE CustomerCode = $customerCode COLLATE NOCASE ORDER BY IssueDate DESC";
            available.Parameters.AddWithValue("$customerCode", customerCode);

            List<string> invoiceIds = [];
            await using (SqliteDataReader reader = await available.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    invoiceIds.Add(reader.GetString(0));
            }

            return new InvoiceDetailsResult(
                Error: "Invoice not found",
                RequestedInvoiceId: invoiceId,
                AvailableInvoices: invoiceIds,
                Note: invoiceIds.Count == 0 ? NothingUnderThisCode : null);
        }

        List<InvoiceLine> lines = await GetInvoiceLinesAsync(connection, invoice.InvoiceId);
        int daysUntilDue = (invoice.DueDate - DateTime.UtcNow).Days;

        return new InvoiceDetailsResult(
            InvoiceId: invoice.InvoiceId,
            CustomerCode: invoice.CustomerCode,
            CustomerName: customerName,
            Period: PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
            Dates: new InvoiceDates(
                invoice.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                invoice.DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                invoice.PaidDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            Status: new InvoiceStatus(
                invoice.Status,
                StatusIcon(invoice.Status),
                StatusDescription(invoice.Status),
                invoice.Status == "Pending" ? daysUntilDue : null,
                invoice.Status == "Pending" && daysUntilDue < 0,
                invoice.Status == "Pending" && daysUntilDue < 0
                    ? Math.Abs(daysUntilDue)
                    : null),
            // Sku is read from the row but never surfaced: it is the greenhouse ledger's identifier
            // for a plant and an accounts agent that hands it out starts being asked catalog
            // questions. OrderId is a different thing — a reference to what was billed, which is
            // exactly what an invoice line is for.
            LineItems: [.. lines.Select(line => new InvoiceLineItem(
                line.Description,
                line.OrderId,
                line.UnitPrice,
                line.Quantity,
                line.Unit,
                line.Amount,
                line.Quantity > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"{line.Quantity} {line.Unit} × €{line.UnitPrice:F2}")
                    : null))],
            Amounts: new InvoiceAmounts(
                invoice.Subtotal,
                invoice.Tax,
                string.Create(CultureInfo.InvariantCulture, $"{invoice.TaxRate * 100:0.##}%"),
                invoice.Total),
            PaymentMethod: PaymentMethod(invoice.PaymentType, invoice.PaymentLastFour));
    }

    /// <summary>
    /// Sums what the customer still owes: the invoices left unpaid, oldest first.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <returns>The outstanding total and the invoices making it up</returns>
    public async Task<OutstandingBalanceResult> GetOutstandingBalance(string customerCode)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {InvoiceColumns} FROM Invoices WHERE CustomerCode = $customerCode COLLATE NOCASE AND Status <> 'Paid' AND Status <> 'Cancelled' ORDER BY DueDate";
        command.Parameters.AddWithValue("$customerCode", customerCode);

        List<Invoice> unpaid = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                unpaid.Add(ReadInvoice(reader));
        }

        if (unpaid.Count == 0)
        {
            return new OutstandingBalanceResult(
                customerCode,
                customerName,
                HasOutstanding: false,
                TotalDue: 0m,
                Message: "Nothing is outstanding under this customer code: either every invoice has been settled, or the books hold none.");
        }

        // The sum is computed here rather than left to whoever reads the list: money that a
        // customer is told they owe is not a figure to be added up in prose and a total that
        // disagrees with the invoices under it is worse than no total at all.
        decimal totalDue = unpaid.Sum(invoice => invoice.Total);
        int worstDaysOverdue = unpaid.Max(invoice => Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days));

        return new OutstandingBalanceResult(
            customerCode,
            customerName,
            HasOutstanding: true,
            TotalDue: totalDue,
            InvoiceCount: unpaid.Count,
            OldestDueDate: unpaid[0].DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            DaysOverdue: worstDaysOverdue > 0 ? worstDaysOverdue : null,
            Invoices: [.. unpaid.Select(invoice => new OutstandingInvoice(
                invoice.InvoiceId,
                PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
                invoice.DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                invoice.Total,
                invoice.Status,
                StatusIcon(invoice.Status),
                Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days)))]);
    }

    /// <summary>
    /// Retrieves the payment history of a customer.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <param name="months">Number of months of history to retrieve (1-12)</param>
    /// <returns>The payment history, or a message when no payment was received</returns>
    public async Task<PaymentHistoryResult> GetPaymentHistory(string customerCode, int months = 6)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        months = Math.Clamp(months, 1, 12);
        DateTime cutoffDate = DateTime.UtcNow.AddMonths(-months);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {InvoiceColumns} FROM Invoices WHERE CustomerCode = $customerCode COLLATE NOCASE AND Status = 'Paid' AND PaidDate >= $cutoff ORDER BY PaidDate DESC";
        command.Parameters.AddWithValue("$customerCode", customerCode);
        command.Parameters.AddWithValue("$cutoff", cutoffDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        List<Invoice> payments = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                payments.Add(ReadInvoice(reader));
        }

        if (payments.Count == 0)
        {
            return new PaymentHistoryResult(
                customerCode,
                customerName,
                months,
                HasData: false,
                Message: $"No payment received in the last {months} months under this customer code.");
        }

        decimal totalPaid = payments.Sum(payment => payment.Total);

        return new PaymentHistoryResult(
            customerCode,
            customerName,
            months,
            HasData: true,
            Summary: new PaymentSummary(
                payments.Count,
                totalPaid,
                Math.Round(totalPaid / payments.Count, 2)),
            Payments: [.. payments.Select(payment => new PaymentEntry(
                payment.InvoiceId,
                PeriodLabel(payment.PeriodStart, payment.PeriodEnd),
                payment.Total,
                payment.PaidDate!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                PaymentMethod(payment.PaymentType, payment.PaymentLastFour)))]);
    }
}
