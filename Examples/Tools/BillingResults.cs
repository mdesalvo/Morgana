using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="BillingTool.GetInvoices"/> returns: the recent invoices of a customer, or a note saying the books hold none.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record InvoicesResult(
    string CustomerCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    int TotalCount,
    List<InvoiceSummary> Invoices,
    string? Note = null);

/// <summary>One invoice in a list, as <see cref="InvoicesResult"/> carries it.</summary>
/// <remarks>The paid date and the days overdue are written even when null, since a null says that the invoice is not paid or not late.</remarks>
public record InvoiceSummary(
    string InvoiceId,
    string Period,
    string IssueDate,
    string DueDate,
    decimal Total,
    string Status,
    string StatusIcon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PaidDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DaysOverdue);

/// <summary>What <see cref="BillingTool.GetInvoiceDetails"/> returns: one invoice in full, or the error naming what was asked and what exists.</summary>
public record InvoiceDetailsResult(
    string? Error = null,
    string? InvoiceId = null,
    string? CustomerCode = null,
    string? CustomerName = null,
    string? Period = null,
    InvoiceDates? Dates = null,
    InvoiceStatus? Status = null,
    List<InvoiceLineItem>? LineItems = null,
    InvoiceAmounts? Amounts = null,
    PaymentMethodInfo? PaymentMethod = null,
    string? RequestedInvoiceId = null,
    List<string>? AvailableInvoices = null,
    string? Note = null);

/// <summary>The dates of an invoice.</summary>
/// <remarks>The paid date is written even when null, since a null says that the invoice is not paid.</remarks>
public record InvoiceDates(
    string IssueDate,
    string DueDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PaidDate);

/// <summary>Where an invoice stands: its status and how late it is.</summary>
/// <remarks>The days until due and the days overdue are written even when null, since a null says that the invoice is not pending or not late.</remarks>
public record InvoiceStatus(
    string Value,
    string Icon,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DaysUntilDue,
    bool IsOverdue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DaysOverdue);

/// <summary>One line of an invoice.</summary>
/// <remarks>The order and the formatted quantity are written even when null, since a null says that the line has no order or a single unit.</remarks>
public record InvoiceLineItem(
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OrderId,
    decimal UnitPrice,
    int Quantity,
    string Unit,
    decimal Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FormattedQuantity);

/// <summary>The money of an invoice.</summary>
public record InvoiceAmounts(
    decimal Subtotal,
    decimal Tax,
    string TaxRate,
    decimal Total);

/// <summary>How an invoice was paid.</summary>
public record PaymentMethodInfo(
    string Type,
    string LastFourDigits,
    string Formatted);

/// <summary>What <see cref="BillingTool.GetOutstandingBalance"/> returns: what the customer still owes, or a message saying that nothing is outstanding.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record OutstandingBalanceResult(
    string CustomerCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    bool HasOutstanding,
    decimal TotalDue,
    int? InvoiceCount = null,
    string? OldestDueDate = null,
    int? DaysOverdue = null,
    List<OutstandingInvoice>? Invoices = null,
    string? Message = null);

/// <summary>One unpaid invoice of an outstanding balance.</summary>
public record OutstandingInvoice(
    string InvoiceId,
    string Period,
    string DueDate,
    decimal Total,
    string Status,
    string StatusIcon,
    int DaysOverdue);

/// <summary>What <see cref="BillingTool.GetPaymentHistory"/> returns: the payments received in a window of months, or a message saying that there were none.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record PaymentHistoryResult(
    string CustomerCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    int Months,
    bool HasData,
    PaymentSummary? Summary = null,
    List<PaymentEntry>? Payments = null,
    string? Message = null);

/// <summary>The totals of a payment history.</summary>
public record PaymentSummary(
    int TotalPayments,
    decimal TotalAmount,
    decimal AverageMonthly);

/// <summary>One payment received against an invoice.</summary>
/// <remarks>The payment method is written even when null, since a null says that the books record no method.</remarks>
public record PaymentEntry(
    string InvoiceId,
    string Period,
    decimal Amount,
    string PaidDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PaymentMethodInfo? PaymentMethod);
