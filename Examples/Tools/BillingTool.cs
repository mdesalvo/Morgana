using System.Globalization;
using System.Text.Json;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;

namespace Examples.Tools;

/// <summary>
/// The accounts desk of The Greenhouse &amp; Nursery: the invoices issued to a customer for plants
/// bought from the catalog and for the Green Care Plan, and the payments received against them.
/// Reads the same shared database the greenhouse ledger writes (see <see cref="GreenhouseDatabaseHelper"/>),
/// which is what lets a detail line point at the very order that produced it — and it only ever
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
    /// have and simply comes back empty: the nursery takes anyone at the counter, and an accounts
    /// desk that refuses to look before it has recognised you is a worse demo and a worse shop.
    /// The name is a courtesy on the answer, never a permission to answer.
    /// </remarks>
    private static async Task<string?> FindCustomerNameAsync(SqliteConnection connection, string customerCode)
    {
        // COLLATE NOCASE, like everywhere else in this plugin: the code travels through a chat
        // transcript, typed from memory, and 'p994e' is the same customer as 'P994E'.
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

    private static object? PaymentMethod(string? paymentType, string? lastFourDigits)
    {
        if (paymentType == null || lastFourDigits == null)
            return null;

        return new
        {
            type = paymentType,
            lastFourDigits,
            formatted = paymentType switch
            {
                "CreditCard" => $"Credit Card ending in {lastFourDigits}",
                "BankTransfer" => $"Bank Transfer from account ending in {lastFourDigits}",
                "DirectDebit" => $"Direct Debit from account ending in {lastFourDigits}",
                _ => $"{paymentType} ({lastFourDigits})"
            }
        };
    }

    private const string NothingUnderThisCode =
        "The accounts book holds no record under this customer code. Nothing on these pages identifies the right "
        + "one: the code names no account here, or it is mistyped.";

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Retrieves the most recent invoices issued to a customer as structured JSON.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <param name="count">Number of recent invoices to retrieve (1-10)</param>
    /// <returns>JSON array of invoice summaries</returns>
    public async Task<string> GetInvoices(string customerCode, int count)
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
            return JsonSerializer.Serialize(new
            {
                customerCode,
                customerName,
                totalCount = 0,
                invoices = Array.Empty<object>(),
                note = NothingUnderThisCode
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        var result = new
        {
            customerCode,
            customerName,
            totalCount = invoices.Count,
            invoices = invoices.Select(invoice => new
            {
                invoiceId = invoice.InvoiceId,
                period = PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
                issueDate = invoice.IssueDate.ToString("dd/MM/yyyy"),
                dueDate = invoice.DueDate.ToString("dd/MM/yyyy"),
                total = invoice.Total,
                status = invoice.Status,
                statusIcon = StatusIcon(invoice.Status),
                paidDate = invoice.PaidDate?.ToString("dd/MM/yyyy"),
                daysOverdue = invoice.Status == "Pending"
                    ? Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days)
                    : (int?)null
            }).ToList()
        };

        return JsonSerializer.Serialize(result, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Retrieves detailed information about a specific invoice as structured JSON.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <param name="invoiceId">Specific invoice identifier (e.g., "INV-0512")</param>
    /// <returns>JSON object with complete invoice details</returns>
    public async Task<string> GetInvoiceDetails(string customerCode, string invoiceId)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        // Scoped to the customer, not merely looked up by id: one customer's invoice is never
        // readable by quoting its number in another customer's conversation, and an invoice that
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

            return JsonSerializer.Serialize(new
            {
                error = "Invoice not found",
                requestedInvoiceId = invoiceId,
                availableInvoices = invoiceIds,
                note = invoiceIds.Count == 0 ? NothingUnderThisCode : null
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        List<InvoiceLine> lines = await GetInvoiceLinesAsync(connection, invoice.InvoiceId);
        int daysUntilDue = (invoice.DueDate - DateTime.UtcNow).Days;

        var result = new
        {
            invoiceId = invoice.InvoiceId,
            customerCode = invoice.CustomerCode,
            customerName,
            period = PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
            dates = new
            {
                issueDate = invoice.IssueDate.ToString("dd/MM/yyyy"),
                dueDate = invoice.DueDate.ToString("dd/MM/yyyy"),
                paidDate = invoice.PaidDate?.ToString("dd/MM/yyyy")
            },
            status = new
            {
                value = invoice.Status,
                icon = StatusIcon(invoice.Status),
                description = StatusDescription(invoice.Status),
                daysUntilDue = invoice.Status == "Pending" ? daysUntilDue : (int?)null,
                isOverdue = invoice.Status == "Pending" && daysUntilDue < 0,
                daysOverdue = invoice.Status == "Pending" && daysUntilDue < 0
                    ? Math.Abs(daysUntilDue)
                    : (int?)null
            },
            // Sku is read from the row but never surfaced: it is the greenhouse ledger's identifier
            // for a plant, and an accounts desk that hands it out starts being asked catalog
            // questions. OrderId is a different thing — a reference to what was billed, which is
            // exactly what an invoice line is for.
            lineItems = lines.Select(line => new
            {
                description = line.Description,
                orderId = line.OrderId,
                unitPrice = line.UnitPrice,
                quantity = line.Quantity,
                unit = line.Unit,
                amount = line.Amount,
                formattedQuantity = line.Quantity > 1
                    ? $"{line.Quantity} {line.Unit} × €{line.UnitPrice:F2}"
                    : null
            }).ToList(),
            amounts = new
            {
                subtotal = invoice.Subtotal,
                tax = invoice.Tax,
                taxRate = $"{invoice.TaxRate * 100:0.##}%",
                total = invoice.Total
            },
            paymentMethod = PaymentMethod(invoice.PaymentType, invoice.PaymentLastFour)
        };

        return JsonSerializer.Serialize(result, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Sums what the customer still owes: the invoices left unpaid, oldest first.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <returns>JSON object with the outstanding total and the invoices making it up</returns>
    public async Task<string> GetOutstandingBalance(string customerCode)
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
            return JsonSerializer.Serialize(new
            {
                customerCode,
                customerName,
                hasOutstanding = false,
                totalDue = 0m,
                message = "Nothing is outstanding under this customer code: either every invoice has been settled, or the books hold none."
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        // The sum is computed here rather than left to whoever reads the list: money that a
        // customer is told they owe is not a figure to be added up in prose, and a total that
        // disagrees with the invoices under it is worse than no total at all.
        decimal totalDue = unpaid.Sum(invoice => invoice.Total);
        int worstDaysOverdue = unpaid.Max(invoice => Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days));

        var result = new
        {
            customerCode,
            customerName,
            hasOutstanding = true,
            totalDue,
            invoiceCount = unpaid.Count,
            oldestDueDate = unpaid[0].DueDate.ToString("dd/MM/yyyy"),
            daysOverdue = worstDaysOverdue > 0 ? worstDaysOverdue : (int?)null,
            invoices = unpaid.Select(invoice => new
            {
                invoiceId = invoice.InvoiceId,
                period = PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
                dueDate = invoice.DueDate.ToString("dd/MM/yyyy"),
                total = invoice.Total,
                status = invoice.Status,
                statusIcon = StatusIcon(invoice.Status),
                daysOverdue = Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days)
            }).ToList()
        };

        return JsonSerializer.Serialize(result, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Retrieves the payment history of a customer as structured JSON.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <param name="months">Number of months of history to retrieve (1-12)</param>
    /// <returns>JSON object with payment history</returns>
    public async Task<string> GetPaymentHistory(string customerCode, int months = 6)
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
            return JsonSerializer.Serialize(new
            {
                customerCode,
                customerName,
                months,
                hasData = false,
                message = $"No payment received in the last {months} months under this customer code."
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        decimal totalPaid = payments.Sum(payment => payment.Total);

        var result = new
        {
            customerCode,
            customerName,
            months,
            hasData = true,
            summary = new
            {
                totalPayments = payments.Count,
                totalAmount = totalPaid,
                averageMonthly = Math.Round(totalPaid / payments.Count, 2)
            },
            payments = payments.Select(payment => new
            {
                invoiceId = payment.InvoiceId,
                period = PeriodLabel(payment.PeriodStart, payment.PeriodEnd),
                amount = payment.Total,
                paidDate = payment.PaidDate!.Value.ToString("dd/MM/yyyy"),
                paymentMethod = PaymentMethod(payment.PaymentType, payment.PaymentLastFour)
            }).ToList()
        };

        return JsonSerializer.Serialize(result, GreenhouseDatabaseHelper.JsonOptions);
    }
}
