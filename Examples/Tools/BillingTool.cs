using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI;
using Morgana.AI.Tools;
using Morgana.AI.Attributes;

namespace Examples.Tools;

/// <summary>
/// The accounts agent of The Greenhouse &amp; Nursery: the invoices issued to a customer and the payments received against them.
/// It reads the shared database that the other agents write, which lets a line point at the very order that produced it;
/// it only ever reads, so nothing here charges, credits or settles anything.
/// </summary>
[ProvidesToolForIntent("billing")]
public class BillingTool : MorganaTool
{
    /// <summary>Binds the tools to the agent's context and makes sure the shop's database is deployed.</summary>
    public BillingTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
        // The first tool built in the process deploys the seed; every later one finds it in place.
        ExamplesDatabaseHelper.Ensure();
    }

    // =========================================================================
    // ROWS AND LOOKUPS
    // =========================================================================

    /// <summary>One invoice as the books hold it.</summary>
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

    /// <summary>One charge line of an invoice.</summary>
    private record InvoiceLine(
        int LineNumber,
        string Description,
        string? Sku,
        string? OrderId,
        decimal UnitPrice,
        int Quantity,
        string Unit,
        decimal Amount);

    /// <summary>The columns of an invoice in the order <see cref="ReadInvoice"/> reads them.</summary>
    private const string InvoiceColumns =
        "InvoiceId, CustomerCode, PeriodStart, PeriodEnd, IssueDate, DueDate, Subtotal, TaxRate, Tax, Total, Status, PaidDate, PaymentType, PaymentLastFour";

    /// <summary>Reads the invoice at the reader's current row, selected with <see cref="InvoiceColumns"/>.</summary>
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

    /// <summary>Reads a stored date, null when the column holds none.</summary>
    private static DateTime? ReadDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Reads the charge lines of one invoice in their order.</summary>
    private static async Task<List<InvoiceLine>> GetInvoiceLinesAsync(SqliteConnection connection, string invoiceId)
    {
        // The lines are numbered within their invoice.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT LineNumber, Description, Sku, OrderId, UnitPrice, Quantity, Unit, Amount FROM InvoiceLines WHERE InvoiceId = $invoiceId ORDER BY LineNumber";
        command.Parameters.AddWithValue("$invoiceId", invoiceId);

        // Each row is one line, its product and order present only where the charge has one.
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

        // The invoice's lines in their order.
        return lines;
    }

    /// <summary>
    /// Names the period an invoice covers: one month or the first and last month it spans.
    /// The period is stored as the two months it spans, so a monthly invoice and a quarterly one read from the same two columns.
    /// </summary>
    private static string PeriodLabel(DateTime periodStart, DateTime periodEnd) =>
        // A period within one month is named by that month alone.
        periodStart.Year == periodEnd.Year && periodStart.Month == periodEnd.Month
            ? periodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
            : $"{periodStart.ToString("MMMM", CultureInfo.InvariantCulture)} - {periodEnd.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>The icon a channel shows beside an invoice's status; an unknown status shows a generic one.</summary>
    private static string StatusIcon(string status) => status switch
    {
        "Paid" => "✅",
        "Pending" => "⏳",
        "Overdue" => "⚠️",
        "Cancelled" => "❌",
        _ => "📋"
    };

    /// <summary>The status as a customer reads it; an unknown status reads as stored.</summary>
    private static string StatusDescription(string status) => status switch
    {
        "Paid" => "Paid",
        "Pending" => "Pending Payment",
        "Overdue" => "Overdue",
        "Cancelled" => "Cancelled",
        _ => status
    };

    /// <summary>Describes how an invoice was paid, by type and last four digits; null for an invoice with no payment on record.</summary>
    private static object? PaymentMethod(string? paymentType, string? lastFourDigits)
    {
        // An unpaid invoice has no method to describe.
        if (paymentType == null || lastFourDigits == null)
            return null;

        // The method with the sentence a customer recognises; an unknown type is named as stored.
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

    /// <summary>What the books say when they hold nothing under a customer code.</summary>
    private const string NothingUnderThisCode =
        "The accounts book holds no record under this customer code. Nothing on these pages identifies the right "
        + "one: the code names no account here, or it is mistyped.";

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>Retrieves the most recent invoices issued to a customer as structured JSON.</summary>
    [Description("Retrieves the customer's recent nursery invoices as structured JSON. Returns: customerCode, totalCount and array of invoice summaries with invoiceId, period, dates, total, status (with icon) and daysOverdue if pending. A code the books hold nothing under comes back with an empty list and says so — that is an answer, not a failure. This tool has only informative capabilities: you will NOT find here dispositive actions (e.g: 'Pay invoice', 'Dispute payment', ...).")]
    public async Task<string> GetInvoices(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode,
        [Description("Number of recent invoices to retrieve (1-10). Defaults to 3-4 if user doesn't specify. Use higher numbers (6-10) when user explicitly asks for 'all' or 'complete history'.")] [ToolParameter(Records.ToolScope.Request)] int count)
    {
        // The books are the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // The model may ask for any number; the books hand out between one and ten.
        count = Math.Clamp(count, 1, 10);

        // The customer's invoices, most recent first, the code matched ignoring case as the customer types it.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {InvoiceColumns} FROM Invoices WHERE CustomerCode = $customerCode COLLATE NOCASE ORDER BY IssueDate DESC LIMIT $count";
        command.Parameters.AddWithValue("$customerCode", customerCode);
        command.Parameters.AddWithValue("$count", count);

        // Every invoice the query returns.
        List<Invoice> invoices = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                invoices.Add(ReadInvoice(reader));
        }

        // A code the books hold nothing under is an answer, not a failure.
        if (invoices.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                customerCode,
                totalCount = 0,
                invoices = Array.Empty<object>(),
                note = NothingUnderThisCode
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // One summary per invoice, dates as the shop writes them; days overdue only for an invoice still pending.
        object result = new
        {
            customerCode,
            totalCount = invoices.Count,
            invoices = invoices.Select(invoice => new
            {
                invoiceId = invoice.InvoiceId,
                period = PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
                issueDate = invoice.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                dueDate = invoice.DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                total = invoice.Total,
                status = invoice.Status,
                statusIcon = StatusIcon(invoice.Status),
                paidDate = invoice.PaidDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                daysOverdue = invoice.Status == "Pending"
                    ? Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days)
                    : (int?)null
            }).ToList()
        };

        // The model reads the result as compact camelCase JSON.
        return JsonSerializer.Serialize(result, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Retrieves the whole of one invoice of the customer as structured JSON: its lines, its amounts and how it was paid.</summary>
    [Description("Retrieves complete invoice details as structured JSON including: invoice metadata, dates, status (with payment timeline), charge lines array (each with description, quantity, unit price, amount and the orderId the charge refers to, where it refers to one), amounts breakdown (subtotal, tax, total) and payment method. Only the invoices of that same customer are readable: any other invoiceId is reported as not found, with the list of the ones that are. This tool has only informative capabilities: you will NOT find here dispositive actions (e.g: 'Pay invoice', 'Dispute payment', ...).")]
    public async Task<string> GetInvoiceDetails(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode,
        [Description("Unique alphanumeric identifier of the invoice to retrieve (e.g., 'INV-0512'). The user should specify which invoice they want to examine, or you should ask them to choose from the list of available invoices. Do NOT guess or assume which invoice the user wants.")] [ToolParameter(Records.ToolScope.Request)] string invoiceId)
    {
        // The books are the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // Scoped to the customer, not merely looked up by id: one customer's invoice is never
        // readable by quoting its number in another customer's conversation and an invoice that
        // belongs to someone else is reported exactly as one that does not exist.
        Invoice? invoice = null;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {InvoiceColumns} FROM Invoices WHERE InvoiceId = $invoiceId COLLATE NOCASE AND CustomerCode = $customerCode COLLATE NOCASE";
            command.Parameters.AddWithValue("$invoiceId", invoiceId);
            command.Parameters.AddWithValue("$customerCode", customerCode);

            // At most one invoice answers to that number for that customer.
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                invoice = ReadInvoice(reader);
        }

        // An invoice not found is answered with the customer's own invoices, so the model can offer the right one.
        if (invoice == null)
        {
            // The customer's invoice numbers, most recent first.
            await using SqliteCommand available = connection.CreateCommand();
            available.CommandText = "SELECT InvoiceId FROM Invoices WHERE CustomerCode = $customerCode COLLATE NOCASE ORDER BY IssueDate DESC";
            available.Parameters.AddWithValue("$customerCode", customerCode);

            // Every number the query returns.
            List<string> invoiceIds = [];
            await using (SqliteDataReader reader = await available.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    invoiceIds.Add(reader.GetString(0));
            }

            // A customer with no invoice at all is told the books hold nothing under the code.
            return JsonSerializer.Serialize(new
            {
                error = "Invoice not found",
                requestedInvoiceId = invoiceId,
                availableInvoices = invoiceIds,
                note = invoiceIds.Count == 0 ? NothingUnderThisCode : null
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // The lines of the invoice and how far its due date is, negative once it has passed.
        List<InvoiceLine> lines = await GetInvoiceLinesAsync(connection, invoice.InvoiceId);
        int daysUntilDue = (invoice.DueDate - DateTime.UtcNow).Days;

        // The whole invoice: dates, status with its timeline, lines, amounts and payment method.
        object result = new
        {
            invoiceId = invoice.InvoiceId,
            customerCode = invoice.CustomerCode,
            period = PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
            dates = new
            {
                issueDate = invoice.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                dueDate = invoice.DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                paidDate = invoice.PaidDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
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
            // for a plant and an accounts agent that hands it out starts being asked catalog
            // questions. OrderId is a different thing: a reference to what was billed, which is
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
                    ? string.Create(CultureInfo.InvariantCulture, $"{line.Quantity} {line.Unit} × €{line.UnitPrice:F2}")
                    : null
            }).ToList(),
            amounts = new
            {
                subtotal = invoice.Subtotal,
                tax = invoice.Tax,
                taxRate = string.Create(CultureInfo.InvariantCulture, $"{invoice.TaxRate * 100:0.##}%"),
                total = invoice.Total
            },
            paymentMethod = PaymentMethod(invoice.PaymentType, invoice.PaymentLastFour)
        };

        // The model reads the result as compact camelCase JSON.
        return JsonSerializer.Serialize(result, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Retrieves the payments a customer has actually made over recent months as structured JSON.</summary>
    [Description("Retrieves the payments the customer has actually made, as structured JSON including: summary statistics (totalPayments, totalAmount, averageMonthly) and payments array with invoice details. Unpaid invoices are not payments and never appear here. This tool has only informative capabilities: you will NOT find here dispositive actions (e.g: 'Withdraw payment', ...).")]
    public async Task<string> GetPaymentHistory(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode,
        [Description("Number of months of payment history to retrieve (1-12). Defaults to 6 months if not specified. Use higher values (10-12) when user asks for 'complete' or 'full year' history.")] [ToolParameter(Records.ToolScope.Request)] int months = 6)
    {
        // The books are the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // The model may ask for any span; the books hand out between one and twelve months back from today.
        months = Math.Clamp(months, 1, 12);
        DateTime cutoffDate = DateTime.UtcNow.AddMonths(-months);

        // A payment is a paid invoice: one left unpaid never appears here.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {InvoiceColumns} FROM Invoices WHERE CustomerCode = $customerCode COLLATE NOCASE AND Status = 'Paid' AND PaidDate >= $cutoff ORDER BY PaidDate DESC";
        command.Parameters.AddWithValue("$customerCode", customerCode);
        command.Parameters.AddWithValue("$cutoff", cutoffDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        // Every payment the query returns, most recent first.
        List<Invoice> payments = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                payments.Add(ReadInvoice(reader));
        }

        // No payment in the span is an answer, said in as many words.
        if (payments.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                customerCode,
                months,
                hasData = false,
                message = $"No payment received in the last {months} months under this customer code."
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // The total is summed here, never left to the model.
        decimal totalPaid = payments.Sum(payment => payment.Total);

        // The summary with every payment and the method it came by.
        object result = new
        {
            customerCode,
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
                paidDate = payment.PaidDate!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                paymentMethod = PaymentMethod(payment.PaymentType, payment.PaymentLastFour)
            }).ToList()
        };

        // The model reads the result as compact camelCase JSON.
        return JsonSerializer.Serialize(result, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Sums what the customer still owes: the invoices left unpaid, oldest first.</summary>
    [Description("Retrieves what the customer still owes as structured JSON: hasOutstanding, totalDue (summed by this tool — never add invoice amounts up yourself), invoiceCount, oldestDueDate, daysOverdue and the unpaid invoices making up the total. Returns hasOutstanding=false when every invoice has been settled. This tool has only informative capabilities: you will NOT find here dispositive actions (e.g: 'Pay now', 'Agree a payment plan', ...).")]
    public async Task<string> GetOutstandingBalance(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode)
    {
        // The books are the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // What is owed is every invoice neither paid nor cancelled, the one falling due first leading.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {InvoiceColumns} FROM Invoices WHERE CustomerCode = $customerCode COLLATE NOCASE AND Status <> 'Paid' AND Status <> 'Cancelled' ORDER BY DueDate";
        command.Parameters.AddWithValue("$customerCode", customerCode);

        // Every unpaid invoice the query returns.
        List<Invoice> unpaid = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                unpaid.Add(ReadInvoice(reader));
        }

        // Nothing owed is an answer, with a total of zero.
        if (unpaid.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                customerCode,
                hasOutstanding = false,
                totalDue = 0m,
                message = "Nothing is outstanding under this customer code: either every invoice has been settled, or the books hold none."
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // The sum is computed here rather than left to whoever reads the list: money that a
        // customer is told they owe is not a figure to be added up in prose and a total that
        // disagrees with the invoices under it is worse than no total at all.
        decimal totalDue = unpaid.Sum(invoice => invoice.Total);

        // The days past due of the invoice most overdue, zero when none is.
        int worstDaysOverdue = unpaid.Max(invoice => Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days));

        // The total with the invoices making it up.
        object result = new
        {
            customerCode,
            hasOutstanding = true,
            totalDue,
            invoiceCount = unpaid.Count,
            oldestDueDate = unpaid[0].DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            daysOverdue = worstDaysOverdue > 0 ? worstDaysOverdue : (int?)null,
            invoices = unpaid.Select(invoice => new
            {
                invoiceId = invoice.InvoiceId,
                period = PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd),
                dueDate = invoice.DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                total = invoice.Total,
                status = invoice.Status,
                statusIcon = StatusIcon(invoice.Status),
                daysOverdue = Math.Max(0, -(invoice.DueDate - DateTime.UtcNow).Days)
            }).ToList()
        };

        // The model reads the result as compact camelCase JSON.
        return JsonSerializer.Serialize(result, ExamplesDatabaseHelper.JsonOptions);
    }
}