using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="ContractTool.GetPlanOverview"/> returns: the terms of the Green Care Plan itself.</summary>
public record PlanOverviewResult(
    [Description("The plan product code")] string PlanCode,
    [Description("The plan product name")] string Name,
    [Description("How often the garden is tended")] string VisitFrequency,
    [Description("What the plan covers")] string Coverage,
    [Description("The plant-health guarantee")] string Guarantee,
    [Description("The monthly fee")] decimal MonthlyFee,
    [Description("The features the plan includes")] List<string> IncludedFeatures,
    [Description("The days of notice that ending the plan requires")] int NoticePeriodDays,
    [Description("The fee for ending the plan early")] decimal EarlyTerminationFee);

/// <summary>What <see cref="ContractTool.SubscribeToGreenCarePlan"/> returns: the plan just opened, or the error saying that the customer already holds one.</summary>
public record SubscriptionResult(
    [Description("Why no plan was opened, present only when the customer already holds one")] string? Error = null,
    [Description("The contract the customer already holds, on an error")] string? ExistingContractId = null,
    [Description("The new contract identifier")] string? ContractId = null,
    [Description("The customer that was enrolled")] string? CustomerCode = null,
    [Description("The plan product code")] string? PlanCode = null,
    [Description("The plan product name")] string? PlanName = null,
    [Description("The status of the contract")] string? Status = null,
    [Description("The date the contract starts")] string? StartDate = null,
    [Description("The date the contract ends")] string? EndDate = null,
    [Description("The monthly fee that was billed")] decimal? MonthlyFee = null,
    [Description("The days of the month on which visits fall")] string? VisitDays = null,
    [Description("The invoice the first month was billed to")] string? InvoiceId = null,
    [Description("What the response does and does not carry")] string? Note = null);

/// <summary>What <see cref="ContractTool.GetContractDetails"/> returns: the customer's plan in full, or the error saying that none is held.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record ContractDetailsResult(
    [Description("Why no plan is returned, present only when none is held")] string? Error = null,
    [Description("The contract identifier")] string? ContractId = null,
    [Description("The customer the contract belongs to")] string? CustomerCode = null,
    [Description("The customer's name when the shop knows the code")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName = null,
    [Description("Contract status with icon")] ContractStatus? Status = null,
    [Description("Name, visit frequency, coverage, guarantee and included features")] ContractPlan? Plan = null,
    [Description("Start and end dates with the time remaining")] ContractPeriod? ContractPeriod = null,
    [Description("Monthly fee and billing cycle")] ContractFee? Fee = null,
    [Description("The services of the plan, each marked optional or required")] List<ContractService>? Services = null,
    [Description("Notice period, early termination fee and automatic renewal")] ContractTermination? Termination = null,
    [Description("The clauses of the plan with number, title and type")] List<ClauseListing>? AvailableClauses = null,
    [Description("What the response does and does not carry")] string? Note = null);

/// <summary>The status of a contract with its icon.</summary>
public record ContractStatus(string Value, string Icon);

/// <summary>The plan product a contract is an instance of.</summary>
public record ContractPlan(
    string Name,
    string VisitFrequency,
    string Coverage,
    string Guarantee,
    List<string> IncludedFeatures);

/// <summary>The dates a contract runs over and what is left of them.</summary>
public record ContractPeriod(
    string StartDate,
    string EndDate,
    int RemainingDays,
    int RemainingMonths);

/// <summary>The monthly fee of a contract and how it is billed.</summary>
public record ContractFee(decimal MonthlyFee, string BillingCycle);

/// <summary>One service of the plan.</summary>
public record ContractService(
    string ServiceId,
    string Name,
    string Description,
    decimal MonthlyCost,
    bool IsOptional,
    string Category);

/// <summary>How a contract ends and what renews it.</summary>
public record ContractTermination(
    int NoticePeriodDays,
    decimal EarlyTerminationFee,
    AutoRenewal AutoRenewal);

/// <summary>The automatic renewal of a contract.</summary>
public record AutoRenewal(bool Enabled, int NoticeDays, string RenewalDate);

/// <summary>One clause of the plan as a list shows it.</summary>
public record ClauseListing(int ClauseNumber, string Title, string Type);

/// <summary>One clause of the plan as the list of what exists shows it.</summary>
public record ClauseHeading(int ClauseNumber, string Title);

/// <summary>What <see cref="ContractTool.GetContractClause"/> returns: one clause, or the error saying that no plan is held or that the clause does not exist.</summary>
public record ContractClauseResult(
    [Description("Why no clause is returned, present only when no plan is held or the clause does not exist")] string? Error = null,
    [Description("The customer code the books were asked about, on an error")] string? CustomerCode = null,
    [Description("The customer's name when the shop knows the code, on an error")] string? CustomerName = null,
    [Description("What the response does and does not carry")] string? Note = null,
    [Description("The contract identifier")] string? ContractId = null,
    [Description("The clause number")] int? ClauseNumber = null,
    [Description("The clause title")] string? Title = null,
    [Description("The clause type")] string? Type = null,
    [Description("The clause summary")] string? Summary = null,
    [Description("The full text of the clause")] string? FullText = null,
    [Description("Where a related matter is read")] string? RelatedInfo = null,
    [Description("The clause number that was asked for, on an error")] int? RequestedClauseNumber = null,
    [Description("The clauses that exist, on an error")] List<ClauseHeading>? AvailableClauses = null);

/// <summary>What <see cref="ContractTool.GetVisitSchedule"/> returns: the tending calendar of the customer, or the error saying that no plan is held.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record VisitScheduleResult(
    [Description("Why no calendar is returned, present only when no plan is held")] string? Error = null,
    [Description("The contract identifier")] string? ContractId = null,
    [Description("The customer code the books were asked about, on an error")] string? CustomerCode = null,
    [Description("The customer's name when the shop knows the code")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName = null,
    [Description("How often the garden is tended")] string? VisitFrequency = null,
    [Description("The days of the month on which visits fall")] string? VisitDays = null,
    [Description("Included visits per month, taken, remaining and the fee of an extra visit")] VisitAllowance? Allowance = null,
    [Description("The next visits with date and days away")] List<UpcomingVisit>? Upcoming = null,
    [Description("The visits already made, most recent first")] List<RecentVisit>? RecentVisits = null,
    [Description("What the response does and does not carry")] string? Note = null);

/// <summary>The visits included in a month and how many are left.</summary>
public record VisitAllowance(
    int IncludedPerMonth,
    int TakenThisMonth,
    int RemainingThisMonth,
    decimal ExtraVisitFee);

/// <summary>A visit still to come.</summary>
public record UpcomingVisit(string Date, int DaysAway, string Kind);

/// <summary>A visit already made.</summary>
/// <remarks>The invoice is written even when null, since a null says that the visit was not charged.</remarks>
public record RecentVisit(
    string VisitId,
    string Date,
    string Kind,
    string Outcome,
    string OutcomeIcon,
    string Notes,
    bool Charged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InvoiceId);

/// <summary>What <see cref="ContractTool.GetTerminationProcedure"/> returns: how the plan is ended, or the error saying that no plan is held.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record TerminationProcedureResult(
    [Description("Why no procedure is returned, present only when no plan is held")] string? Error = null,
    [Description("The contract identifier")] string? ContractId = null,
    [Description("The customer code the books were asked about, on an error")] string? CustomerCode = null,
    [Description("The customer's name when the shop knows the code")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName = null,
    [Description("The termination reason that was given")] string? Reason = null,
    [Description("Required days and earliest effective date")] NoticePeriod? NoticePeriod = null,
    [Description("Early termination fee and waiver eligibility")] TerminationFees? Fees = null,
    [Description("The numbered steps and the required documents")] TerminationProcedure? Procedure = null,
    [Description("The refund policy of the plan")] string? RefundPolicy = null,
    [Description("Conditions that apply to a termination")] List<string>? ImportantNotes = null,
    [Description("What the response does and does not carry")] string? Note = null);

/// <summary>The notice that ending a plan requires.</summary>
public record NoticePeriod(int RequiredDays, string EarliestEffectiveDate);

/// <summary>What ending a plan costs and when the cost is waived.</summary>
public record TerminationFees(EarlyTerminationFee EarlyTermination, WaiverEligibility WaiverEligibility);

/// <summary>The fee for ending a plan before its end date.</summary>
public record EarlyTerminationFee(bool Applicable, decimal Amount, string Reason);

/// <summary>Whether the early termination fee can be waived and on what conditions.</summary>
public record WaiverEligibility(bool Available, List<string> Conditions);

/// <summary>The steps and documents for ending a plan.</summary>
public record TerminationProcedure(List<TerminationStep> Steps, List<string> RequiredDocuments);

/// <summary>One numbered step of the termination procedure.</summary>
public record TerminationStep(int StepNumber, string Description);
