using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.Providers;
using System.Text;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Examples.Tools;

/// <summary>
/// Professional billing tool with structured domain objects and comprehensive invoice management.
/// Provides realistic telecom billing data with line items, taxes, and payment history.
/// </summary>
[ProvidesToolForIntent("billing")]
public class BillingTool : MorganaTool
{
    public BillingTool(
        ILogger toolLogger,
        Func<MorganaContextProvider> getContextProvider) : base(toolLogger, getContextProvider) { }

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
    /// Retrieves the most recent invoices for a user.
    /// Returns detailed invoice information including line items and payment status.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="count">Number of recent invoices to retrieve (1-10)</param>
    /// <returns>Formatted invoice list with summary information</returns>
    public async Task<string> GetInvoices(string userId, int count)
    {
        await Task.Delay(150); // Simulate database query

        count = Math.Clamp(count, 1, 10);
        List<Invoice> invoices = _mockInvoices.Take(count).ToList();

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"📄 **Recent Invoices for User {userId}** (Showing {invoices.Count})");
        sb.AppendLine();

        foreach (Invoice invoice in invoices)
        {
            string statusIcon = invoice.Status switch
            {
                InvoiceStatus.Paid => "✅",
                InvoiceStatus.Pending => "⏳",
                InvoiceStatus.Overdue => "⚠️",
                InvoiceStatus.Cancelled => "❌",
                _ => "📋"
            };

            sb.AppendLine($"{statusIcon} **{invoice.InvoiceId}**");
            sb.AppendLine($"   Period: {invoice.Period}");
            sb.AppendLine($"   Amount: €{invoice.Total:F2}");
            sb.AppendLine($"   Status: {invoice.Status}");
            
            if (invoice.Status == InvoiceStatus.Pending)
            {
                sb.AppendLine($"   Due Date: {invoice.DueDate:dd/MM/yyyy}");
            }
            else if (invoice.PaidDate.HasValue)
            {
                sb.AppendLine($"   Paid: {invoice.PaidDate.Value:dd/MM/yyyy}");
            }
            
            sb.AppendLine();
        }

        sb.AppendLine($"💡 To see detailed line items for any invoice, ask me about a specific invoice ID (e.g., '{invoices.First().InvoiceId}').");

        return sb.ToString();
    }

    /// <summary>
    /// Retrieves detailed information about a specific invoice including all line items.
    /// Provides comprehensive breakdown of charges, taxes, and payment details.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="invoiceId">Specific invoice identifier (e.g., "INV-2025-001")</param>
    /// <returns>Detailed invoice breakdown with all line items</returns>
    public async Task<string> GetInvoiceDetails(string userId, string invoiceId)
    {
        await Task.Delay(100); // Simulate database query

        Invoice? invoice = _mockInvoices.FirstOrDefault(inv => 
            inv.InvoiceId.Equals(invoiceId, StringComparison.OrdinalIgnoreCase));

        if (invoice == null)
        {
            return $"❌ Invoice '{invoiceId}' not found for user {userId}. " +
                   $"Available invoices: {string.Join(", ", _mockInvoices.Select(i => i.InvoiceId))}";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"📄 **Invoice Details: {invoice.InvoiceId}**");
        sb.AppendLine();
        sb.AppendLine($"**Billing Period:** {invoice.Period}");
        sb.AppendLine($"**Issue Date:** {invoice.IssueDate:dd/MM/yyyy}");
        sb.AppendLine($"**Due Date:** {invoice.DueDate:dd/MM/yyyy}");
        sb.AppendLine($"**Status:** {GetStatusDescription(invoice.Status)}");
        
        if (invoice.PaidDate.HasValue)
        {
            sb.AppendLine($"**Paid Date:** {invoice.PaidDate.Value:dd/MM/yyyy}");
        }
        
        if (invoice.PaymentMethod != null)
        {
            sb.AppendLine($"**Payment Method:** {FormatPaymentMethod(invoice.PaymentMethod)}");
        }
        
        sb.AppendLine();
        sb.AppendLine("**Line Items:**");
        sb.AppendLine("```");
        
        foreach (InvoiceLineItem item in invoice.LineItems)
        {
            string quantityInfo = item.Quantity > 1 
                ? $"{item.Quantity} {item.Unit} × €{item.UnitPrice:F2}"
                : "";
            
            string amountSign = item.Amount < 0 ? "" : " ";
            sb.AppendLine($"{item.Description,-50} {quantityInfo,-20} {amountSign}€{item.Amount,8:F2}");
        }
        
        sb.AppendLine(new string('-', 80));
        sb.AppendLine($"{"Subtotal",-70} €{invoice.Subtotal,8:F2}");
        sb.AppendLine($"{"Tax (22%)",-70} €{invoice.Tax,8:F2}");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine($"{"TOTAL",-70} €{invoice.Total,8:F2}");
        sb.AppendLine("```");

        if (invoice.Status == InvoiceStatus.Pending)
        {
            sb.AppendLine();
            int daysUntilDue = (invoice.DueDate - DateTime.Now).Days;
            if (daysUntilDue > 0)
            {
                sb.AppendLine($"⏰ **Payment due in {daysUntilDue} days**");
            }
            else
            {
                sb.AppendLine($"⚠️ **Payment is {Math.Abs(daysUntilDue)} days overdue**");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Retrieves payment history for a user showing all settled invoices.
    /// Useful for financial tracking and payment verification.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="months">Number of months of history to retrieve (1-12)</param>
    /// <returns>Payment history with dates, amounts, and methods</returns>
    public async Task<string> GetPaymentHistory(string userId, int months = 6)
    {
        await Task.Delay(120);

        months = Math.Clamp(months, 1, 12);
        DateTime cutoffDate = DateTime.Now.AddMonths(-months);
        
        List<Invoice> paidInvoices = _mockInvoices
            .Where(i => i.Status == InvoiceStatus.Paid && i.PaidDate >= cutoffDate)
            .OrderByDescending(i => i.PaidDate)
            .ToList();

        if (!paidInvoices.Any())
        {
            return $"📊 No payment history found for the last {months} months.";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"💳 **Payment History for User {userId}** (Last {months} months)");
        sb.AppendLine();

        decimal totalPaid = 0m;

        foreach (Invoice invoice in paidInvoices)
        {
            sb.AppendLine($"✅ **{invoice.InvoiceId}** - {invoice.Period}");
            sb.AppendLine($"   Amount: €{invoice.Total:F2}");
            sb.AppendLine($"   Paid: {invoice.PaidDate!.Value:dd/MM/yyyy}");
            sb.AppendLine($"   Method: {FormatPaymentMethod(invoice.PaymentMethod!)}");
            sb.AppendLine();

            totalPaid += invoice.Total;
        }

        sb.AppendLine($"**Total Paid (Last {months} months):** €{totalPaid:F2}");
        sb.AppendLine($"**Average Monthly Spend:** €{totalPaid / paidInvoices.Count:F2}");

        return sb.ToString();
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private static string GetStatusDescription(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Paid => "✅ Paid",
        InvoiceStatus.Pending => "⏳ Pending Payment",
        InvoiceStatus.Overdue => "⚠️ Overdue",
        InvoiceStatus.Cancelled => "❌ Cancelled",
        _ => status.ToString()
    };

    private static string FormatPaymentMethod(PaymentMethod method) => method.Type switch
    {
        "CreditCard" => $"Credit Card ending in {method.LastFourDigits}",
        "BankTransfer" => $"Bank Transfer from account ending in {method.LastFourDigits}",
        "DirectDebit" => $"Direct Debit from account ending in {method.LastFourDigits}",
        _ => $"{method.Type} ({method.LastFourDigits})"
    };
}