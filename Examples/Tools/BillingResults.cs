using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="BillingTool.GetInvoices"/> returns: the recent invoices of a customer, or a note saying the books hold none.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record InvoicesResult(
    [Description("The customer code the books were asked about")] string CustomerCode,
    [Description("The customer's name when the shop knows the code")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    [Description("How many invoices are listed")] int TotalCount,
    [Description("The invoices, most recent first, each with period, dates, total, status and days overdue")] List<InvoiceSummary> Invoices,
    [Description("Says that the books hold nothing under the code")] string? Note = null);

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
    [Description("Why no invoice is returned, present only when the invoice was not found")] string? Error = null,
    [Description("The invoice identifier")] string? InvoiceId = null,
    [Description("The customer the invoice was issued to")] string? CustomerCode = null,
    [Description("The customer's name when the shop knows the code")] string? CustomerName = null,
    [Description("The period the invoice covers")] string? Period = null,
    [Description("Issue date, due date and paid date")] InvoiceDates? Dates = null,
    [Description("Status with icon, description and days until due or overdue")] InvoiceStatus? Status = null,
    [Description("The charge lines, each with description, order reference, unit price, quantity and amount")] List<InvoiceLineItem>? LineItems = null,
    [Description("Subtotal, tax, tax rate and total")] InvoiceAmounts? Amounts = null,
    [Description("How the invoice was paid, absent while it is unpaid")] PaymentMethodInfo? PaymentMethod = null,
    [Description("The invoice identifier that was asked for, on an error")] string? RequestedInvoiceId = null,
    [Description("The invoice identifiers the customer does have, on an error")] List<string>? AvailableInvoices = null,
    [Description("Says that the books hold nothing under the code")] string? Note = null);

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
    [Description("The customer code the books were asked about")] string CustomerCode,
    [Description("The customer's name when the shop knows the code")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    [Description("Whether any invoice is left unpaid")] bool HasOutstanding,
    [Description("The sum of the unpaid invoices")] decimal TotalDue,
    [Description("How many invoices are unpaid")] int? InvoiceCount = null,
    [Description("The earliest due date among the unpaid invoices")] string? OldestDueDate = null,
    [Description("The longest delay among the unpaid invoices, absent when none is late")] int? DaysOverdue = null,
    [Description("The unpaid invoices, oldest due first")] List<OutstandingInvoice>? Invoices = null,
    [Description("Says that nothing is outstanding")] string? Message = null);

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
    [Description("The customer code the books were asked about")] string CustomerCode,
    [Description("The customer's name when the shop knows the code")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    [Description("The number of months of history that was read")] int Months,
    [Description("Whether any payment was received in that window")] bool HasData,
    [Description("Payment count, total amount and monthly average")] PaymentSummary? Summary = null,
    [Description("The payments received, most recent first")] List<PaymentEntry>? Payments = null,
    [Description("Says that no payment was received in the window")] string? Message = null);

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
