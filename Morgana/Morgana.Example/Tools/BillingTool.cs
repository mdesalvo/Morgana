using Microsoft.Extensions.Logging;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Attributes;
using Morgana.Framework.Providers;
using System.Text.Json;

namespace Morgana.Example.Tools;

/// <summary>
/// Professional billing tool with structured domain objects and comprehensive invoice management.
/// Provides realistic telecom billing data with line items, taxes, and payment history.
/// </summary>
[ProvidesToolForIntent("billing")]
public class BillingTool : MorganaTool
{
    public BillingTool(
        ILogger toolLogger,
        Func<MorganaAIContextProvider> getContextProvider) : base(toolLogger, getContextProvider) { }

    // =========================================================================
    // DOMAIN MODELS
    // =========================================================================

    /// <summary>
    /// Represents a telecom invoice with detailed line items and payment information.
    /// </summary>
    public record Invoice(
        string InvoiceId,
        string Period,
        DateTime IssueDate,
        DateTime DueDate,
        decimal Subtotal,
        decimal Tax,
        decimal Total,
        InvoiceStatus Status,
        DateTime? PaidDate,
        List<InvoiceLineItem> LineItems,
        PaymentMethod? PaymentMethod);

    /// <summary>
    /// Individual line item within an invoice (service charge, usage, fee, etc.).
    /// </summary>
    public record InvoiceLineItem(
        string Description,
        decimal UnitPrice,
        int Quantity,
        string Unit,
        decimal Amount);

    /// <summary>
    /// Invoice payment status.
    /// </summary>
    public enum InvoiceStatus
    {
        Pending,
        Paid,
        Overdue,
        Cancelled
    }

    /// <summary>
    /// Payment method used for invoice settlement.
    /// </summary>
    public record PaymentMethod(
        string Type,  // "CreditCard", "BankTransfer", "DirectDebit"
        string LastFourDigits);

    // =========================================================================
    // MOCK DATA
    // =========================================================================

    private readonly List<Invoice> _mockInvoices =
    [
        new Invoice(
            InvoiceId: "INV-2025-001",
            Period: "October 2025",
            IssueDate: new DateTime(2025, 11, 1),
            DueDate: new DateTime(2025, 12, 15),
            Subtotal: 106.56m,
            Tax: 23.44m,
            Total: 130.00m,
            Status: InvoiceStatus.Pending,
            PaidDate: null,
            LineItems:
            [
                new InvoiceLineItem("Premium 100Mbps Plan - Monthly Fee", 90.00m, 1, "month", 90.00m),
                new InvoiceLineItem("Static IP Address Service", 5.00m, 1, "month", 5.00m),
                new InvoiceLineItem("International Calls", 0.12m, 48, "minutes", 5.76m),
                new InvoiceLineItem("SMS Package (200 messages)", 5.00m, 1, "package", 5.00m),
                new InvoiceLineItem("Mobile Data Overage", 0.02m, 40, "MB", 0.80m)
            ],
            PaymentMethod: null),

        new Invoice(
            InvoiceId: "INV-2025-002",
            Period: "September 2025",
            IssueDate: new DateTime(2025, 10, 1),
            DueDate: new DateTime(2025, 11, 15),
            Subtotal: 122.95m,
            Tax: 27.05m,
            Total: 150.00m,
            Status: InvoiceStatus.Paid,
            PaidDate: new DateTime(2025, 11, 14),
            LineItems:
            [
                new InvoiceLineItem("Premium 100Mbps Plan - Monthly Fee", 90.00m, 1, "month", 90.00m),
                new InvoiceLineItem("Static IP Address Service", 5.00m, 1, "month", 5.00m),
                new InvoiceLineItem("Technical Support Call", 25.00m, 1, "call", 25.00m),
                new InvoiceLineItem("Equipment Rental (Router)", 2.95m, 1, "month", 2.95m)
            ],
            PaymentMethod: new PaymentMethod("DirectDebit", "4532")),

        new Invoice(
            InvoiceId: "INV-2025-003",
            Period: "June - August 2025",
            IssueDate: new DateTime(2025, 9, 1),
            DueDate: new DateTime(2025, 10, 15),
            Subtotal: 102.46m,
            Tax: 22.54m,
            Total: 125.00m,
            Status: InvoiceStatus.Paid,
            PaidDate: new DateTime(2025, 10, 13),
            LineItems:
            [
                new InvoiceLineItem("Premium 100Mbps Plan - Quarterly Fee", 270.00m, 1, "quarter", 270.00m),
                new InvoiceLineItem("Quarterly Discount (-60%)", -162.00m, 1, "discount", -162.00m),
                new InvoiceLineItem("Static IP Address Service", 5.00m, 3, "months", 15.00m),
                new InvoiceLineItem("Installation Fee Refund", -25.00m, 1, "refund", -25.00m)
            ],
            PaymentMethod: new PaymentMethod("CreditCard", "8901")),

        new Invoice(
            InvoiceId: "INV-2025-004",
            Period: "May 2025",
            IssueDate: new DateTime(2025, 6, 1),
            DueDate: new DateTime(2025, 7, 15),
            Subtotal: 81.97m,
            Tax: 18.03m,
            Total: 100.00m,
            Status: InvoiceStatus.Paid,
            PaidDate: new DateTime(2025, 9, 13),
            LineItems:
            [
                new InvoiceLineItem("Premium 100Mbps Plan - Monthly Fee", 90.00m, 1, "month", 90.00m),
                new InvoiceLineItem("New Customer Discount (-10%)", -9.00m, 1, "discount", -9.00m),
                new InvoiceLineItem("Installation Fee", 50.00m, 1, "one-time", 50.00m),
                new InvoiceLineItem("Installation Discount (-60%)", -30.00m, 1, "discount", -30.00m),
                new InvoiceLineItem("Activation Fee Waived", -19.03m, 1, "waiver", -19.03m)
            ],
            PaymentMethod: new PaymentMethod("BankTransfer", "7821"))
    ];

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Retrieves the most recent invoices for a user as structured JSON.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="count">Number of recent invoices to retrieve (1-10)</param>
    /// <returns>JSON array of invoice summaries</returns>
    public async Task<string> GetInvoices(string userId, int count)
    {
        await Task.Delay(50);

        count = Math.Clamp(count, 1, 10);
        List<Invoice> invoices = _mockInvoices.Take(count).ToList();

        var result = new
        {
            userId,
            totalCount = invoices.Count,
            invoices = invoices.Select(inv => new
            {
                invoiceId = inv.InvoiceId,
                period = inv.Period,
                issueDate = inv.IssueDate.ToString("dd/MM/yyyy"),
                dueDate = inv.DueDate.ToString("dd/MM/yyyy"),
                total = inv.Total,
                status = inv.Status.ToString(),
                statusIcon = inv.Status switch
                {
                    InvoiceStatus.Paid => "✅",
                    InvoiceStatus.Pending => "⏳",
                    InvoiceStatus.Overdue => "⚠️",
                    InvoiceStatus.Cancelled => "❌",
                    _ => "📋"
                },
                paidDate = inv.PaidDate?.ToString("dd/MM/yyyy"),
                daysOverdue = inv.Status == InvoiceStatus.Pending 
                    ? Math.Max(0, -(inv.DueDate - DateTime.UtcNow).Days)
                    : (int?)null
            }).ToList()
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
    }

    /// <summary>
    /// Retrieves detailed information about a specific invoice as structured JSON.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="invoiceId">Specific invoice identifier (e.g., "INV-2025-001")</param>
    /// <returns>JSON object with complete invoice details</returns>
    public async Task<string> GetInvoiceDetails(string userId, string invoiceId)
    {
        await Task.Delay(50);

        Invoice? invoice = _mockInvoices.FirstOrDefault(inv =>
            inv.InvoiceId.Equals(invoiceId, StringComparison.OrdinalIgnoreCase));

        if (invoice == null)
        {
            var error = new
            {
                error = "Invoice not found",
                requestedInvoiceId = invoiceId,
                availableInvoices = _mockInvoices.Select(i => i.InvoiceId).ToList()
            };
            return JsonSerializer.Serialize(error, new JsonSerializerOptions 
            { 
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });
        }

        int daysUntilDue = (invoice.DueDate - DateTime.UtcNow).Days;

        var result = new
        {
            invoiceId = invoice.InvoiceId,
            period = invoice.Period,
            dates = new
            {
                issueDate = invoice.IssueDate.ToString("dd/MM/yyyy"),
                dueDate = invoice.DueDate.ToString("dd/MM/yyyy"),
                paidDate = invoice.PaidDate?.ToString("dd/MM/yyyy")
            },
            status = new
            {
                value = invoice.Status.ToString(),
                icon = invoice.Status switch
                {
                    InvoiceStatus.Paid => "✅",
                    InvoiceStatus.Pending => "⏳",
                    InvoiceStatus.Overdue => "⚠️",
                    InvoiceStatus.Cancelled => "❌",
                    _ => "📋"
                },
                description = invoice.Status switch
                {
                    InvoiceStatus.Paid => "Paid",
                    InvoiceStatus.Pending => "Pending Payment",
                    InvoiceStatus.Overdue => "Overdue",
                    InvoiceStatus.Cancelled => "Cancelled",
                    _ => invoice.Status.ToString()
                },
                daysUntilDue = invoice.Status == InvoiceStatus.Pending ? daysUntilDue : (int?)null,
                isOverdue = invoice.Status == InvoiceStatus.Pending && daysUntilDue < 0,
                daysOverdue = invoice.Status == InvoiceStatus.Pending && daysUntilDue < 0 
                    ? Math.Abs(daysUntilDue) 
                    : (int?)null
            },
            lineItems = invoice.LineItems.Select(item => new
            {
                description = item.Description,
                unitPrice = item.UnitPrice,
                quantity = item.Quantity,
                unit = item.Unit,
                amount = item.Amount,
                formattedQuantity = item.Quantity > 1 
                    ? $"{item.Quantity} {item.Unit} × €{item.UnitPrice:F2}"
                    : null
            }).ToList(),
            amounts = new
            {
                subtotal = invoice.Subtotal,
                tax = invoice.Tax,
                taxRate = "22%",
                total = invoice.Total
            },
            paymentMethod = invoice.PaymentMethod != null 
                ? new
                {
                    type = invoice.PaymentMethod.Type,
                    lastFourDigits = invoice.PaymentMethod.LastFourDigits,
                    formatted = invoice.PaymentMethod.Type switch
                    {
                        "CreditCard" => $"Credit Card ending in {invoice.PaymentMethod.LastFourDigits}",
                        "BankTransfer" => $"Bank Transfer from account ending in {invoice.PaymentMethod.LastFourDigits}",
                        "DirectDebit" => $"Direct Debit from account ending in {invoice.PaymentMethod.LastFourDigits}",
                        _ => $"{invoice.PaymentMethod.Type} ({invoice.PaymentMethod.LastFourDigits})"
                    }
                }
                : null
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
    }

    /// <summary>
    /// Retrieves payment history for a user as structured JSON.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="months">Number of months of history to retrieve (1-12)</param>
    /// <returns>JSON object with payment history</returns>
    public async Task<string> GetPaymentHistory(string userId, int months = 6)
    {
        await Task.Delay(50);

        months = Math.Clamp(months, 1, 12);
        DateTime cutoffDate = DateTime.UtcNow.AddMonths(-months);

        List<Invoice> paidInvoices = _mockInvoices
            .Where(i => i.Status == InvoiceStatus.Paid && i.PaidDate >= cutoffDate)
            .OrderByDescending(i => i.PaidDate)
            .ToList();

        if (!paidInvoices.Any())
        {
            var noData = new
            {
                userId,
                months,
                hasData = false,
                message = $"No payment history found for the last {months} months."
            };
            return JsonSerializer.Serialize(noData, new JsonSerializerOptions 
            { 
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });
        }

        decimal totalPaid = paidInvoices.Sum(i => i.Total);

        var result = new
        {
            userId,
            months,
            hasData = true,
            summary = new
            {
                totalPayments = paidInvoices.Count,
                totalAmount = totalPaid,
                averageMonthly = totalPaid / paidInvoices.Count
            },
            payments = paidInvoices.Select(inv => new
            {
                invoiceId = inv.InvoiceId,
                period = inv.Period,
                amount = inv.Total,
                paidDate = inv.PaidDate!.Value.ToString("dd/MM/yyyy"),
                paymentMethod = inv.PaymentMethod != null 
                    ? new
                    {
                        type = inv.PaymentMethod.Type,
                        lastFourDigits = inv.PaymentMethod.LastFourDigits,
                        formatted = inv.PaymentMethod.Type switch
                        {
                            "CreditCard" => $"Credit Card ending in {inv.PaymentMethod.LastFourDigits}",
                            "BankTransfer" => $"Bank Transfer (...{inv.PaymentMethod.LastFourDigits})",
                            "DirectDebit" => $"Direct Debit (...{inv.PaymentMethod.LastFourDigits})",
                            _ => $"{inv.PaymentMethod.Type} (...{inv.PaymentMethod.LastFourDigits})"
                        }
                    }
                    : null
            }).ToList()
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
    }
}