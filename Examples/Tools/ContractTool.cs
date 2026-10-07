using System.ComponentModel;
using System.Globalization;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;

namespace Examples.Tools;

/// <summary>
/// The Green Care Plan agent of The Greenhouse &amp; Nursery: the garden-care contract a customer
/// signs alongside their plants — tending visits, coverage, plant-health guarantee, fees, clauses
/// and termination. Reads the same shared database the greenhouse ledger writes (see
/// <see cref="GreenhouseDatabaseHelper"/>): the plan's terms are the shop's, the schedule is the
/// customer's and the monthly fee it sets is the one BillingTool's invoices charge. Reads for
/// every existing plan; the one dispositive action it has is enrolling a customer in a NEW plan
/// (see <see cref="SubscribeToGreenCarePlan"/>), which bills through the same shared backoffice
/// path <see cref="InventoryTool"/>'s ConfirmOrder uses — see <see cref="GreenhouseDatabaseHelper.BillCustomerAsync"/>.
/// </summary>
[ProvidesToolForIntent("contract")]
public class ContractTool : MorganaTool
{
    public ContractTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
        GreenhouseDatabaseHelper.Ensure();
    }

    // =========================================================================
    // ROWS AND LOOKUPS
    // =========================================================================

    // The plan is two things at once and the database keeps them apart: the PRODUCT (one row in
    // PlanProducts with its features, services, clauses, termination steps and documents — the
    // same terms for everyone who signs it) and the SCHEDULE (one row in CarePlans per customer:
    // their contract number, their dates, their fee). Everything below reads that pairing.
    private record CarePlanSchedule(
        string ContractId,
        string CustomerCode,
        string PlanCode,
        DateTime StartDate,
        DateTime EndDate,
        string Status,
        string BillingCycle,
        decimal MonthlyFee,
        string VisitDays);

    private record PlanProduct(
        string PlanCode,
        string Name,
        string VisitFrequency,
        string Coverage,
        string Guarantee,
        decimal MonthlyFee,
        int IncludedVisitsPerMonth,
        decimal ExtraVisitFee,
        int NoticePeriodDays,
        decimal EarlyTerminationFee,
        string RefundPolicy);

    private record PlanClause(
        int ClauseNumber,
        string Title,
        string Summary,
        string FullText,
        string ClauseType);

    private record PlanService(
        string ServiceId,
        string Name,
        string Description,
        decimal MonthlyCost,
        bool IsOptional);

    private static async Task<string?> FindCustomerNameAsync(SqliteConnection connection, string customerCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM Customers WHERE CustomerCode = $customerCode COLLATE NOCASE";
        command.Parameters.AddWithValue("$customerCode", customerCode);

        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<CarePlanSchedule?> FindScheduleAsync(SqliteConnection connection, string customerCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ContractId, CustomerCode, PlanCode, StartDate, EndDate, Status, BillingCycle, MonthlyFee, VisitDays FROM CarePlans WHERE CustomerCode = $customerCode COLLATE NOCASE ORDER BY StartDate DESC";
        command.Parameters.AddWithValue("$customerCode", customerCode);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new CarePlanSchedule(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(5),
            reader.GetString(6),
            (decimal)reader.GetDouble(7),
            reader.GetString(8));
    }

    private static async Task<PlanProduct> GetPlanProductAsync(SqliteConnection connection, string planCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT PlanCode, Name, VisitFrequency, Coverage, Guarantee, MonthlyFee, IncludedVisitsPerMonth, ExtraVisitFee, NoticePeriodDays, EarlyTerminationFee, RefundPolicy FROM PlanProducts WHERE PlanCode = $planCode";
        command.Parameters.AddWithValue("$planCode", planCode);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"Plan product '{planCode}' is referenced by a care plan but missing from the database.");

        return new PlanProduct(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            (decimal)reader.GetDouble(5),
            reader.GetInt32(6),
            (decimal)reader.GetDouble(7),
            reader.GetInt32(8),
            (decimal)reader.GetDouble(9),
            reader.GetString(10));
    }

    private static async Task<List<string>> GetTextColumnAsync(SqliteConnection connection, string sql, string planCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$planCode", planCode);

        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));

        return values;
    }

    private static Task<List<string>> GetFeaturesAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Feature FROM PlanFeatures WHERE PlanCode = $planCode ORDER BY Position", planCode);

    private static Task<List<string>> GetTerminationStepsAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Description FROM PlanTerminationSteps WHERE PlanCode = $planCode ORDER BY StepNumber", planCode);

    private static Task<List<string>> GetRequiredDocumentsAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Document FROM PlanRequiredDocuments WHERE PlanCode = $planCode ORDER BY Position", planCode);

    private static async Task<List<PlanService>> GetServicesAsync(SqliteConnection connection, string planCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ServiceId, Name, Description, MonthlyCost, IsOptional FROM PlanServices WHERE PlanCode = $planCode ORDER BY ServiceId";
        command.Parameters.AddWithValue("$planCode", planCode);

        List<PlanService> services = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            services.Add(new PlanService(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                (decimal)reader.GetDouble(3),
                reader.GetInt64(4) != 0));
        }

        return services;
    }

    private static async Task<List<PlanClause>> GetClausesAsync(SqliteConnection connection, string planCode, int? clauseNumber = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = clauseNumber == null
            ? "SELECT ClauseNumber, Title, Summary, FullText, ClauseType FROM PlanClauses WHERE PlanCode = $planCode ORDER BY ClauseNumber"
            : "SELECT ClauseNumber, Title, Summary, FullText, ClauseType FROM PlanClauses WHERE PlanCode = $planCode AND ClauseNumber = $clauseNumber";
        command.Parameters.AddWithValue("$planCode", planCode);
        if (clauseNumber != null)
            command.Parameters.AddWithValue("$clauseNumber", clauseNumber);

        List<PlanClause> clauses = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            clauses.Add(new PlanClause(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return clauses;
    }

    // No gate on the customer registry, here or anywhere else in this plugin: an unknown code is
    // simply a code no plan hangs from, which is the same answer a known customer without a plan
    // gets — SubscribeToGreenCarePlan takes either exactly the same way.
    private const string NoCarePlanError = "No care plan found";

    private const string NoCarePlanNote =
        "No Green Care Plan is held under this customer code: either none was ever opened under it, or the code is mistyped. This response carries nothing about what the plan offers or costs — no term, fee or figure of the plan can be read out of it. SubscribeToGreenCarePlan is the only tool that opens one.";

    // The only plan product the nursery currently offers. GetPlanProductAsync already reads any
    // PlanCode the schema might hold, so a second product would only need this constant to grow
    // into a parameter — nothing else here assumes there is exactly one.
    private const string DefaultPlanCode = "GREEN-CARE-PREMIUM";

    /// <summary>
    /// Spaces <paramref name="count"/> visit days as evenly as a 28-day month allows, anchored on
    /// the enrollment date — the same shape the seed data uses (e.g. "6,20", 14 days apart).
    /// </summary>
    private static string PickVisitDays(DateTime startDate, int count)
    {
        count = Math.Max(1, count);
        int step = 28 / count;
        int firstDay = Math.Clamp(startDate.Day, 1, 28);
        return string.Join(',', Enumerable.Range(0, count).Select(position => ((firstDay - 1 + position * step) % 28) + 1));
    }

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Retrieves the Green Care Plan's own terms — no customer code, no
    /// existing schedule required. What GetContractDetails cannot be for a prospect: every other
    /// read tool in this class needs a CarePlans row to hang off, which a customer deciding
    /// whether to sign up does not have yet. This is the ONE thing SubscribeToGreenCarePlan's
    /// restate-then-confirm step can ground its numbers in without already having enrolled them.
    /// </summary>
    /// <returns>The plan's name, coverage, guarantee, fee and included features.</returns>
    [Description("Retrieves the Green Care Plan's own terms as structured JSON — planCode, name, visitFrequency, coverage, guarantee, monthlyFee, includedFeatures, noticePeriodDays, earlyTerminationFee. Needs no customer code and no existing schedule: this is the ONE tool that answers what the plan itself offers and costs, for a prospect who does not hold one yet as much as for a customer who already does. Never answer a 'what does it include / what does it cost' question from GetContractDetails or from memory: always call this tool first.")]
    [RequiresApproval(false)]
    public async Task<PlanOverviewResult> GetPlanOverview()
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        PlanProduct product = await GetPlanProductAsync(connection, DefaultPlanCode);
        List<string> features = await GetFeaturesAsync(connection, DefaultPlanCode);

        return new PlanOverviewResult(
            product.PlanCode,
            product.Name,
            product.VisitFrequency,
            product.Coverage,
            product.Guarantee,
            product.MonthlyFee,
            features,
            product.NoticePeriodDays,
            product.EarlyTerminationFee);
    }

    /// <summary>
    /// Enrolls a customer in the Green Care Plan: opens a new CarePlans schedule and bills the
    /// first month's fee immediately, atomically with it — see GreenhouseDatabaseHelper.BillCustomerAsync,
    /// the same backoffice write path InventoryTool.ConfirmOrder bills a confirmed order through.
    /// </summary>
    /// <param name="customerCode">Customer code enrolling (retrieved from shared context).</param>
    /// <returns>The new contractId, the plan's terms and the invoice it was billed to, or the error saying that a plan is already held.</returns>
    [Description("Enrolls a customer in the Green Care Plan: opens a new contract and bills its first month's fee in the same action. Returns structured JSON: contractId, planCode, planName, status ('Active'), startDate, endDate, monthlyFee, visitDays, invoiceId, note. invoiceId identifies the invoice this enrollment was just billed to — the accounts agent (Billing), never you, is where the customer sees its total or line items. Fails with an error and the existingContractId if the customer already holds an Active or PendingRenewal plan — no code is ever rejected for being unregistered. This tool has dispositive capabilities: it commits a real, persistent change to the shop's books and to the customer's account.")]
    [RequiresApproval(true)]
    public async Task<SubscriptionResult> SubscribeToGreenCarePlan(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.Context, shared: true)] string customerCode)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // One active (or renewing) plan per customer at a time: a domain rule about what a Green
        // Care Plan IS, not a gate on whether the customer code is real — see NoCarePlan's remark.
        CarePlanSchedule? existing = await FindScheduleAsync(connection, customerCode);
        if (existing != null && existing.Status is "Active" or "PendingRenewal")
        {
            return new SubscriptionResult(
                Error: "Customer already has an active Green Care Plan",
                ExistingContractId: existing.ContractId,
                Status: existing.Status,
                Note: "Only one Green Care Plan may be active per customer code at a time and this code already holds one: nothing was opened and nothing was billed. GetContractDetails reads the existing plan, GetTerminationProcedure describes how one is ended.");
        }

        PlanProduct product = await GetPlanProductAsync(connection, DefaultPlanCode);

        DateTime startDate = DateTime.UtcNow.Date;
        DateTime endDate = startDate.AddMonths(12);
        string contractId = $"GCP-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        string visitDays = PickVisitDays(startDate, product.IncludedVisitsPerMonth);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await using (SqliteCommand insertPlan = connection.CreateCommand())
        {
            insertPlan.Transaction = transaction;
            insertPlan.CommandText = """
                INSERT INTO CarePlans (ContractId, CustomerCode, PlanCode, StartDate, EndDate, Status, BillingCycle, MonthlyFee, VisitDays)
                VALUES ($contractId, $customerCode, $planCode, $startDate, $endDate, 'Active', 'Monthly', $monthlyFee, $visitDays)
                """;
            insertPlan.Parameters.AddWithValue("$contractId", contractId);
            insertPlan.Parameters.AddWithValue("$customerCode", customerCode);
            insertPlan.Parameters.AddWithValue("$planCode", DefaultPlanCode);
            insertPlan.Parameters.AddWithValue("$startDate", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertPlan.Parameters.AddWithValue("$endDate", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertPlan.Parameters.AddWithValue("$monthlyFee", (double)product.MonthlyFee);
            insertPlan.Parameters.AddWithValue("$visitDays", visitDays);
            await insertPlan.ExecuteNonQueryAsync();
        }

        string invoiceId = await GreenhouseDatabaseHelper.BillCustomerAsync(connection, transaction, customerCode,
            $"{product.Name} - Monthly Fee", null, null, (double)product.MonthlyFee, 1, startDate);

        await transaction.CommitAsync();

        toolLogger.LogInformation("Enrolled {CustomerCode} in Green Care Plan {PlanCode}: contract {ContractId}, billed to invoice {InvoiceId}", customerCode, DefaultPlanCode, contractId, invoiceId);

        return new SubscriptionResult(
            ContractId: contractId,
            CustomerCode: customerCode,
            PlanCode: DefaultPlanCode,
            PlanName: product.Name,
            Status: "Active",
            StartDate: startDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            EndDate: endDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            MonthlyFee: product.MonthlyFee,
            VisitDays: visitDays,
            InvoiceId: invoiceId,
            Note: "The plan is active and its first month has been billed to this invoice. This response carries the invoice identifier and nothing else about it: its total and its line items are not on these pages.");
    }

    /// <summary>
    /// Retrieves the customer's Green Care Plan in full.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <returns>The complete plan overview, or the error saying that no plan is held</returns>
    [Description("Retrieves comprehensive Green Care Plan information for that customer as structured JSON including: contractId, customerName, status (with icon), plan details (name, visit frequency, garden coverage, plant-health guarantee, included features), plan period (with remaining days/months), fee (amount and cycle), optional services array, termination basics and available clauses list. This tool has only informative capabilities: you will NOT find here dispositive actions on an EXISTING plan (e.g: 'Change plan', 'Book a visit', 'Terminate plan', ...). A code no plan hangs from is reported as having none — that is an answer, not a failure; enrolling one is SubscribeToGreenCarePlan's job, not this tool's and only once the customer asks.")]
    [RequiresApproval(false)]
    public async Task<ContractDetailsResult> GetContractDetails(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.Context, shared: true)] string customerCode)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        CarePlanSchedule? schedule = await FindScheduleAsync(connection, customerCode);
        if (schedule == null)
            return new ContractDetailsResult(Error: NoCarePlanError, CustomerCode: customerCode, CustomerName: customerName, Note: NoCarePlanNote);

        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        List<string> features = await GetFeaturesAsync(connection, schedule.PlanCode);
        List<PlanService> services = await GetServicesAsync(connection, schedule.PlanCode);
        List<PlanClause> clauses = await GetClausesAsync(connection, schedule.PlanCode);

        int remainingDays = (schedule.EndDate - DateTime.UtcNow).Days;

        return new ContractDetailsResult(
            ContractId: schedule.ContractId,
            CustomerCode: schedule.CustomerCode,
            CustomerName: customerName,
            Status: new ContractStatus(
                schedule.Status,
                schedule.Status switch
                {
                    "Active" => "✅",
                    "PendingRenewal" => "🔄",
                    "Expired" => "⏰",
                    "Terminated" => "❌",
                    "Suspended" => "⏸️",
                    _ => "📋"
                }),
            Plan: new ContractPlan(
                product.Name,
                product.VisitFrequency,
                product.Coverage,
                product.Guarantee,
                features),
            ContractPeriod: new ContractPeriod(
                schedule.StartDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                schedule.EndDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                remainingDays > 0 ? remainingDays : 0,
                remainingDays > 0 ? remainingDays / 30 : 0),
            Fee: new ContractFee(schedule.MonthlyFee, schedule.BillingCycle),
            Services: [.. services.Select(service => new ContractService(
                service.ServiceId,
                service.Name,
                service.Description,
                service.MonthlyCost,
                service.IsOptional,
                service.IsOptional ? "Optional" : "Required"))],
            Termination: new ContractTermination(
                product.NoticePeriodDays,
                product.EarlyTerminationFee,
                new AutoRenewal(true, 60, schedule.EndDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))),
            AvailableClauses: [.. clauses.Select(clause => new ClauseListing(
                clause.ClauseNumber,
                clause.Title,
                clause.ClauseType))]);
    }

    /// <summary>
    /// Retrieves a single clause of the customer's Green Care Plan.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <param name="clauseNumber">Clause number to retrieve (1-7)</param>
    /// <returns>The complete clause details, or the error saying why none can be read</returns>
    [Description("Retrieves a specific plan clause as structured JSON including: clauseNumber, title, type, summary, fullText and relatedInfo. The plan has 7 clauses: (1) Plant Health Guarantee, (2) Payment Terms, (3) Visit Schedule and Rescheduling, (4) Termination Terms, (5) Auto-Renewal, (6) Limitation of Liability, (7) Data Privacy (GDPR). The clauses are the plan's own terms, identical for everyone who signs it; the dates and fees specific to this customer are in GetContractDetails. This tool has only informative capabilities: you will NOT find here dispositive actions (e.g: 'Modify clause', ...).")]
    [RequiresApproval(false)]
    public async Task<ContractClauseResult> GetContractClause(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.Context, shared: true)] string customerCode,
        [Description("Clause number to retrieve (1-7). The user should specify which clause they want to read, or you should present the available clause topics and ask them to choose. Do NOT guess or assume which clause the user wants.")] [ToolParameter(Records.ToolScope.Request)] int clauseNumber)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        CarePlanSchedule? schedule = await FindScheduleAsync(connection, customerCode);
        if (schedule == null)
            return new ContractClauseResult(Error: NoCarePlanError, CustomerCode: customerCode, CustomerName: customerName, Note: NoCarePlanNote);

        List<PlanClause> clauses = await GetClausesAsync(connection, schedule.PlanCode, clauseNumber);
        if (clauses.Count == 0)
        {
            List<PlanClause> allClauses = await GetClausesAsync(connection, schedule.PlanCode);
            return new ContractClauseResult(
                Error: "Clause not found",
                RequestedClauseNumber: clauseNumber,
                AvailableClauses: [.. allClauses.Select(clause => new ClauseHeading(clause.ClauseNumber, clause.Title))]);
        }

        PlanClause found = clauses[0];

        return new ContractClauseResult(
            ContractId: schedule.ContractId,
            ClauseNumber: found.ClauseNumber,
            Title: found.Title,
            Type: found.ClauseType,
            Summary: found.Summary,
            FullText: found.FullText,
            RelatedInfo: found.ClauseType switch
            {
                "Termination" => "For termination procedures, use GetTerminationProcedure tool",
                "VisitSchedule" => "Extra visits are charged on the customer's invoices, which another bench of the nursery keeps: no tool here reads them",
                "PlantHealth" => "For the dates this guarantee runs against, use GetContractDetails tool",
                _ => null
            });
    }

    /// <summary>
    /// Retrieves the customer's tending calendar: the visits already made and when the next ones fall.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <returns>The recent visits, the upcoming dates and this month's allowance, or the error saying that no plan is held</returns>
    [Description("Retrieves the customer's tending calendar as structured JSON: visitFrequency, visitDays, allowance (includedPerMonth, takenThisMonth, remainingThisMonth, extraVisitFee), upcoming (the next dates, computed from the plan's own visit days) and recentVisits (date, kind Included/Extra, outcome Completed/Missed, notes and — where an extra visit was charged — the invoiceId it appears on). This tool has only informative capabilities: it does NOT book, move or cancel a visit and no tool here does.")]
    [RequiresApproval(false)]
    public async Task<VisitScheduleResult> GetVisitSchedule(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.Context, shared: true)] string customerCode)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        CarePlanSchedule? schedule = await FindScheduleAsync(connection, customerCode);
        if (schedule == null)
            return new VisitScheduleResult(Error: NoCarePlanError, CustomerCode: customerCode, CustomerName: customerName, Note: NoCarePlanNote);

        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        DateTime today = DateTime.UtcNow.Date;

        // Only visits that have actually happened are read from the table. What is still to come is
        // COMPUTED from the plan's own visit days, never stored: a seeded calendar of future dates
        // is stale the day after it is written, while a recurrence rule is right forever.
        List<RecentVisit> recent = [];
        int takenThisMonth = 0;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT VisitId, VisitDate, Kind, Outcome, Notes, InvoiceId FROM PlanVisits WHERE ContractId = $contractId AND VisitDate <= $today ORDER BY VisitDate DESC LIMIT 8";
            command.Parameters.AddWithValue("$contractId", schedule.ContractId);
            command.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                DateTime visitDate = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                string kind = reader.GetString(2);
                string outcome = reader.GetString(3);

                if (visitDate.Year == today.Year && visitDate.Month == today.Month && kind == "Included" && outcome == "Completed")
                    takenThisMonth++;

                recent.Add(new RecentVisit(
                    reader.GetString(0),
                    visitDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    kind,
                    outcome,
                    outcome == "Completed" ? "\u2705" : "\u26A0\uFE0F",
                    reader.GetString(4),
                    kind == "Extra",
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        return new VisitScheduleResult(
            ContractId: schedule.ContractId,
            CustomerName: customerName,
            VisitFrequency: product.VisitFrequency,
            VisitDays: schedule.VisitDays,
            Allowance: new VisitAllowance(
                product.IncludedVisitsPerMonth,
                takenThisMonth,
                Math.Max(0, product.IncludedVisitsPerMonth - takenThisMonth),
                product.ExtraVisitFee),
            Upcoming: [.. NextVisitDates(schedule.VisitDays, today, 3).Select(date => new UpcomingVisit(
                date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                (date - today).Days,
                "Included"))],
            RecentVisits: recent);
    }

    /// <summary>
    /// The next <paramref name="count"/> occurrences of the contract's visit days, strictly after
    /// <paramref name="today"/>.
    /// </summary>
    /// <remarks>
    /// A day the month is too short for is simply skipped rather than clamped: a plan tended on the
    /// 30th is not tended on the 28th of February just because the calendar is shorter.
    /// </remarks>
    private static List<DateTime> NextVisitDates(string visitDays, DateTime today, int count)
    {
        int[] days = visitDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .OrderBy(day => day)
            .ToArray();

        List<DateTime> dates = [];
        DateTime month = new DateTime(today.Year, today.Month, 1);

        while (dates.Count < count)
        {
            foreach (int day in days)
            {
                if (day > DateTime.DaysInMonth(month.Year, month.Month))
                    continue;

                DateTime candidate = new DateTime(month.Year, month.Month, day);
                if (candidate > today && dates.Count < count)
                    dates.Add(candidate);
            }

            month = month.AddMonths(1);
        }

        return dates;
    }

    /// <summary>
    /// Provides the step-by-step termination procedure of the customer's plan.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from context)</param>
    /// <param name="reason">Optional termination reason for internal tracking</param>
    /// <returns>The complete termination guide, or the error saying that no plan is held</returns>
    [Description("Retrieves the plan termination procedure as structured JSON including: contractId, noticePeriod (requiredDays, earliestEffectiveDate), fees (earlyTermination amount and applicability, waiverEligibility), procedure (steps array, requiredDocuments array), refundPolicy and importantNotes. This tool has only informative capabilities: you will NOT find here dispositive actions (e.g: 'Terminate plan', ...) — it explains the steps, it does not take them.")]
    [RequiresApproval(false)]
    public async Task<TerminationProcedureResult> GetTerminationProcedure(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.Context, shared: true)] string customerCode,
        [Description("Optional reason for considering termination (e.g., 'moving house', 'tending the garden myself', 'unhappy with the visits'). This is recorded for internal purposes but is not required. Ask the user if they'd like to provide a reason, but make it clear it's optional.")] [ToolParameter(Records.ToolScope.Request)] string? reason = null)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        CarePlanSchedule? schedule = await FindScheduleAsync(connection, customerCode);
        if (schedule == null)
            return new TerminationProcedureResult(Error: NoCarePlanError, CustomerCode: customerCode, CustomerName: customerName, Note: NoCarePlanNote);

        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        List<string> steps = await GetTerminationStepsAsync(connection, schedule.PlanCode);
        List<string> documents = await GetRequiredDocumentsAsync(connection, schedule.PlanCode);

        DateTime earliestTerminationDate = DateTime.UtcNow.AddDays(product.NoticePeriodDays);
        bool earlyTermination = earliestTerminationDate < schedule.EndDate;

        return new TerminationProcedureResult(
            ContractId: schedule.ContractId,
            CustomerName: customerName,
            Reason: reason ?? "Not specified",
            NoticePeriod: new NoticePeriod(
                product.NoticePeriodDays,
                earliestTerminationDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            Fees: new TerminationFees(
                new EarlyTerminationFee(
                    earlyTermination,
                    earlyTermination ? product.EarlyTerminationFee : 0m,
                    earlyTermination
                        ? $"Plan runs to {schedule.EndDate:dd/MM/yyyy}, termination before this date incurs fee"
                        : "No early termination fee (plan expired or within normal period)"),
                new WaiverEligibility(
                    true,
                    [
                        "Relocation outside the service area (proof required)",
                        "Three consecutive months with more than half the scheduled visits missed by the nursery"
                    ])),
            Procedure: new TerminationProcedure(
                [.. steps.Select((step, index) => new TerminationStep(index + 1, step))],
                documents),
            RefundPolicy: product.RefundPolicy,
            ImportantNotes:
            [
                "Termination request must be submitted in writing",
                "All outstanding invoices must be settled before termination",
                "Leased equipment must be returned to avoid replacement charges",
                "The plant health guarantee lapses on the termination effective date"
            ]);
    }
}
