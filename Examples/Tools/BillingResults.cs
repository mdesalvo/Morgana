using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="BillingTool.GetInvoices"/> returns: the recent invoices of a customer, or a note saying the books hold none.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
[Description("The recent invoices of a customer.")]
public record InvoicesResult(
    [Description("The customer code the books were asked about.")] string CustomerCode,
    [Description("The customer's name; null when the shop does not know the code.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    [Description("How many invoices are listed.")] int TotalCount,
    [Description("The invoices, most recent first.")] List<InvoiceSummary> Invoices,
    [Description("States that the books hold nothing under the code; null otherwise.")] string? Note = null);

/// <summary>One invoice in a list, as <see cref="InvoicesResult"/> carries it.</summary>
/// <remarks>The paid date and the days overdue are written even when null, since a null says that the invoice is not paid or not late.</remarks>
[Description("One invoice in a list.")]
public record InvoiceSummary(
    [Description("The invoice identifier.")] string InvoiceId,
    [Description("The months that the invoice covers.")] string Period,
    [Description("The date the invoice was issued, as dd/MM/yyyy.")] string IssueDate,
    [Description("The date the invoice falls due, as dd/MM/yyyy.")] string DueDate,
    [Description("The amount of the invoice in euros, tax included.")] decimal Total,
    [Description("Where the invoice stands: Paid, Pending, Overdue or Cancelled.")] string Status,
    [Description("The icon that goes with the status.")] string StatusIcon,
    [Description("The date the invoice was paid, as dd/MM/yyyy; null while it is unpaid.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PaidDate,
    [Description("How many days past its due date the invoice is; null when it is not late.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DaysOverdue);

/// <summary>What <see cref="BillingTool.GetInvoiceDetails"/> returns: one invoice in full, or the error naming what was asked and what exists.</summary>
[Description("One invoice in full.")]
public record InvoiceDetailsResult(
    [Description("Why no invoice is returned; null when the invoice was found.")] string? Error = null,
    [Description("The invoice identifier.")] string? InvoiceId = null,
    [Description("The customer the invoice was issued to.")] string? CustomerCode = null,
    [Description("The customer's name; null when the shop does not know the code.")] string? CustomerName = null,
    [Description("The months that the invoice covers.")] string? Period = null,
    [Description("The dates of the invoice.")] InvoiceDates? Dates = null,
    [Description("Where the invoice stands.")] InvoiceStatus? Status = null,
    [Description("The charge lines of the invoice.")] List<InvoiceLineItem>? LineItems = null,
    [Description("The money of the invoice.")] InvoiceAmounts? Amounts = null,
    [Description("How the invoice was paid; null while it is unpaid.")] PaymentMethodInfo? PaymentMethod = null,
    [Description("The invoice identifier that was asked for; present only on an error.")] string? RequestedInvoiceId = null,
    [Description("The invoice identifiers that the customer does have; present only on an error.")] List<string>? AvailableInvoices = null,
    [Description("States that the books hold nothing under the code; null otherwise.")] string? Note = null);

/// <summary>The dates of an invoice.</summary>
/// <remarks>The paid date is written even when null, since a null says that the invoice is not paid.</remarks>
[Description("The dates of an invoice.")]
public record InvoiceDates(
    [Description("The date the invoice was issued, as dd/MM/yyyy.")] string IssueDate,
    [Description("The date the invoice falls due, as dd/MM/yyyy.")] string DueDate,
    [Description("The date the invoice was paid, as dd/MM/yyyy; null while it is unpaid.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PaidDate);

/// <summary>Where an invoice stands: its status and how late it is.</summary>
/// <remarks>The days until due and the days overdue are written even when null, since a null says that the invoice is not pending or not late.</remarks>
[Description("Where an invoice stands.")]
public record InvoiceStatus(
    [Description("The status: Paid, Pending, Overdue or Cancelled.")] string Value,
    [Description("The icon that goes with the status.")] string Icon,
    [Description("The status as worded for a reader.")] string Description,
    [Description("How many days are left before the invoice falls due; null unless it is pending.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DaysUntilDue,
    [Description("True when a pending invoice is past its due date.")] bool IsOverdue,
    [Description("How many days past its due date the invoice is; null when it is not late.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DaysOverdue);

/// <summary>One line of an invoice.</summary>
/// <remarks>The order and the formatted quantity are written even when null, since a null says that the line has no order or a single unit.</remarks>
[Description("One charge line of an invoice.")]
public record InvoiceLineItem(
    [Description("What the line charges for.")] string Description,
    [Description("The order the line bills; null when the line bills no order.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OrderId,
    [Description("The price of one unit in euros.")] decimal UnitPrice,
    [Description("How many units the line charges.")] int Quantity,
    [Description("The unit that the quantity counts.")] string Unit,
    [Description("The amount of the line in euros.")] decimal Amount,
    [Description("The quantity worded with its unit and unit price; null for a single unit.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FormattedQuantity);

/// <summary>The money of an invoice.</summary>
[Description("The money of an invoice.")]
public record InvoiceAmounts(
    [Description("The sum of the lines in euros, before tax.")] decimal Subtotal,
    [Description("The tax in euros.")] decimal Tax,
    [Description("The tax rate, as a percentage.")] string TaxRate,
    [Description("The amount of the invoice in euros, tax included.")] decimal Total);

/// <summary>How an invoice was paid.</summary>
[Description("How an invoice was paid.")]
public record PaymentMethodInfo(
    [Description("The kind of payment: CreditCard, BankTransfer or DirectDebit.")] string Type,
    [Description("The last four digits of the card or the account.")] string LastFourDigits,
    [Description("The payment method worded for a reader.")] string Formatted);

/// <summary>What <see cref="BillingTool.GetOutstandingBalance"/> returns: what the customer still owes, or a message saying that nothing is outstanding.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
[Description("What a customer still owes.")]
public record OutstandingBalanceResult(
    [Description("The customer code the books were asked about.")] string CustomerCode,
    [Description("The customer's name; null when the shop does not know the code.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    [Description("False when every invoice has been settled.")] bool HasOutstanding,
    [Description("What the customer owes in total, already summed by the tool: never a sum to redo from the invoices.")] decimal TotalDue,
    [Description("How many invoices are unpaid; null when none is.")] int? InvoiceCount = null,
    [Description("The earliest due date among the unpaid invoices, as dd/MM/yyyy; null when none is unpaid.")] string? OldestDueDate = null,
    [Description("The longest delay in days among the unpaid invoices; null when none is late.")] int? DaysOverdue = null,
    [Description("The unpaid invoices, oldest due first; null when none is unpaid.")] List<OutstandingInvoice>? Invoices = null,
    [Description("States that nothing is outstanding; null otherwise.")] string? Message = null);

/// <summary>One unpaid invoice of an outstanding balance.</summary>
[Description("One unpaid invoice.")]
public record OutstandingInvoice(
    [Description("The invoice identifier.")] string InvoiceId,
    [Description("The months that the invoice covers.")] string Period,
    [Description("The date the invoice falls due, as dd/MM/yyyy.")] string DueDate,
    [Description("The amount of the invoice in euros, tax included.")] decimal Total,
    [Description("Where the invoice stands: Pending or Overdue.")] string Status,
    [Description("The icon that goes with the status.")] string StatusIcon,
    [Description("How many days past its due date the invoice is; zero when it is not late.")] int DaysOverdue);

/// <summary>What <see cref="BillingTool.GetPaymentHistory"/> returns: the payments received in a window of months, or a message saying that there were none.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
[Description("The payments that a customer made over a window of months.")]
public record PaymentHistoryResult(
    [Description("The customer code the books were asked about.")] string CustomerCode,
    [Description("The customer's name; null when the shop does not know the code.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    [Description("The number of months of history that was read.")] int Months,
    [Description("True when at least one payment was received in that window.")] bool HasData,
    [Description("The totals of the payments; null when there were none.")] PaymentSummary? Summary = null,
    [Description("The payments received, most recent first; null when there were none.")] List<PaymentEntry>? Payments = null,
    [Description("States that no payment was received in the window; null otherwise.")] string? Message = null);

/// <summary>The totals of a payment history.</summary>
[Description("The totals of a payment history.")]
public record PaymentSummary(
    [Description("How many payments were received.")] int TotalPayments,
    [Description("The sum of the payments in euros.")] decimal TotalAmount,
    [Description("The average amount paid per month in euros.")] decimal AverageMonthly);

/// <summary>One payment received against an invoice.</summary>
/// <remarks>The payment method is written even when null, since a null says that the books record no method.</remarks>
[Description("One payment received against an invoice.")]
public record PaymentEntry(
    [Description("The invoice that the payment settled.")] string InvoiceId,
    [Description("The months that the settled invoice covers.")] string Period,
    [Description("The amount paid in euros.")] decimal Amount,
    [Description("The date the payment was received, as dd/MM/yyyy.")] string PaidDate,
    [Description("How the payment was made; null when the books record no method.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PaymentMethodInfo? PaymentMethod);
