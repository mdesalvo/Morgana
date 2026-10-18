using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="ContractTool.GetPlanOverview"/> returns: the terms of the Green Care Plan itself.</summary>
[Description("The terms of the Green Care Plan itself.")]
public record PlanOverviewResult(
    [Description("The plan product code.")] string PlanCode,
    [Description("The plan product name.")] string Name,
    [Description("How often the garden is tended.")] string VisitFrequency,
    [Description("What the plan covers.")] string Coverage,
    [Description("The plant-health guarantee that the plan gives.")] string Guarantee,
    [Description("The monthly fee in euros.")] decimal MonthlyFee,
    [Description("The features that the plan includes.")] List<string> IncludedFeatures,
    [Description("The days of notice that ending the plan requires.")] int NoticePeriodDays,
    [Description("The fee in euros for ending the plan before its end date.")] decimal EarlyTerminationFee);

/// <summary>What <see cref="ContractTool.SubscribeToGreenCarePlan"/> returns: the plan just opened, or the error saying that the customer already holds one.</summary>
[Description("The plan just opened for a customer.")]
public record SubscriptionResult(
    [Description("Why no plan was opened; present only when the customer already holds one.")] string? Error = null,
    [Description("The contract that the customer already holds; present only on an error.")] string? ExistingContractId = null,
    [Description("The identifier of the new contract.")] string? ContractId = null,
    [Description("The customer that was enrolled.")] string? CustomerCode = null,
    [Description("The plan product code.")] string? PlanCode = null,
    [Description("The plan product name.")] string? PlanName = null,
    [Description("The status of the new contract, which is Active.")] string? Status = null,
    [Description("The date the contract starts, as dd/MM/yyyy.")] string? StartDate = null,
    [Description("The date the contract ends, as dd/MM/yyyy.")] string? EndDate = null,
    [Description("The monthly fee in euros that was billed.")] decimal? MonthlyFee = null,
    [Description("The days of the month on which visits fall, separated by commas.")] string? VisitDays = null,
    [Description("The invoice that the first month was billed to.")] string? InvoiceId = null,
    [Description("States what this response does and does not carry.")] string? Note = null);

/// <summary>What <see cref="ContractTool.GetContractDetails"/> returns: the customer's plan in full, or the error saying that none is held.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
[Description("The Green Care Plan that a customer holds, in full.")]
public record ContractDetailsResult(
    [Description("Why no plan is returned; present only when none is held.")] string? Error = null,
    [Description("The contract identifier.")] string? ContractId = null,
    [Description("The customer the contract belongs to.")] string? CustomerCode = null,
    [Description("Where the contract stands.")] ContractStatus? Status = null,
    [Description("The plan product that the contract is an instance of.")] ContractPlan? Plan = null,
    [Description("The dates that the contract runs over.")] ContractPeriod? ContractPeriod = null,
    [Description("The fee that the contract charges.")] ContractFee? Fee = null,
    [Description("The services of the plan.")] List<ContractService>? Services = null,
    [Description("How the contract ends and renews.")] ContractTermination? Termination = null,
    [Description("The clauses that the plan has.")] List<ClauseListing>? AvailableClauses = null,
    [Description("States what this response does and does not carry.")] string? Note = null);

/// <summary>The status of a contract with its icon.</summary>
[Description("The status of a contract.")]
public record ContractStatus(
    [Description("The status: Active, PendingRenewal, Expired, Terminated or Suspended.")] string Value,
    [Description("The icon that goes with the status.")] string Icon);

/// <summary>The plan product a contract is an instance of.</summary>
[Description("The plan product that a contract is an instance of.")]
public record ContractPlan(
    [Description("The plan product name.")] string Name,
    [Description("How often the garden is tended.")] string VisitFrequency,
    [Description("What the plan covers.")] string Coverage,
    [Description("The plant-health guarantee that the plan gives.")] string Guarantee,
    [Description("The features that the plan includes.")] List<string> IncludedFeatures);

/// <summary>The dates a contract runs over and what is left of them.</summary>
[Description("The dates that a contract runs over and what is left of them.")]
public record ContractPeriod(
    [Description("The date the contract starts, as dd/MM/yyyy.")] string StartDate,
    [Description("The date the contract ends, as dd/MM/yyyy.")] string EndDate,
    [Description("How many days are left before the end date; zero once it has passed.")] int RemainingDays,
    [Description("How many whole months are left before the end date; zero once it has passed.")] int RemainingMonths);

/// <summary>The monthly fee of a contract and how it is billed.</summary>
[Description("The fee that a contract charges.")]
public record ContractFee(
    [Description("The monthly fee in euros.")] decimal MonthlyFee,
    [Description("How often the fee is billed, which is Monthly.")] string BillingCycle);

/// <summary>One service of the plan.</summary>
[Description("One service of the plan.")]
public record ContractService(
    [Description("The service identifier.")] string ServiceId,
    [Description("The service name.")] string Name,
    [Description("What the service does.")] string Description,
    [Description("What the service costs per month in euros.")] decimal MonthlyCost,
    [Description("True when the customer may leave the service out.")] bool IsOptional,
    [Description("Whether the service is Optional or Required.")] string Category);

/// <summary>How a contract ends and what renews it.</summary>
[Description("How a contract ends and renews.")]
public record ContractTermination(
    [Description("The days of notice that ending the contract requires.")] int NoticePeriodDays,
    [Description("The fee in euros for ending the contract before its end date.")] decimal EarlyTerminationFee,
    [Description("How the contract renews by itself.")] AutoRenewal AutoRenewal);

/// <summary>The automatic renewal of a contract.</summary>
[Description("The automatic renewal of a contract.")]
public record AutoRenewal(
    [Description("True when the contract renews by itself.")] bool Enabled,
    [Description("The days before the renewal date by which the customer must give notice to stop it.")] int NoticeDays,
    [Description("The date the contract renews, as dd/MM/yyyy.")] string RenewalDate);

/// <summary>One clause of the plan as a list shows it.</summary>
[Description("One clause of the plan in a list.")]
public record ClauseListing(
    [Description("The number of the clause, from 1 to 7.")] int ClauseNumber,
    [Description("The title of the clause.")] string Title,
    [Description("The subject of the clause: PlantHealth, Payment, VisitSchedule, Termination, RenewalTerms, Liability or Privacy.")] string Type);

/// <summary>One clause of the plan as the list of what exists shows it.</summary>
[Description("One clause of the plan that exists.")]
public record ClauseHeading(
    [Description("The number of the clause, from 1 to 7.")] int ClauseNumber,
    [Description("The title of the clause.")] string Title);

/// <summary>What <see cref="ContractTool.GetContractClause"/> returns: one clause, or the error saying that no plan is held or that the clause does not exist.</summary>
[Description("One clause of the plan.")]
public record ContractClauseResult(
    [Description("Why no clause is returned; present only when no plan is held or the clause does not exist.")] string? Error = null,
    [Description("The customer code the books were asked about; present only on an error.")] string? CustomerCode = null,
    [Description("States what this response does and does not carry.")] string? Note = null,
    [Description("The contract identifier.")] string? ContractId = null,
    [Description("The number of the clause, from 1 to 7.")] int? ClauseNumber = null,
    [Description("The title of the clause.")] string? Title = null,
    [Description("The subject of the clause: PlantHealth, Payment, VisitSchedule, Termination, RenewalTerms, Liability or Privacy.")] string? Type = null,
    [Description("The clause in a few words.")] string? Summary = null,
    [Description("The full text of the clause.")] string? FullText = null,
    [Description("Where a matter related to the clause is read; null when there is none.")] string? RelatedInfo = null,
    [Description("The clause number that was asked for; present only on an error.")] int? RequestedClauseNumber = null,
    [Description("The clauses that exist; present only on an error.")] List<ClauseHeading>? AvailableClauses = null);

/// <summary>What <see cref="ContractTool.GetVisitSchedule"/> returns: the tending calendar of the customer, or the error saying that no plan is held.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
[Description("The tending calendar of a customer.")]
public record VisitScheduleResult(
    [Description("Why no calendar is returned; present only when no plan is held.")] string? Error = null,
    [Description("The contract identifier.")] string? ContractId = null,
    [Description("The customer code the books were asked about; present only on an error.")] string? CustomerCode = null,
    [Description("How often the garden is tended.")] string? VisitFrequency = null,
    [Description("The days of the month on which visits fall, separated by commas.")] string? VisitDays = null,
    [Description("The visits that the plan includes each month and how many are used.")] VisitAllowance? Allowance = null,
    [Description("The next visits, soonest first.")] List<UpcomingVisit>? Upcoming = null,
    [Description("The visits already made, most recent first.")] List<RecentVisit>? RecentVisits = null,
    [Description("States what this response does and does not carry.")] string? Note = null);

/// <summary>The visits included in a month and how many are left.</summary>
[Description("The visits that the plan includes in a month and how many are left.")]
public record VisitAllowance(
    [Description("How many visits the plan includes each month.")] int IncludedPerMonth,
    [Description("How many included visits this month has already had.")] int TakenThisMonth,
    [Description("How many included visits are left this month.")] int RemainingThisMonth,
    [Description("What an extra visit costs in euros.")] decimal ExtraVisitFee);

/// <summary>A visit still to come.</summary>
[Description("A visit still to come.")]
public record UpcomingVisit(
    [Description("The date of the visit, as dd/MM/yyyy.")] string Date,
    [Description("How many days away the visit is.")] int DaysAway,
    [Description("Whether the visit is Included in the plan or Extra.")] string Kind);

/// <summary>A visit already made.</summary>
/// <remarks>The invoice is written even when null, since a null says that the visit was not charged.</remarks>
[Description("A visit already made.")]
public record RecentVisit(
    [Description("The visit identifier.")] string VisitId,
    [Description("The date of the visit, as dd/MM/yyyy.")] string Date,
    [Description("Whether the visit was Included in the plan or Extra.")] string Kind,
    [Description("How the visit went: Completed or Missed.")] string Outcome,
    [Description("The icon that goes with the outcome.")] string OutcomeIcon,
    [Description("The notes that the gardener took.")] string Notes,
    [Description("True when the visit was billed on top of the fee.")] bool Charged,
    [Description("The invoice that the visit appears on; null when it was not charged.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InvoiceId);

/// <summary>What <see cref="ContractTool.GetTerminationProcedure"/> returns: how the plan is ended, or the error saying that no plan is held.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
[Description("How a customer's plan is ended.")]
public record TerminationProcedureResult(
    [Description("Why no procedure is returned; present only when no plan is held.")] string? Error = null,
    [Description("The contract identifier.")] string? ContractId = null,
    [Description("The customer code the books were asked about; present only on an error.")] string? CustomerCode = null,
    [Description("The reason for ending the plan that was given; Not specified when none was.")] string? Reason = null,
    [Description("The notice that ending the plan requires.")] NoticePeriod? NoticePeriod = null,
    [Description("What ending the plan costs.")] TerminationFees? Fees = null,
    [Description("The steps to follow and the documents to hold.")] TerminationProcedure? Procedure = null,
    [Description("The refund policy of the plan.")] string? RefundPolicy = null,
    [Description("The conditions that apply to a termination.")] List<string>? ImportantNotes = null,
    [Description("States what this response does and does not carry.")] string? Note = null);

/// <summary>The notice that ending a plan requires.</summary>
[Description("The notice that ending a plan requires.")]
public record NoticePeriod(
    [Description("The days of notice required.")] int RequiredDays,
    [Description("The earliest date the termination can take effect, as dd/MM/yyyy.")] string EarliestEffectiveDate);

/// <summary>What ending a plan costs and when the cost is waived.</summary>
[Description("What ending a plan costs and when the cost is waived.")]
public record TerminationFees(
    [Description("The fee for ending the plan before its end date.")] EarlyTerminationFee EarlyTermination,
    [Description("Whether the fee can be waived.")] WaiverEligibility WaiverEligibility);

/// <summary>The fee for ending a plan before its end date.</summary>
[Description("The fee for ending a plan before its end date.")]
public record EarlyTerminationFee(
    [Description("True when ending the plan now incurs the fee.")] bool Applicable,
    [Description("The fee in euros; zero when it does not apply.")] decimal Amount,
    [Description("Why the fee applies or does not.")] string Reason);

/// <summary>Whether the early termination fee can be waived and on what conditions.</summary>
[Description("Whether the early termination fee can be waived.")]
public record WaiverEligibility(
    [Description("True when a waiver can be granted.")] bool Available,
    [Description("The conditions under which a waiver is granted.")] List<string> Conditions);

/// <summary>The steps and documents for ending a plan.</summary>
[Description("The steps and documents for ending a plan.")]
public record TerminationProcedure(
    [Description("The steps to follow, in order.")] List<TerminationStep> Steps,
    [Description("The documents that the request requires.")] List<string> RequiredDocuments);

/// <summary>One numbered step of the termination procedure.</summary>
[Description("One step of the termination procedure.")]
public record TerminationStep(
    [Description("The position of the step, starting at 1.")] int StepNumber,
    [Description("What the step asks for.")] string Description);
