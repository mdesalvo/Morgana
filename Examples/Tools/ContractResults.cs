using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="ContractTool.GetPlanOverview"/> returns: the terms of the Green Care Plan itself.</summary>
public record PlanOverviewResult(
    string PlanCode,
    string Name,
    string VisitFrequency,
    string Coverage,
    string Guarantee,
    decimal MonthlyFee,
    List<string> IncludedFeatures,
    int NoticePeriodDays,
    decimal EarlyTerminationFee);

/// <summary>What <see cref="ContractTool.SubscribeToGreenCarePlan"/> returns: the plan just opened, or the error saying that the customer already holds one.</summary>
public record SubscriptionResult(
    string? Error = null,
    string? ExistingContractId = null,
    string? ContractId = null,
    string? CustomerCode = null,
    string? PlanCode = null,
    string? PlanName = null,
    string? Status = null,
    string? StartDate = null,
    string? EndDate = null,
    decimal? MonthlyFee = null,
    string? VisitDays = null,
    string? InvoiceId = null,
    string? Note = null);

/// <summary>What <see cref="ContractTool.GetContractDetails"/> returns: the customer's plan in full, or the error saying that none is held.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record ContractDetailsResult(
    string? Error = null,
    string? ContractId = null,
    string? CustomerCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName = null,
    ContractStatus? Status = null,
    ContractPlan? Plan = null,
    ContractPeriod? ContractPeriod = null,
    ContractFee? Fee = null,
    List<ContractService>? Services = null,
    ContractTermination? Termination = null,
    List<ClauseListing>? AvailableClauses = null,
    string? Note = null);

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
    string? Error = null,
    string? CustomerCode = null,
    string? CustomerName = null,
    string? Note = null,
    string? ContractId = null,
    int? ClauseNumber = null,
    string? Title = null,
    string? Type = null,
    string? Summary = null,
    string? FullText = null,
    string? RelatedInfo = null,
    int? RequestedClauseNumber = null,
    List<ClauseHeading>? AvailableClauses = null);

/// <summary>What <see cref="ContractTool.GetVisitSchedule"/> returns: the tending calendar of the customer, or the error saying that no plan is held.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record VisitScheduleResult(
    string? Error = null,
    string? ContractId = null,
    string? CustomerCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName = null,
    string? VisitFrequency = null,
    string? VisitDays = null,
    VisitAllowance? Allowance = null,
    List<UpcomingVisit>? Upcoming = null,
    List<RecentVisit>? RecentVisits = null,
    string? Note = null);

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
    string? Error = null,
    string? ContractId = null,
    string? CustomerCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName = null,
    string? Reason = null,
    NoticePeriod? NoticePeriod = null,
    TerminationFees? Fees = null,
    TerminationProcedure? Procedure = null,
    string? RefundPolicy = null,
    List<string>? ImportantNotes = null,
    string? Note = null);

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
